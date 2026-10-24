"""Ghi âm trực tiếp (live): xử lý tăng dần theo chunk, chốt câu theo IM LẶNG, finalize khi dừng.

Mô hình:
- Backend lưu từng chunk PCM (s16le, 16 kHz, mono) vào thư mục media dùng chung rồi báo `seq` + `media_key`.
- Phiên chỉ tiêu thụ chunk LIÊN TIẾP theo `seq`, nối vào một buffer nhỏ (đuôi chưa chép), chạy VAD trên buffer.
  Đoạn nói đã "đóng" (sau im lặng đủ dài) được chép ngay và chốt; chunk chỉ để truyền nên không cắt giữa từ.
- Chunk thiếu: sau LIVE_GAP_WAIT_SEC (hoặc `wait_missing_sec` khi finalize) thì ghi nhận khoảng trống và đi tiếp.
  Chunk đến muộn sau đó được chép riêng, không chép lại các chunk khác (APR-LIVE-04).
- Finalize: chép nốt phần đuôi, đối soát chunk thiếu, tóm tắt (nếu yêu cầu); KHÔNG chép lại toàn bộ audio (APR-LIVE-03).

Mọi xử lý nặng chạy trong luồng worker của JobManager (xem jobs.py), nên chen vào giữa các lô decode của job upload.

TODO(live, chưa làm): khôi phục buffer khi AI service restart (hiện backend tạo lại phiên và gửi lại toàn bộ chunk),
transcript "đang nói" (volatile) bằng cửa sổ trượt, loại câu ảo giác do tiếng ồn. Xem docs/live-recording.md.
"""
from __future__ import annotations

import json
import logging
import os
import threading
import time
from concurrent.futures import Future
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Optional

import numpy as np

from .jobs import JobError, resolve_media
from .pipeline import SAMPLE_RATE, build_windows

log = logging.getLogger("ai.live")

ACTIVE, FINALIZING, ANALYZING = "ACTIVE", "FINALIZING", "ANALYZING"
COMPLETED, PARTIAL_FAILED, FAILED, CANCELLED = "COMPLETED", "PARTIAL_FAILED", "FAILED", "CANCELLED"
TERMINAL = frozenset({COMPLETED, PARTIAL_FAILED, FAILED, CANCELLED})
HAS_RESULT = frozenset({COMPLETED, PARTIAL_FAILED})
BYTES_PER_SEC = SAMPLE_RATE * 2  # s16le mono


class LiveError(Exception):
    def __init__(self, status: int, code: str, message: str):
        super().__init__(message)
        self.status, self.code = status, code


@dataclass
class LiveConfig:
    max_sessions: int = 2
    gap_wait_sec: float = 30.0          # chờ chunk thiếu bao lâu (khi đang ghi) trước khi coi là khoảng trống
    keep_silence_sec: float = 1.0       # giữ lại bao nhiêu giây im lặng ở đuôi buffer khi chưa có lời nói
    terminal_ttl_sec: float = 3600.0    # xóa phiên đã kết thúc khỏi RAM (kết quả vẫn còn trên đĩa)


def read_pcm(path: Path) -> np.ndarray:
    raw = path.read_bytes()
    if len(raw) < 2:
        raise ValueError("chunk rỗng")
    if len(raw) % 2:
        raw = raw[:-1]  # byte lẻ ở cuối: bỏ, không làm hỏng cả chunk
    return np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0


