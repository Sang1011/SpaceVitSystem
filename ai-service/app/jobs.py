"""Quản lý job: một worker thread, hàng đợi có ưu tiên, tiến độ, transcript từng phần, hủy job.

Trạng thái "nguồn sự thật" của job nằm ở backend (Postgres). Service này chỉ giữ hàng đợi trong RAM và ghi kết
quả đã xong ra {MEDIA_ROOT}/results/ để sống sót khi restart. Job đang chạy/đang chờ mà service restart sẽ mất;
backend thấy 404 khi polling thì submit lại (an toàn vì job_id do backend tạo, submit là idempotent).

Luồng live (app/live.py) dùng CHUNG worker thread này: việc live luôn được ưu tiên, và chen vào giữa các lô decode
của job upload đang chạy (hook `on_idle`), nên chỉ có một decode tại một thời điểm trên GPU. Phần tóm tắt Gemini
chạy ở thread riêng để không chặn việc live.
"""
from __future__ import annotations

import bisect
import json
import logging
import os
import threading
import time
import traceback
from concurrent.futures import Future, ThreadPoolExecutor, TimeoutError as FutureTimeout
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional

from .pipeline import JobCancelled, audio_seconds

log = logging.getLogger("ai.jobs")

TERMINAL = frozenset({"COMPLETED", "PARTIAL_FAILED", "FAILED", "CANCELLED"})
HAS_RESULT = frozenset({"COMPLETED", "PARTIAL_FAILED"})
UPLOAD_PRIORITY = 10  # số nhỏ chạy trước


class JobError(ValueError):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def resolve_media(root: Path, key: str) -> Path:
    """media_key là đường dẫn TƯƠNG ĐỐI trong MEDIA_ROOT. Chặn path traversal và đường dẫn tuyệt đối."""
    norm = (key or "").replace("\\", "/")
    if not norm or norm.startswith("/") or ".." in norm.split("/") or ":" in norm:
        raise JobError("MEDIA_KEY_INVALID", "media_key không hợp lệ")
    root_r = root.resolve()
    path = (root_r / norm).resolve()
    if root_r not in path.parents:
        raise JobError("MEDIA_KEY_INVALID", "media_key nằm ngoài thư mục media")
    if not path.is_file():
        raise JobError("MEDIA_NOT_FOUND", "Không tìm thấy file media")
    return path


@dataclass
class Job:
    job_id: str
    mode: str                      # transcribe | summarize | resummarize
    language: str
    output_language: str = "vi"
    media_key: Optional[str] = None
    input_segments: Optional[list] = None   # chỉ dùng cho resummarize
    priority: int = UPLOAD_PRIORITY
    status: str = "QUEUED"
    progress: float = 0.0
    error: Optional[str] = None
    error_code: Optional[str] = None
    created_at: float = field(default_factory=time.time)
    started_at: Optional[float] = None
    finished_at: Optional[float] = None
    audio_sec: Optional[float] = None
    speech_sec: Optional[float] = None
    decoded_sec: Optional[float] = None
    segments: list = field(default_factory=list)
    summary: Optional[dict] = None
    summary_error: Optional[str] = None
    stages: dict = field(default_factory=dict)
    config: dict = field(default_factory=dict)
    cancel_requested: bool = False

    def to_dict(self) -> dict:
        d = asdict(self)
        d.pop("input_segments", None)  # đã nằm trong segments của kết quả, không cần lưu hai lần
        return d

    @staticmethod
    def from_dict(d: dict) -> "Job":
        known = {k: v for k, v in d.items() if k in Job.__dataclass_fields__}
        return Job(**known)


