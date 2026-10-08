"""HTTP API của AI service (v1). Chỉ backend gọi; frontend không bao giờ nói chuyện trực tiếp với service này.

Chạy:  uvicorn app.main:create_app --factory --host 0.0.0.0 --port 8000 --workers 1
`--workers 1` là bắt buộc: mỗi worker uvicorn sẽ nạp model riêng và tranh nhau GPU.

TODO(live-recording): dự trữ tiền tố /v1/sessions cho ghi âm trực tiếp (tạo session, PUT chunk theo số thứ tự,
lấy transcript từng phần, finalize). Xem docs/live-recording-todo.md. Không thêm vào /v1/jobs.
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
            **manager.stats(),
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

    return app
