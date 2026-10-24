"""Kiểm tra luồng live với Silero VAD THẬT (STT giả) trên giọng nói tổng hợp bằng espeak-ng.

Bỏ qua nếu máy không có espeak-ng. Mục đích: xác nhận quy tắc "đoạn đã đóng" khớp hành vi thật của Silero và
không cắt giữa câu khi chunk 5 giây rơi vào giữa lời nói.
"""
import shutil
import subprocess
import time

import numpy as np
import pytest

pytest.importorskip("faster_whisper")
pytestmark = pytest.mark.skipif(shutil.which("espeak-ng") is None, reason="cần espeak-ng")

from faster_whisper import decode_audio
from faster_whisper.vad import VadOptions, get_speech_timestamps

from tests.test_live import H, SID, FakeLiveEngine, chunks_of, finalize, make, put, result, segs, sil, start, wait_until

SR = 16000


class RealVadEngine(FakeLiveEngine):
    def __init__(self):
        super().__init__()
        self.opts = VadOptions(threshold=0.4, min_silence_duration_ms=700, min_speech_duration_ms=250,
                               speech_pad_ms=500, max_speech_duration_s=25)

    def detect_speech(self, audio, live=False):
        return get_speech_timestamps(audio, self.opts)


def tts(text, tmp, name, lang="en"):
    out = tmp / f"{name}.wav"
    subprocess.run(["espeak-ng", "-v", lang, "-s", "150", text, "-w", str(out)], check=True, capture_output=True)
    return decode_audio(str(out), sampling_rate=SR)


def test_chunks_arriving_in_realtime_never_cut_inside_an_utterance(tmp_path):
    a = tts("Hello everyone, today we will review the project plan and the deadlines for the next sprint.", tmp_path, "a")
    b = tts("Please send me the report before Friday afternoon.", tmp_path, "b")
    audio = np.concatenate([sil(0.5), a, sil(1.8), b, sil(1.8), a, sil(1.0)])
    eng = RealVadEngine()
    truth = eng.detect_speech(audio)                          # VAD trên TOÀN BỘ audio làm mốc đúng
    assert len(truth) == 3
    with make(tmp_path, engine=eng) as c:
        start(c)
        ch = chunks_of(audio)
        committed_before_end = 0
        for i, x in enumerate(ch):                            # chunk 5 s tới lần lượt, như khi ghi thật
            put(c, tmp_path, i, x)
            wait_until(lambda: c.get(f"/v1/sessions/{SID}", headers=H).json()["next_seq"] == i + 1)
            time.sleep(0.15)
            if i < len(ch) - 1:
                committed_before_end = segs(c)["next"]
        assert committed_before_end >= 1                      # đã có câu chốt trước khi dừng ghi
        finalize(c, len(ch))
        res = result(c)
        assert res["gaps"] == [] and res["segments"]
        wins = [(off + w["start"], off + w["end"]) for _, off, ws in eng.calls for w in ws]
        for t in truth:                                       # mỗi câu nằm TRỌN trong đúng một cửa sổ chép
            s0, e0 = t["start"] / SR, t["end"] / SR
            inside = [w for w in wins if w[0] <= s0 + 0.05 and w[1] >= e0 - 0.05]
            partial = [w for w in wins if w[0] < e0 - 0.05 and w[1] > s0 + 0.05 and w not in inside]
            assert len(inside) == 1 and not partial, (t, wins)
