"""Tóm tắt transcript bằng Gemini (SDK google-genai).

Model và API key lấy từ cấu hình (GEMINI_MODEL, GEMINI_API_KEY). Thiếu cấu hình hoặc Gemini lỗi chỉ làm
hỏng bước tóm tắt (SummarizeError); transcript vẫn được giữ và trả về như bình thường.
"""
from __future__ import annotations

import bisect
import logging
import time
from typing import Optional

from .summary_prompt import SUMMARY_SCHEMA, SYSTEM_INSTRUCTION, build_user_message, parse_summary

log = logging.getLogger("ai.summarize")

_TRANSIENT = {429, 500, 502, 503, 504}


class SummarizeError(Exception):
    pass


def _hms(seconds: float) -> str:
    s = int(seconds)
    return f"{s // 3600:02d}:{s % 3600 // 60:02d}:{s % 60:02d}"


def _to_sec(ts: str) -> int:
    h, m, s = (int(x) for x in ts.split(":"))
    return h * 3600 + m * 60 + s


def snap_timestamps(summary: dict, segments: list[dict]) -> dict:
    """Mốc thời gian phải trùng một dòng có thật trong transcript.

    Model nhẹ đôi khi chép sai mốc nhưng vẫn đúng định dạng HH:MM:SS. Mốc không có trong transcript được kéo về
    dòng gần nhất ở trước nó; nếu không có dòng nào ở trước thì bỏ mốc (None).
    """
    starts = sorted({int(s["start"]) for s in segments if s.get("text", "").strip()})
    valid = {_hms(x) for x in starts}
    for value in summary.values():
        if not isinstance(value, list):
            continue
        for item in value:
            if not isinstance(item, dict) or not item.get("timestamp"):
                continue
            ts = item["timestamp"]
            if ts in valid:
                continue
            i = bisect.bisect_right(starts, _to_sec(ts)) - 1
            item["timestamp"] = _hms(starts[i]) if i >= 0 else None
    return summary


class GeminiSummarizer:
    def __init__(self, api_key: str, model: str, thinking_level: str = "", timeout_sec: int = 300):
        self.api_key, self.model, self.thinking_level, self.timeout_sec = api_key, model, thinking_level, timeout_sec
        self._client = None

    def _get_client(self):
        if not self.api_key or not self.model:
            raise SummarizeError("Chưa cấu hình GEMINI_API_KEY hoặc GEMINI_MODEL trong ai-service/.env")
        if self._client is None:
            from google import genai

            self._client = genai.Client(api_key=self.api_key)
        return self._client

    def _config(self):
        from google.genai import types

        kwargs = dict(
            system_instruction=SYSTEM_INSTRUCTION,
            response_mime_type="application/json",
            response_schema=SUMMARY_SCHEMA,
            temperature=0.2,
            http_options=types.HttpOptions(timeout=self.timeout_sec * 1000),  # SDK tính bằng mili giây
        )
        if self.thinking_level:
            kwargs["thinking_config"] = types.ThinkingConfig(thinking_level=self.thinking_level)
        return types.GenerateContentConfig(**kwargs)

    def summarize(self, segments: list[dict], output_lang: str = "vi", duration_sec: Optional[float] = None) -> dict:
        if not any(s.get("text", "").strip() for s in segments):
            return {
                "content_type": "other", "title": "Không có nội dung lời nói",
                "overview": "Bản ghi không có lời nói nhận dạng được nên không có gì để tóm tắt.",
                "key_points": [], "topics": [], "decisions": [], "action_items": [],
                "open_questions": [], "positions": [], "quality_notes": ["Transcript rỗng."],
            }
        from google.genai import errors

        client = self._get_client()
        contents = build_user_message(segments, output_lang, duration_sec)
        config = self._config()
        last: Exception | None = None
        for attempt in range(3):
            try:
                response = client.models.generate_content(model=self.model, contents=contents, config=config)
                if not response.text:
                    raise ValueError("Gemini trả về rỗng (có thể bị chặn bởi bộ lọc an toàn)")
                return snap_timestamps(parse_summary(response.text), segments)
            except ValueError as e:  # JSON hỏng/rỗng: thử lại, tối đa 3 lần
                last = e
            except errors.APIError as e:
                last = e
                if getattr(e, "code", None) not in _TRANSIENT:
                    break
            log.warning("Gemini lần %d thất bại: %s", attempt + 1, last)
            time.sleep(2 ** attempt)
        raise SummarizeError(f"{type(last).__name__}: {last}"[:500])
