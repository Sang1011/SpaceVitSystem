"""HTTP API của AI service (v1). Chỉ backend gọi; frontend không bao giờ nói chuyện trực tiếp với service này.

Chạy:  uvicorn app.main:create_app --factory --host 0.0.0.0 --port 8000 --workers 1
`--workers 1` là bắt buộc: mỗi worker uvicorn sẽ nạp model riêng và tranh nhau GPU.

Ghi âm trực tiếp: /v1/sessions (xem docs/live-recording.md). Upload: /v1/jobs.
"""
from __future__ import annotations

import logging
import secrets
from contextlib import asynccontextmanager
from typing import Callable, Literal, Optional

from fastapi import Depends, FastAPI, Header, HTTPException, Query
from pydantic import BaseModel, Field, model_validator

from .config import Settings, load_settings
from .jobs import HAS_RESULT, TERMINAL, Job, JobManager
from .live import HAS_RESULT as LIVE_HAS_RESULT
from .live import LiveConfig, LiveError, LiveManager
from .summarize import GeminiSummarizer

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
log = logging.getLogger("ai.api")


class SegmentIn(BaseModel):
    start: float = Field(ge=0)
    end: float = Field(ge=0)
    text: str


class CreateJob(BaseModel):
    job_id: str = Field(pattern=r"^[A-Za-z0-9_-]{8,64}$")
    mode: Literal["transcribe", "summarize", "resummarize"]
    language: Literal["vi", "en"] = "vi"
    output_language: Literal["vi", "en"] = "vi"
    media_key: Optional[str] = Field(default=None, max_length=512)
    segments: Optional[list[SegmentIn]] = None

    @model_validator(mode="after")
    def _check(self):
        if self.mode == "resummarize":
            if not self.segments:
                raise ValueError("resummarize cần segments")
        elif not self.media_key:
            raise ValueError(f"{self.mode} cần media_key")
        return self


SESSION_ID = r"^[A-Za-z0-9_-]{8,64}$"


class CreateSession(BaseModel):
    session_id: str = Field(pattern=SESSION_ID)
    language: Literal["vi", "en"] = "vi"
    output_language: Literal["vi", "en"] = "vi"
    chunk_sec: float = Field(default=5.0, ge=0.5, le=30)  # độ dài chunk danh nghĩa, dùng khi một chunk bị thiếu


class PutChunk(BaseModel):
    media_key: str = Field(max_length=512)


class FinalizeSession(BaseModel):
    total_chunks: int = Field(ge=0, le=100000)
    mode: Literal["transcribe", "summarize"] = "transcribe"
    wait_missing_sec: float = Field(default=60, ge=0, le=600)


def _status_body(job: Job, manager: JobManager) -> dict:
    with manager._lock:
        return {
            "job_id": job.job_id, "status": job.status, "mode": job.mode, "progress": round(job.progress, 3),
            "queue_position": manager.queue_position(job), "segments_ready": len(job.segments),
            "audio_sec": job.audio_sec, "error": job.error, "error_code": job.error_code,
        }