class LiveSession:
    def __init__(self, session_id: str, language: str, output_language: str, chunk_sec: float, cfg: LiveConfig):
        self.id, self.language, self.output_language, self.chunk_sec = session_id, language, output_language, chunk_sec
        self.cfg = cfg
        self.lock = threading.RLock()
        self.state = ACTIVE
        self.created_at = self.last_activity = time.time()
        self.finished_at: Optional[float] = None

        self.chunks: dict[int, str] = {}        # seq -> media_key (đã nhận)
        self.skipped: dict[int, dict] = {}      # seq đã bị coi là khoảng trống
        self.late: list[int] = []               # seq đến muộn, chờ chép riêng
        self.next_seq = 0
        self.blocked_since: Optional[float] = None

        self.buffer = np.zeros(0, dtype=np.float32)
        self.buffer_start = 0.0                 # mốc (giây) của buffer[0] trong bản ghi
        self.timeline_end = 0.0

        self.segments: list[dict] = []          # chỉ thêm vào cuối, theo thứ tự chốt (để lấy từng phần bằng `after`)
        self.gaps: list[dict] = []
        self.committed_sec = 0.0

        self.finalize_req: Optional[dict] = None
        self.finalize_at: Optional[float] = None
        self.future: Optional[Future] = None
        self.summary: Optional[dict] = None
        self.summary_error: Optional[str] = None
        self.error: Optional[str] = None
        self.error_code: Optional[str] = None
        self.stages: dict[str, float] = {"stt_sec": 0.0}
        self.config: dict = {}
        self.dirty = False

    # ------------------------------------------------------------------ gọi từ luồng HTTP

    def put_chunk(self, seq: int, media_key: str) -> str:
        with self.lock:
            if self.state not in (ACTIVE, FINALIZING):
                raise LiveError(409, "SESSION_CLOSED", f"Phiên đã ở trạng thái {self.state}")
            if self.finalize_req and seq >= self.finalize_req["total_chunks"]:
                raise LiveError(400, "SEQ_OUT_OF_RANGE", "seq vượt total_chunks đã báo khi dừng")
            self.last_activity = time.time()
            if seq in self.chunks:
                if self.chunks[seq] != media_key:
                    raise LiveError(409, "SEQ_CONFLICT", "seq đã tồn tại với nội dung khác")
                return "duplicate"
            self.chunks[seq] = media_key
            if seq in self.skipped:
                self.late.append(seq)
                self.dirty = True
                return "late"
            self.dirty = True
            return "accepted"

    def request_finalize(self, total_chunks: int, mode: str, wait_missing_sec: float) -> None:
        with self.lock:
            if self.finalize_req:
                if self.finalize_req["total_chunks"] != total_chunks:
                    raise LiveError(409, "FINALIZE_CONFLICT", "Đã dừng với total_chunks khác")
                return  # idempotent
            if self.state != ACTIVE:
                raise LiveError(409, "SESSION_CLOSED", f"Phiên đã ở trạng thái {self.state}")
            if any(s >= total_chunks for s in self.chunks):
                raise LiveError(400, "SEQ_OUT_OF_RANGE", "Có chunk với seq >= total_chunks")
            self.finalize_req = {"total_chunks": total_chunks, "mode": mode, "wait_missing_sec": wait_missing_sec}
            self.finalize_at = time.time()
            self.state = FINALIZING
            self.dirty = True

    def cancel(self) -> None:
        with self.lock:
            if self.state not in TERMINAL:
                self.state = CANCELLED
                self.finished_at = time.time()
                self.buffer = np.zeros(0, dtype=np.float32)

    def view(self) -> dict:
        with self.lock:
            highest = max(self.chunks, default=-1)
            missing = [s for s in range(highest) if s not in self.chunks]
            return {
                "session_id": self.id, "state": self.state, "next_seq": self.next_seq, "received": len(self.chunks),
                "highest_seq": highest, "missing_seqs": missing[:200], "gaps": [dict(g) for g in self.gaps],
                "segments_ready": len(self.segments), "committed_sec": round(self.committed_sec, 2),
                "buffered_sec": round(len(self.buffer) / SAMPLE_RATE, 2), "error": self.error, "error_code": self.error_code,
            }

    def result(self) -> dict:
        with self.lock:
            return {
                "session_id": self.id, "state": self.state, "language": self.language,
                "audio_sec": round(self.timeline_end, 2),
                "segments": sorted((dict(s) for s in self.segments), key=lambda s: (s["start"], s["end"])),
                "gaps": [dict(g) for g in self.gaps], "summary": self.summary, "summary_error": self.summary_error,
                "stages": {k: round(v, 1) for k, v in self.stages.items()}, "config": self.config,
            }

    # ------------------------------------------------------------------ gọi từ luồng worker

    def tick(self, now: float) -> None:
        with self.lock:
            if self.state in (ACTIVE, FINALIZING):
                if self.blocked_since is not None:
                    wait = self.finalize_req["wait_missing_sec"] if self.finalize_req else self.cfg.gap_wait_sec
                    ref = max(self.blocked_since, self.finalize_at or 0.0)
                    if now - ref >= wait:
                        self.dirty = True
            elif self.state == ANALYZING and self.future is not None and self.future.done():
                self.dirty = True

    def has_work(self) -> bool:
        return self.dirty

    def step(self, engine: Any, submit_summary: Callable[..., Future]) -> None:
        with self.lock:
            self.dirty = False
            state = self.state
            self.config = dict(getattr(engine, "config", {}) or {})
        if state in (ACTIVE, FINALIZING):
            self._consume(engine)
            self._process_late(engine)
            self._maybe_finish(engine, submit_summary)
        elif state == ANALYZING:
            self._collect_summary()

    # --- tiêu thụ chunk theo thứ tự

    def _consume(self, engine: Any) -> None:
        consumed = False
        while True:
            with self.lock:
                seq = self.next_seq
                key = self.chunks.get(seq) if seq not in self.skipped else None
                fin = self.finalize_req
                has_later = any(s > seq for s in self.chunks)
                now = time.time()
            if key is not None:
                try:
                    audio = read_pcm(resolve_media(self.media_root, key))
                except (JobError, ValueError, OSError) as e:
                    self._declare_gap(engine, seq, f"chunk không đọc được: {e}")
                    continue
                with self.lock:
                    self.buffer = np.concatenate([self.buffer, audio])
                    self.timeline_end += len(audio) / SAMPLE_RATE
                    self.next_seq += 1
                    self.blocked_since = None
                consumed = True
                continue
            if fin and seq >= fin["total_chunks"]:
                break
            if not fin and not has_later:
                break  # chưa tới, không phải bị chặn
            # đang bị chặn bởi chunk thiếu
            with self.lock:
                if self.blocked_since is None:
                    self.blocked_since = now
                wait = fin["wait_missing_sec"] if fin else self.cfg.gap_wait_sec
                ref = max(self.blocked_since, self.finalize_at or 0.0)
                expired = now - ref >= wait
            if not expired:
                break
            self._declare_gap(engine, seq, "chunk không tới kịp")
        if consumed:
            self._commit(engine, flush=False)

    def _declare_gap(self, engine: Any, seq: int, reason: str) -> None:
        self._commit(engine, flush=True)  # liên tục bị đứt: chốt hết phần đã có
        with self.lock:
            rec = {"seq": seq, "start": round(self.timeline_end, 2), "end": round(self.timeline_end + self.chunk_sec, 2), "reason": reason}
            self.skipped[seq] = rec
            self.gaps.append(rec)
            self.timeline_end += self.chunk_sec
            self.buffer_start = self.timeline_end
            self.next_seq += 1
            self.blocked_since = None
        log.warning("Phiên %s: khoảng trống tại seq %d (%s)", self.id, seq, reason)

    def _process_late(self, engine: Any) -> None:
        while True:
            with self.lock:
                if not self.late:
                    return
                seq = self.late.pop(0)
                rec = self.skipped.get(seq)
                key = self.chunks.get(seq)
            if rec is None or key is None:
                continue
            try:
                audio = read_pcm(resolve_media(self.media_root, key))
            except (JobError, ValueError, OSError) as e:
                rec["reason"] = f"chunk đến muộn nhưng không đọc được: {e}"
                continue
            if self._transcribe(engine, audio, rec["start"], flush=True):
                with self.lock:
                    self.gaps = [g for g in self.gaps if g["seq"] != seq]  # đã bù được

    # --- chốt câu theo im lặng

    def _closed_spans(self, engine: Any, speech: list[dict], n: int, flush: bool) -> list[dict]:
        if flush or not speech:
            return speech
        closed = speech[:-1]
        last = speech[-1]
        # Đoạn cuối chỉ được coi là đã đóng khi sau nó có đủ im lặng. `end` đã gồm đệm `pad`, và bị chặn ở n nếu im lặng < pad.
        need = max(engine.live_commit_silence_sec - engine.live_pad_sec, 0.05) * SAMPLE_RATE
        if last["end"] < n and (n - last["end"]) >= need:
            closed.append(last)
        return closed

    def _commit(self, engine: Any, flush: bool) -> None:
        with self.lock:
            buf, start = self.buffer, self.buffer_start
        n = len(buf)
        if n == 0:
            return
        speech = engine.detect_speech(buf, live=True)
        if not speech:
            keep = int(self.cfg.keep_silence_sec * SAMPLE_RATE)
            drop = n if flush else max(n - keep, 0)
            self._advance(drop)
            return
        closed = self._closed_spans(engine, speech, n, flush)
        if not closed:
            return
        windows = build_windows(closed)
        self._transcribe_windows(engine, buf, windows, start)
        self._advance(n if flush else int(closed[-1]["end"]))

    def _advance(self, drop: int) -> None:
        with self.lock:
            self.buffer = self.buffer[drop:]
            self.buffer_start += drop / SAMPLE_RATE

    def _transcribe(self, engine: Any, audio: np.ndarray, offset: float, flush: bool) -> bool:
        speech = engine.detect_speech(audio, live=True)
        if not speech:
            return True
        return self._transcribe_windows(engine, audio, build_windows(speech), offset)

    def _transcribe_windows(self, engine: Any, audio: np.ndarray, windows: list[dict], offset: float) -> bool:
        t = time.perf_counter()
        try:
            raw = engine.transcribe_windows(audio, windows, self.language, offset_sec=offset)
        except Exception as e:  # một lô lỗi không được làm chết cả phiên: ghi khoảng trống rồi đi tiếp
            log.exception("Phiên %s: chép lỗi", self.id)
            with self.lock:
                self.gaps.append({"seq": None, "start": round(offset + windows[0]["start"], 2),
                                  "end": round(offset + windows[-1]["end"], 2), "reason": f"lỗi STT: {type(e).__name__}"})
            return False
        with self.lock:
            self.stages["stt_sec"] += time.perf_counter() - t
            for s, e, text in raw:
                self.segments.append({"start": round(s, 2), "end": round(e, 2), "text": text})
            if raw:
                self.committed_sec = max(self.committed_sec, raw[-1][1])
        return True

    # --- kết thúc

    def _maybe_finish(self, engine: Any, submit_summary: Callable[..., Future]) -> None:
        with self.lock:
            fin = self.finalize_req
            ready = (self.state == FINALIZING and fin is not None and self.next_seq >= fin["total_chunks"] and not self.late)
        if not ready:
            return
        self._commit(engine, flush=True)
        if fin["mode"] == "summarize":
            with self.lock:
                self.state = ANALYZING
                segs = sorted(self.segments, key=lambda s: s["start"])
                self._gemini_t0 = time.perf_counter()
                self.future = submit_summary(segs, self.output_language, self.timeline_end)
                self.dirty = True
        else:
            self._finish(COMPLETED)

    def _collect_summary(self) -> None:
        with self.lock:
            fut = self.future
        if fut is None or not fut.done():
            return
        try:
            summary = fut.result()
            err = None
        except Exception as e:
            summary, err = None, f"{type(e).__name__}: {e}"[:500]
            log.error("Phiên %s: tóm tắt lỗi: %s", self.id, err)
        with self.lock:
            self.summary, self.summary_error = summary, err
            self.stages["gemini_sec"] = time.perf_counter() - getattr(self, "_gemini_t0", time.perf_counter())
        if err:
            self._finish(PARTIAL_FAILED, err, "SUMMARY_FAILED")
        else:
            self._finish(COMPLETED)

    def _finish(self, state: str, error: Optional[str] = None, code: Optional[str] = None) -> None:
        with self.lock:
            self.state, self.error, self.error_code = state, error, code
            self.finished_at = time.time()
            self.stages["total_sec"] = self.finished_at - self.created_at
            self.buffer = np.zeros(0, dtype=np.float32)

    # media_root được LiveManager gán khi tạo phiên
    media_root: Path = Path(".")


