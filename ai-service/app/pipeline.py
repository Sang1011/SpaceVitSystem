"""Pipeline STT dùng chung cho AI service và spikes/stt/bench.py.

Cấu trúc 3 lớp, để luồng Live Recording sau này chỉ cần thêm nơi gọi, không phải sửa pipeline:

1. Lõi không trạng thái, làm việc trên MẢNG audio (16 kHz mono float32) và có `offset_sec`:
   `detect_windows()` (VAD) và `transcribe_windows()` (STT batched). Một chunk live chỉ là một
   mảng audio nhỏ với `offset_sec` = thời điểm bắt đầu chunk trong bản ghi.
2. `transcribe_file()` là lớp mỏng: giải mã file -> lõi -> merge, có hook tiến độ và hủy job.
3. `load_faster_whisper()` giữ nguyên giao diện cũ `transcribe(path, lang) -> dict` cho bench.py.

TODO(live-recording): xem docs/live-recording-todo.md. Việc cần làm ở đây khi triển khai live:
- thêm `transcribe_chunk(audio, lang, offset_sec, ...)` gọi `detect_windows` + `transcribe_windows`;
- quyết định VAD có mang ngữ cảnh qua ranh giới chunk hay không (xem tài liệu).
"""
from __future__ import annotations

import json
import logging
import os
import platform
import subprocess
import time
from pathlib import Path
from typing import Callable, Optional

import av

log = logging.getLogger("ai.pipeline")

SAMPLE_RATE = 16000
# Whisper decode theo cửa sổ 30s. Giữ dưới 30s để thư viện không cắt mất phần đuôi cửa sổ.
MAX_WINDOW_SEC = 29.5

OnSegment = Callable[[float, float, str], None]
OnStage = Callable[[str, dict], None]
ShouldCancel = Callable[[], bool]


class JobCancelled(Exception):
    """Ném khi người dùng hủy job; được kiểm tra giữa các lô (batch) segment."""


def env_int(name: str, default: int) -> int:
    return int(os.environ.get(name, default))


def env_float(name: str, default: float) -> float:
    return float(os.environ.get(name, default))


def audio_seconds(path: Path) -> float:
    """Độ dài audio đọc từ metadata container, không giải mã nên rất nhanh."""
    with av.open(str(path)) as container:
        if container.duration:
            return container.duration / 1_000_000
        stream = container.streams.audio[0]  # một số container không có duration tổng
        return float(stream.duration * stream.time_base)


def hardware_info(device: str) -> dict:
    info = {"cpu_count": os.cpu_count(), "platform": platform.platform(), "device": device}
    if device == "cuda":
        try:
            info["gpu"] = subprocess.run(
                ["nvidia-smi", "--query-gpu=name,memory.total", "--format=csv,noheader"],
                capture_output=True, text=True, timeout=5,
            ).stdout.strip()
        except Exception:
            pass
    return info


def build_windows(speech: list[dict], sr: int = SAMPLE_RATE, max_len: float = MAX_WINDOW_SEC) -> list[dict]:
    """Gộp các đoạn speech liền nhau thành cửa sổ <= max_len giây, theo timeline GỐC (đơn vị giây).

    Whisper luôn pad mỗi cửa sổ lên 30s nên cửa sổ càng đầy càng đỡ tốn.
    Khoảng im lặng nằm giữa 2 cửa sổ bị bỏ qua; im lặng ngắn nằm trong cửa sổ được giữ nguyên.
    Vì dùng timeline gốc nên timestamp của segment không cần ánh xạ lại.
    """
    windows: list[dict] = []
    cur: dict | None = None
    for seg in speech:
        s, e = seg["start"] / sr, seg["end"] / sr
        while e - s > max_len:  # đoạn speech quá dài: cắt cứng, không để thư viện âm thầm bỏ phần sau
            if cur:
                windows.append(cur)
                cur = None
            windows.append({"start": s, "end": s + max_len})
            s += max_len
        if cur and e - cur["start"] <= max_len:
            cur["end"] = e
        else:
            if cur:
                windows.append(cur)
            cur = {"start": s, "end": e}
    if cur:
        windows.append(cur)
    return windows


