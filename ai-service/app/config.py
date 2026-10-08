"""Cấu hình AI service. Mọi giá trị đến từ biến môi trường (compose `env_file` hoặc `.env` khi chạy trên host).

Không hard-code model Gemini hay API key: dùng GEMINI_MODEL / GEMINI_API_KEY trong ai-service/.env.
"""
from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

try:  # chỉ để tiện chạy trên host; trong Docker biến đã được compose nạp sẵn
    from dotenv import load_dotenv

    load_dotenv()
except ImportError:  # pragma: no cover
    pass


@dataclass(frozen=True)
class Settings:
    token: str                    # AI_SERVICE_TOKEN: secret dùng chung với backend (header X-Api-Key)
    media_root: Path              # MEDIA_ROOT: thư mục chia sẻ với backend; media_key là đường dẫn TƯƠNG ĐỐI
    stt_model: str                # STT_MODEL
    max_audio_sec: float          # MAX_AUDIO_SEC: giới hạn độ dài (SRS/yêu cầu: 2 giờ)
    result_ttl_days: int          # RESULT_TTL_DAYS: dọn kết quả đã xong quá hạn khi khởi động
    gemini_api_key: str           # GEMINI_API_KEY (chỉ nằm ở service này, không bao giờ ra frontend)
    gemini_model: str             # GEMINI_MODEL
    gemini_thinking_level: str    # GEMINI_THINKING_LEVEL: rỗng = dùng mặc định của model


def load_settings() -> Settings:
    return Settings(
        token=os.environ.get("AI_SERVICE_TOKEN", ""),
        media_root=Path(os.environ.get("MEDIA_ROOT", "/data/media")),
        stt_model=os.environ.get("STT_MODEL", "large-v3-turbo"),
        max_audio_sec=float(os.environ.get("MAX_AUDIO_SEC", 7200 + 60)),  # +60s dung sai metadata
        result_ttl_days=int(os.environ.get("RESULT_TTL_DAYS", 7)),
        gemini_api_key=os.environ.get("GEMINI_API_KEY", ""),
        gemini_model=os.environ.get("GEMINI_MODEL", ""),
        gemini_thinking_level=os.environ.get("GEMINI_THINKING_LEVEL", "").upper(),
    )