class LiveManager:
    def __init__(self, media_root: Path, cfg: LiveConfig, submit_summary: Callable[..., Future]):
        self.media_root, self.cfg, self._submit = media_root, cfg, submit_summary
        self.results_dir = media_root / "results"
        self._lock = threading.RLock()
        self._sessions: dict[str, LiveSession] = {}
        self._rr = 0

    # ------------------------------------------------------------------ HTTP

    def create(self, session_id: str, language: str, output_language: str, chunk_sec: float) -> tuple[LiveSession, bool]:
        with self._lock:
            existing = self.get(session_id)
            if existing is not None:
                return existing, False
            active = sum(1 for s in self._sessions.values() if s.state in (ACTIVE, FINALIZING))
            if active >= self.cfg.max_sessions:
                raise LiveError(429, "LIVE_BUSY", f"Đã có {active} phiên ghi âm đang chạy (tối đa {self.cfg.max_sessions})")
            s = LiveSession(session_id, language, output_language, chunk_sec, self.cfg)
            s.media_root = self.media_root
            self._sessions[session_id] = s
            return s, True

    def get(self, session_id: str) -> Optional[LiveSession]:
        with self._lock:
            s = self._sessions.get(session_id)
        return s or self._load(session_id)

    def delete(self, session_id: str) -> Optional[str]:
        with self._lock:
            s = self._sessions.get(session_id)
            if s is None:
                p = self._path(session_id)
                existed = p.exists()
                p.unlink(missing_ok=True)
                return "deleted" if existed else None
            if s.state in TERMINAL:
                self._sessions.pop(session_id, None)
                self._path(session_id).unlink(missing_ok=True)
                return "deleted"
        s.cancel()
        self._persist(s)
        return "cancelled"

    def active_count(self) -> int:
        with self._lock:
            return sum(1 for s in self._sessions.values() if s.state in (ACTIVE, FINALIZING))

    # ------------------------------------------------------------------ lịch chạy (luồng worker)

    def tick(self) -> None:
        now = time.time()
        with self._lock:
            sessions = list(self._sessions.values())
        for s in sessions:
            s.tick(now)
            if s.state in TERMINAL and s.finished_at and now - s.finished_at > self.cfg.terminal_ttl_sec:
                with self._lock:
                    self._sessions.pop(s.id, None)

    def has_work(self) -> bool:
        with self._lock:
            return any(s.has_work() for s in self._sessions.values())

    def step(self, engine: Any) -> None:
        with self._lock:
            ready = [s for s in self._sessions.values() if s.has_work()]
            if not ready:
                return
            self._rr += 1
            s = ready[self._rr % len(ready)]  # xoay vòng để một phiên không chiếm hết
        try:
            s.step(engine, self._submit)
        except Exception as e:
            log.exception("Phiên %s lỗi không lường trước", s.id)
            s._finish(FAILED, f"{type(e).__name__}: {e}"[:500], "LIVE_FAILED")
        if s.state in TERMINAL:
            self._persist(s)

    # ------------------------------------------------------------------ lưu kết quả

    def _path(self, session_id: str) -> Path:
        return self.results_dir / f"live-{session_id}.json"

    def _persist(self, s: LiveSession) -> None:
        try:
            self.results_dir.mkdir(parents=True, exist_ok=True)
            data = {**s.view(), **s.result(), "chunk_sec": s.chunk_sec, "output_language": s.output_language}
            tmp = self._path(s.id).with_suffix(".tmp")
            tmp.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
            os.replace(tmp, self._path(s.id))
        except OSError:
            log.exception("Không lưu được kết quả phiên %s", s.id)

    def _load(self, session_id: str) -> Optional[LiveSession]:
        try:
            d = json.loads(self._path(session_id).read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return None
        s = LiveSession(session_id, d.get("language", "vi"), d.get("output_language", "vi"), d.get("chunk_sec", 5.0), self.cfg)
        s.state, s.segments, s.gaps = d["state"], d.get("segments", []), d.get("gaps", [])
        s.summary, s.summary_error = d.get("summary"), d.get("summary_error")
        s.error, s.error_code, s.stages = d.get("error"), d.get("error_code"), d.get("stages", {})
        s.timeline_end, s.config = d.get("audio_sec", 0.0), d.get("config", {})
        return s