class FasterWhisperEngine:
    """Nạp model MỘT lần; mọi cấu hình lấy từ biến môi trường STT_* (xem README)."""

    def __init__(self, model_name: str = "large-v3-turbo"):
        import ctranslate2
        from faster_whisper import BatchedInferencePipeline, WhisperModel
        from faster_whisper.vad import VadOptions, get_speech_timestamps

        device = os.environ.get("STT_DEVICE", "auto")
        if device == "auto":  # resolve rõ ràng để log biết chắc đang chạy CPU hay GPU
            device = "cuda" if ctranslate2.get_cuda_device_count() > 0 else "cpu"
        compute_type = os.environ.get("STT_COMPUTE_TYPE") or ("float16" if device == "cuda" else "int8")
        # faster-whisper mặc định chỉ dùng 4 thread CPU nếu cpu_threads=0
        cpu_threads = env_int("STT_CPU_THREADS", os.cpu_count() or 4)
        self.beam = env_int("STT_BEAM", 1)
        self.batch = env_int("STT_BATCH", 8)  # 0 = chạy tuần tự kiểu cũ (model.transcribe)
        self.use_vad = os.environ.get("STT_VAD", "1") == "1"
        self.vad_opts = VadOptions(
            threshold=env_float("STT_VAD_THRESHOLD", 0.4),  # thấp hơn mặc định 0.5 để đỡ rơi tiếng nhỏ
            min_silence_duration_ms=env_int("STT_VAD_MIN_SILENCE_MS", 300),
            speech_pad_ms=env_int("STT_VAD_PAD_MS", 500),  # đệm đầu/cuối câu
            max_speech_duration_s=28,  # + pad 2 đầu vẫn < MAX_WINDOW_SEC
        )
        self._get_speech_timestamps = get_speech_timestamps

        t0 = time.perf_counter()
        self.model = WhisperModel(model_name, device=device, compute_type=compute_type, cpu_threads=cpu_threads)
        self._pipeline = BatchedInferencePipeline(self.model) if self.batch > 0 else None
        self.config = {
            "model": model_name, "compute_type": compute_type, "cpu_threads": cpu_threads,
            "beam": self.beam, "batch": self.batch, "vad": self.use_vad,
            "vad_threshold": self.vad_opts.threshold, "vad_pad_ms": self.vad_opts.speech_pad_ms,
            "model_load_sec": round(time.perf_counter() - t0, 1),
            **hardware_info(device),
        }
        log.info("STT engine ready: %s", json.dumps(self.config, ensure_ascii=False))

    # ------------------------------------------------------------------ lõi (mảng audio)

    def detect_windows(self, audio) -> tuple[list[dict], float]:
        """VAD -> các cửa sổ <= MAX_WINDOW_SEC (giây, theo timeline của `audio`). Trả (windows, speech_sec)."""
        audio_sec = len(audio) / SAMPLE_RATE
        if self.use_vad:
            speech = self._get_speech_timestamps(audio, self.vad_opts)
            speech_sec = sum(s["end"] - s["start"] for s in speech) / SAMPLE_RATE
            return build_windows(speech), speech_sec
        # đối chứng: không VAD, cắt cố định, dùng để kiểm tra VAD có làm mất chữ không
        starts = [i * MAX_WINDOW_SEC for i in range(int(audio_sec // MAX_WINDOW_SEC) + 1)]
        return [{"start": s, "end": min(s + MAX_WINDOW_SEC, audio_sec)} for s in starts if s < audio_sec], audio_sec

    def transcribe_windows(
        self, audio, windows: list[dict], lang: str, offset_sec: float = 0.0,
        on_segment: Optional[OnSegment] = None, should_cancel: Optional[ShouldCancel] = None,
    ) -> list[tuple[float, float, str]]:
        """STT theo batch trên các cửa sổ. Timestamp trả về = timeline của `audio` + `offset_sec`.

        Segment được phát dần qua `on_segment` (theo từng lô cửa sổ) nên có thể xem transcript từng phần.
        """
        if self._pipeline is None:
            raise RuntimeError("transcribe_windows cần STT_BATCH > 0")
        raw: list[tuple[float, float, str]] = []
        if not windows:
            return raw
        segs, _ = self._pipeline.transcribe(
            audio, language=lang, beam_size=self.beam, batch_size=self.batch,
            clip_timestamps=windows, without_timestamps=False, vad_filter=False,
        )
        for s in segs:
            if should_cancel and should_cancel():
                raise JobCancelled()
            text = s.text.strip()
            if not text:
                continue
            start, end = s.start + offset_sec, s.end + offset_sec
            raw.append((start, end, text))
            if on_segment:
                on_segment(start, end, text)
        return raw

    # ------------------------------------------------------------------ file

    def transcribe_file(
        self, path: Path, lang: str, on_stage: Optional[OnStage] = None,
        on_segment: Optional[OnSegment] = None, should_cancel: Optional[ShouldCancel] = None,
    ) -> dict:
        from faster_whisper import decode_audio

        stages = {"extract_sec": 0.0, "vad_sec": 0.0, "stt_sec": 0.0, "merge_sec": 0.0}
        if on_stage:
            on_stage("PREPROCESSING", {})

        # Stage 1: giải mã/trích audio 16 kHz mono (video: lấy audio stream), dùng chung cho VAD và STT
        t = time.perf_counter()
        audio = decode_audio(str(path), sampling_rate=SAMPLE_RATE)
        stages["extract_sec"] = time.perf_counter() - t
        audio_sec = len(audio) / SAMPLE_RATE
        if should_cancel and should_cancel():
            raise JobCancelled()

        if self._pipeline is not None:
            # Stage 2: VAD (Silero) -> gộp thành cửa sổ <= 30s
            t = time.perf_counter()
            windows, speech_sec = self.detect_windows(audio)
            stages["vad_sec"] = time.perf_counter() - t
            decoded_sec = sum(w["end"] - w["start"] for w in windows)
            if on_stage:
                on_stage("TRANSCRIBING", {"audio_sec": audio_sec, "speech_sec": speech_sec, "decoded_sec": decoded_sec})

            # Stage 3: STT theo batch; timestamp trả về đã theo timeline gốc
            t = time.perf_counter()
            raw = self.transcribe_windows(audio, windows, lang, 0.0, on_segment, should_cancel)
            stages["stt_sec"] = time.perf_counter() - t
        else:
            # Đường cũ (tuần tự): VAD chạy bên trong transcribe nên nằm trong stt_sec
            if on_stage:
                on_stage("TRANSCRIBING", {"audio_sec": audio_sec, "speech_sec": None, "decoded_sec": None})
            t = time.perf_counter()
            segs, info = self.model.transcribe(
                audio, language=lang, beam_size=self.beam, vad_filter=self.use_vad, vad_parameters=self.vad_opts
            )
            raw = []
            for s in segs:
                if should_cancel and should_cancel():
                    raise JobCancelled()
                if s.text.strip():
                    raw.append((s.start, s.end, s.text.strip()))
                    if on_segment:
                        on_segment(s.start, s.end, s.text.strip())
            stages["stt_sec"] = time.perf_counter() - t
            decoded_sec = getattr(info, "duration_after_vad", audio_sec)
            speech_sec = decoded_sec

        # Stage 4: merge theo thứ tự thời gian, KHÔNG dedupe để không làm mất transcript
        t = time.perf_counter()
        segments = [{"start": round(s, 2), "end": round(e, 2), "text": x.strip()} for s, e, x in sorted(raw) if x.strip()]
        text = " ".join(s["text"] for s in segments)
        stages["merge_sec"] = time.perf_counter() - t

        return {
            "text": text, "segments": segments, "stages": stages, "audio_sec": audio_sec,
            "speech_sec": speech_sec, "decoded_sec": decoded_sec,
        }


def load_faster_whisper(model_name: str):
    """Giao diện cũ cho bench.py: trả hàm transcribe(path, lang) -> dict, có thuộc tính .config."""
    engine = FasterWhisperEngine(model_name)
    print(f"[config] {json.dumps(engine.config, ensure_ascii=False)}")

    def transcribe(path: Path, lang: str) -> dict:
        return engine.transcribe_file(path, lang)

    transcribe.config = engine.config
    return transcribe