class JobManager:
    def __init__(
        self, media_root: Path, max_audio_sec: float, engine_factory: Callable[[], Any],
        summarizer: Any, probe_duration: Callable[[Path], float] = audio_seconds,
        result_ttl_days: int = 7,
    ):
        self.media_root = media_root
        self.results_dir = media_root / "results"
        self.max_audio_sec = max_audio_sec
        self._engine_factory = engine_factory
        self._summarizer = summarizer
        self._probe = probe_duration
        self._ttl_days = result_ttl_days

        self._lock = threading.RLock()
        self._cv = threading.Condition(self._lock)
        self._jobs: dict[str, Job] = {}
        self._pending: list[tuple[int, int, str]] = []   # (priority, seq, job_id), luôn sắp xếp
        self._seq = 0
        self._running: Optional[str] = None
        self._engine = None
        self.engine_ready = False
        self.engine_error: Optional[str] = None
        self._stop = False
        self._live: Any = None
        self._next_engine_retry = 0.0
        self._summary_pool = ThreadPoolExecutor(max_workers=1, thread_name_prefix="summary")
        self._thread = threading.Thread(target=self._loop, name="ai-worker", daemon=True)

    # ------------------------------------------------------------------ live

    def attach_live(self, live: Any) -> None:
        self._live = live

    def submit_summary(self, segments: list, output_lang: str, duration: Optional[float]) -> Future:
        return self._summary_pool.submit(self._summarizer.summarize, segments, output_lang, duration)

    def wake(self) -> None:
        with self._cv:
            self._cv.notify_all()

    def _engine_available(self) -> bool:
        if self._engine is not None:
            return True
        if time.time() < self._next_engine_retry:  # nền: không thử nạp lại model liên tục
            return False
        ok = self._try_load_engine()
        if not ok:
            self._next_engine_retry = time.time() + 30
        return ok

    def _service_live(self, max_steps: int = 8) -> None:
        """Chạy tối đa max_steps đơn vị việc live. Được gọi từ vòng chính và từ giữa các lô decode của job upload."""
        if self._live is None or not self._engine_available():
            return
        for _ in range(max_steps):
            self._live.tick()
            if not self._live.has_work():
                return
            self._live.step(self._engine)

    # ------------------------------------------------------------------ vòng đời

    def start(self) -> None:
        try:
            self.results_dir.mkdir(parents=True, exist_ok=True)
            self._purge_old_results()
        except OSError:
            log.exception("Không tạo/dọn được %s", self.results_dir)
        self._thread.start()

    def stop(self) -> None:
        with self._cv:
            self._stop = True
            self._cv.notify_all()
        self._summary_pool.shutdown(wait=False, cancel_futures=True)

    def _purge_old_results(self) -> None:
        cutoff = time.time() - self._ttl_days * 86400
        for f in self.results_dir.glob("*.json"):
            if f.stat().st_mtime < cutoff:
                f.unlink(missing_ok=True)

    # ------------------------------------------------------------------ API cho tầng HTTP

    def submit(self, req: dict) -> tuple[Job, bool]:
        """Idempotent theo job_id. Trả (job, created)."""
        with self._cv:
            existing = self.get(req["job_id"])
            if existing is not None:
                return existing, False
            job = Job(
                job_id=req["job_id"], mode=req["mode"], language=req["language"],
                output_language=req.get("output_language", "vi"), media_key=req.get("media_key"),
                input_segments=req.get("segments"),
            )
            self._jobs[job.job_id] = job
            self._seq += 1
            bisect.insort(self._pending, (job.priority, self._seq, job.job_id))
            self._cv.notify()
            return job, True

    def get(self, job_id: str) -> Optional[Job]:
        with self._lock:
            job = self._jobs.get(job_id)
            if job is not None:
                return job
        return self._load_from_disk(job_id)

    def queue_position(self, job: Job) -> Optional[int]:
        with self._lock:
            for i, (_, _, jid) in enumerate(self._pending):
                if jid == job.job_id:
                    return i + 1
        return None

    def delete(self, job_id: str) -> Optional[str]:
        """Job đang chờ -> hủy ngay; đang chạy -> yêu cầu hủy; đã kết thúc -> xóa kết quả. None nếu không có."""
        with self._cv:
            job = self._jobs.get(job_id)
            if job is None:
                return "deleted" if self._remove_from_disk(job_id) else None
            if job.status == "QUEUED":
                self._pending = [p for p in self._pending if p[2] != job_id]
                self._finish(job, "CANCELLED")
                return "cancelled"
            if job.status in TERMINAL:
                self._jobs.pop(job_id, None)
                self._remove_from_disk(job_id)
                return "deleted"
            job.cancel_requested = True
            return "cancel_requested"

    def stats(self) -> dict:
        with self._lock:
            return {"queued": len(self._pending), "running": self._running}

    # ------------------------------------------------------------------ lưu trữ kết quả

    def _result_path(self, job_id: str) -> Path:
        return self.results_dir / f"{job_id}.json"

    def _persist(self, job: Job) -> None:
        try:
            self.results_dir.mkdir(parents=True, exist_ok=True)
            tmp = self._result_path(job.job_id).with_suffix(".tmp")
            tmp.write_text(json.dumps(job.to_dict(), ensure_ascii=False), encoding="utf-8")
            os.replace(tmp, self._result_path(job.job_id))
        except OSError:
            log.exception("Không lưu được kết quả job %s", job.job_id)

    def _load_from_disk(self, job_id: str) -> Optional[Job]:
        p = self._result_path(job_id)
        try:
            return Job.from_dict(json.loads(p.read_text(encoding="utf-8")))
        except (OSError, ValueError):
            return None

    def _remove_from_disk(self, job_id: str) -> bool:
        p = self._result_path(job_id)
        existed = p.exists()
        p.unlink(missing_ok=True)
        return existed

    # ------------------------------------------------------------------ worker

    def _try_load_engine(self) -> bool:
        """Nạp engine STT. Lỗi (CUDA/model/mạng) KHÔNG tự lùi về CPU vì sẽ chậm gấp nhiều lần mà không ai biết."""
        try:
            self._engine = self._engine_factory()
            self.engine_ready, self.engine_error = True, None
        except Exception as e:
            log.exception("Không khởi tạo được STT engine")
            self.engine_error = f"{type(e).__name__}: {e}"[:500]
        return self.engine_ready

    def _loop(self) -> None:
        self._try_load_engine()
        while True:
            live_ready = False
            with self._cv:
                if self._stop:
                    return
                live_ready = self._live is not None and self._live.has_work() and self._engine is not None
                if not self._pending and not live_ready:
                    self._cv.wait(timeout=0.5)  # thức định kỳ để các bộ đếm giờ của phiên live chạy
                if self._stop:
                    return
            if self._live is not None:
                self._live.tick()
                if self._live.has_work() and self._engine_available():
                    self._service_live()
                    continue  # việc live luôn trước job upload
            job = None
            with self._cv:
                if self._pending:
                    _, _, job_id = self._pending.pop(0)
                    job = self._jobs.get(job_id)
                    if job is None or job.status != "QUEUED":
                        continue
                    self._running = job_id
            if job is None:
                continue
            try:
                self._run(job)
            finally:
                with self._lock:
                    self._running = None

    def _set(self, job: Job, **fields) -> None:
        with self._lock:
            for k, v in fields.items():
                setattr(job, k, v)

    def _finish(self, job: Job, status: str, error: Optional[str] = None, code: Optional[str] = None) -> None:
        with self._lock:
            job.status = status
            job.error, job.error_code = error, code
            job.finished_at = time.time()
            if status == "COMPLETED":
                job.progress = 1.0
            if job.started_at:
                job.stages["total_sec"] = round(job.finished_at - job.started_at, 1)
        self._persist(job)

    def _run(self, job: Job) -> None:
        self._set(job, status="PREPROCESSING", started_at=time.time())
        stt_weight = 0.95 if job.mode == "summarize" else 1.0
        try:
            if job.mode == "resummarize":
                job.segments = list(job.input_segments or [])
                job.audio_sec = job.segments[-1]["end"] if job.segments else 0.0
            else:
                self._run_stt(job, stt_weight)
            if job.mode in ("summarize", "resummarize"):
                self._set(job, status="ANALYZING", progress=stt_weight if job.mode == "summarize" else 0.0)
                t = time.perf_counter()
                try:
                    job.summary = self._await_summary(job)
                except Exception as e:
                    log.exception("Tóm tắt thất bại (job %s)", job.job_id)
                    job.summary_error = f"{type(e).__name__}: {e}"[:500]
                job.stages["gemini_sec"] = round(time.perf_counter() - t, 1)
                if job.summary_error:
                    # Transcript vẫn dùng được; backend có thể chạy lại bằng mode=resummarize
                    self._finish(job, "PARTIAL_FAILED", job.summary_error, "SUMMARY_FAILED")
                    return
            self._finish(job, "COMPLETED")
        except JobCancelled:
            self._finish(job, "CANCELLED")
        except JobError as e:
            self._finish(job, "FAILED", str(e), e.code)
        except Exception as e:
            log.error("Job %s lỗi:\n%s", job.job_id, traceback.format_exc())
            self._finish(job, "FAILED", f"{type(e).__name__}: {e}"[:500], "STT_FAILED")

    def _await_summary(self, job: Job) -> dict:
        """Chạy tóm tắt ở thread riêng; trong lúc chờ vẫn phục vụ việc live."""
        fut = self.submit_summary(job.segments, job.output_language, job.audio_sec)
        while True:
            try:
                return fut.result(timeout=0.25)
            except FutureTimeout:
                self._service_live()

    def _run_stt(self, job: Job, stt_weight: float) -> None:
        if self._engine is None and not self._try_load_engine():  # tự thử lại mỗi job, khỏi phải restart container
            raise JobError("STT_ENGINE_UNAVAILABLE", f"STT engine chưa sẵn sàng: {self.engine_error}")
        path = resolve_media(self.media_root, job.media_key or "")
        duration = self._probe(path)
        if duration > self.max_audio_sec:
            raise JobError("AUDIO_TOO_LONG", f"Audio dài {duration / 60:.0f} phút, vượt giới hạn {self.max_audio_sec / 60:.0f} phút")
        job.audio_sec = duration
        job.config = dict(self._engine.config)

        def on_stage(name: str, info: dict) -> None:
            fields: dict = {"status": name}
            for k in ("audio_sec", "speech_sec", "decoded_sec"):
                if info.get(k) is not None:
                    fields[k] = info[k]
            self._set(job, **fields)

        def on_segment(start: float, end: float, text: str) -> None:
            with self._lock:
                job.segments.append({"start": round(start, 2), "end": round(end, 2), "text": text})
                if job.audio_sec:
                    job.progress = min(stt_weight, stt_weight * end / job.audio_sec)

        res = self._engine.transcribe_file(
            path, job.language, on_stage=on_stage, on_segment=on_segment,
            should_cancel=lambda: job.cancel_requested, on_idle=self._service_live,
        )
        with self._lock:
            job.segments = res["segments"]  # danh sách cuối đã sắp theo thời gian
            job.stages.update({k: round(v, 1) for k, v in res["stages"].items()})
            job.audio_sec = res.get("audio_sec", job.audio_sec)
            job.speech_sec, job.decoded_sec = res.get("speech_sec"), res.get("decoded_sec")
            job.progress = stt_weight