def create_app(
    settings: Optional[Settings] = None, engine_factory: Optional[Callable] = None, summarizer=None,
    probe_duration: Optional[Callable] = None,
) -> FastAPI:
    settings = settings or load_settings()
    if not settings.token:
        raise RuntimeError("AI_SERVICE_TOKEN chưa được cấu hình; từ chối khởi động service không có xác thực")

    if engine_factory is None:
        def engine_factory():
            from .pipeline import FasterWhisperEngine

            return FasterWhisperEngine(settings.stt_model)

    summarizer = summarizer or GeminiSummarizer(
        settings.gemini_api_key, settings.gemini_model, settings.gemini_thinking_level
    )
    kwargs = {"probe_duration": probe_duration} if probe_duration else {}
    manager = JobManager(
        settings.media_root, settings.max_audio_sec, engine_factory, summarizer,
        result_ttl_days=settings.result_ttl_days, **kwargs,
    )

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        manager.start()
        yield
        manager.stop()

    live = LiveManager(
        settings.media_root,
        LiveConfig(max_sessions=settings.live_max_sessions, gap_wait_sec=settings.live_gap_wait_sec),
        manager.submit_summary,
    )
    manager.attach_live(live)

    app = FastAPI(title="SVS AI Service", version="1", lifespan=lifespan)
    token = settings.token.encode()

    def auth(x_api_key: Optional[str] = Header(default=None)) -> None:
        if not x_api_key or not secrets.compare_digest(x_api_key.encode(), token):
            raise HTTPException(401, "API key không hợp lệ")

    app.state.manager = manager

    @app.get("/health")
    def health():
        """Không cần xác thực (dùng cho healthcheck của Docker). 503 khi engine STT khởi tạo lỗi."""
        body = {
            "status": "ok" if manager.engine_ready else ("degraded" if manager.engine_error else "loading"),
            "model_loaded": manager.engine_ready, "load_error": manager.engine_error,
            "device": (manager._engine.config.get("device") if manager._engine else None),
            **manager.stats(), "live_sessions": live.active_count(),
        }
        if manager.engine_error:
            raise HTTPException(503, body)
        return body

    @app.post("/v1/jobs", status_code=202, dependencies=[Depends(auth)])
    def create_job(req: CreateJob):
        payload = req.model_dump()
        job, _ = manager.submit(payload)
        return _status_body(job, manager)

    def _get(job_id: str) -> Job:
        job = manager.get(job_id)
        if job is None:
            raise HTTPException(404, "Không tìm thấy job")
        return job

    @app.get("/v1/jobs/{job_id}", dependencies=[Depends(auth)])
    def job_status(job_id: str):
        return _status_body(_get(job_id), manager)

    @app.get("/v1/jobs/{job_id}/segments", dependencies=[Depends(auth)])
    def job_segments(job_id: str, after: int = Query(0, ge=0)):
        """Transcript từng phần: trả các segment có chỉ số >= after (chỉ số tuyệt đối, bắt đầu từ 0)."""
        job = _get(job_id)
        with manager._lock:
            chunk = job.segments[after:]
        return {
            "job_id": job_id, "status": job.status, "after": after, "next": after + len(chunk),
            "segments": [{"index": after + i, **s} for i, s in enumerate(chunk)],
        }

    @app.get("/v1/jobs/{job_id}/result", dependencies=[Depends(auth)])
    def job_result(job_id: str):
        job = _get(job_id)
        if job.status not in HAS_RESULT:
            raise HTTPException(409, {"status": job.status, "error": job.error})
        with manager._lock:
            return {
                "job_id": job.job_id, "status": job.status, "mode": job.mode, "language": job.language,
                "audio_sec": job.audio_sec, "speech_sec": job.speech_sec, "decoded_sec": job.decoded_sec,
                "segments": job.segments, "summary": job.summary, "summary_error": job.summary_error,
                "stages": job.stages, "config": job.config,
            }

    @app.delete("/v1/jobs/{job_id}", dependencies=[Depends(auth)])
    def delete_job(job_id: str):
        """Đang chờ -> hủy ngay; đang chạy -> yêu cầu hủy (dừng sau lô hiện tại); đã xong -> xóa kết quả."""
        action = manager.delete(job_id)
        if action is None:
            raise HTTPException(404, "Không tìm thấy job")
        return {"job_id": job_id, "action": action}

    # ------------------------------------------------------------------ ghi âm trực tiếp

    @app.exception_handler(LiveError)
    def _live_error(_, exc: LiveError):
        from fastapi.responses import JSONResponse

        return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": str(exc)}})

    def _session(session_id: str):
        s = live.get(session_id)
        if s is None:
            raise HTTPException(404, "Không tìm thấy phiên")
        return s

    @app.post("/v1/sessions", status_code=201, dependencies=[Depends(auth)])
    def create_session(req: CreateSession):
        s, _ = live.create(req.session_id, req.language, req.output_language, req.chunk_sec)  # idempotent
        return s.view()

    @app.put("/v1/sessions/{session_id}/chunks/{seq}", dependencies=[Depends(auth)])
    def put_chunk(session_id: str, seq: int, req: PutChunk):
        if seq < 0 or seq > 100000:
            raise HTTPException(400, "seq không hợp lệ")
        s = _session(session_id)
        outcome = s.put_chunk(seq, req.media_key)
        manager.wake()
        return {**s.view(), "outcome": outcome}

    @app.get("/v1/sessions/{session_id}", dependencies=[Depends(auth)])
    def session_status(session_id: str):
        return _session(session_id).view()

    @app.get("/v1/sessions/{session_id}/segments", dependencies=[Depends(auth)])
    def session_segments(session_id: str, after: int = Query(0, ge=0)):
        """Transcript tăng dần theo thứ tự chốt. Chỉ số `index` tuyệt đối từ 0, dùng làm `after` lần sau."""
        s = _session(session_id)
        with s.lock:
            chunk = s.segments[after:]
            state = s.state
        return {
            "session_id": session_id, "state": state, "after": after, "next": after + len(chunk),
            "segments": [{"index": after + i, **seg} for i, seg in enumerate(chunk)],
        }

    @app.post("/v1/sessions/{session_id}/finalize", status_code=202, dependencies=[Depends(auth)])
    def finalize_session(session_id: str, req: FinalizeSession):
        s = _session(session_id)
        s.request_finalize(req.total_chunks, req.mode, req.wait_missing_sec)
        manager.wake()
        return s.view()

    @app.get("/v1/sessions/{session_id}/result", dependencies=[Depends(auth)])
    def session_result(session_id: str):
        s = _session(session_id)
        if s.state not in LIVE_HAS_RESULT:
            raise HTTPException(409, {"state": s.state, "error": s.error})
        return s.result()

    @app.delete("/v1/sessions/{session_id}", dependencies=[Depends(auth)])
    def delete_session(session_id: str):
        """Đang ghi: hủy phiên. Đã kết thúc: xóa kết quả."""
        action = live.delete(session_id)
        if action is None:
            raise HTTPException(404, "Không tìm thấy phiên")
        return {"session_id": session_id, "action": action}

    return app
