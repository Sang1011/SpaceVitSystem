"""Test lõi pipeline với faster-whisper giả (không cần model)."""
import numpy as np
import pytest

import app.pipeline as P

SR = P.SAMPLE_RATE


def test_build_windows_cover_all_speech_and_respect_limit():
    speech = [{"start": int(a * SR), "end": int(b * SR)} for a, b in [(1, 5), (6, 12), (20, 28), (70, 72), (100, 135)]]
    w = P.build_windows(speech)
    assert all(x["end"] - x["start"] <= P.MAX_WINDOW_SEC + 1e-6 for x in w)
    for s in speech:
        for t in (s["start"] / SR, s["end"] / SR):
            assert any(x["start"] - 1e-6 <= t <= x["end"] + 1e-6 for x in w)


class Seg:
    def __init__(self, a, b, t):
        self.start, self.end, self.text = a, b, t


@pytest.fixture
def engine(monkeypatch):
    import faster_whisper
    import faster_whisper.vad as vad
    import ctranslate2

    calls = {}

    class FakeModel:
        def __init__(self, *a, **k):
            calls["model_kwargs"] = k

    class FakePipe:
        def __init__(self, m): ...

        def transcribe(self, audio, **kw):
            calls["kw"] = kw
            return iter([Seg(30.0, 32.0, " hai "), Seg(1.0, 3.0, " một "), Seg(5, 6, "  ")]), None

    monkeypatch.setattr(faster_whisper, "WhisperModel", FakeModel)
    monkeypatch.setattr(faster_whisper, "BatchedInferencePipeline", FakePipe)
    monkeypatch.setattr(vad, "get_speech_timestamps", lambda a, o: [{"start": SR, "end": 5 * SR}, {"start": 20 * SR, "end": 28 * SR}])
    monkeypatch.setattr(ctranslate2, "get_cuda_device_count", lambda: 0)
    e = P.FasterWhisperEngine("fake")
    e.calls = calls
    return e


def test_core_is_array_based_with_offset_and_callbacks(engine):
    audio = np.zeros(40 * SR, dtype="float32")
    windows, speech_sec = engine.detect_windows(audio)
    assert speech_sec == 12.0 and windows[0]["start"] == 1.0
    seen = []
    raw = engine.transcribe_windows(audio, windows, "vi", offset_sec=100.0, on_segment=lambda s, e, t: seen.append((s, t)))
    assert [r[2] for r in raw] == ["hai", "một"] and seen[0] == (130.0, "hai")  # offset được cộng, segment rỗng bị bỏ
    kw = engine.calls["kw"]
    assert kw["vad_filter"] is False and kw["without_timestamps"] is False and kw["beam_size"] == 1 and kw["batch_size"] == 8


def test_cancel_between_segments(engine):
    audio = np.zeros(40 * SR, dtype="float32")
    with pytest.raises(P.JobCancelled):
        engine.transcribe_windows(audio, [{"start": 0, "end": 10}], "vi", should_cancel=lambda: True)


def test_cpu_threads_not_left_at_library_default(engine):
    assert engine.config["cpu_threads"] >= 1 and engine.calls["model_kwargs"]["cpu_threads"] == engine.config["cpu_threads"]


def test_live_core_works_even_when_file_path_is_sequential(engine, monkeypatch):
    """STT_BATCH=0 chỉ đổi đường xử lý FILE; transcribe_windows (dùng cho live) vẫn chạy, với batch_size tối thiểu 1."""
    import faster_whisper
    monkeypatch.setenv("STT_BATCH", "0")
    e = P.FasterWhisperEngine("fake")
    e.calls = engine.calls
    assert e._pipeline is None
    raw = e.transcribe_windows(np.zeros(40 * SR, dtype="float32"), [{"start": 0, "end": 10}], "vi")
    assert [r[2] for r in raw] == ["hai", "một"] and engine.calls["kw"]["batch_size"] == 1
