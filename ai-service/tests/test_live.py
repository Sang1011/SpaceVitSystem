"""Test luồng live qua API thật với engine giả: VAD giả theo năng lượng, STT giả trả "seg@<mốc bắt đầu>"."""
import time

import numpy as np
import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.main import create_app
from app.summarize import SummarizeError

SR = 16000
H = {"X-Api-Key": "secret"}
SID = "live-session-0001"


class FakeLiveEngine:
    config = {"device": "cpu", "model": "fake"}
    live_commit_silence_sec = 0.7
    live_pad_sec = 0.5

    def __init__(self):
        self.calls = []

    def detect_speech(self, audio, live=False):
        n, frame = len(audio), int(0.03 * SR)
        loud = [float(np.sqrt(np.mean(audio[i:i + frame] ** 2))) > 0.05 for i in range(0, n - frame + 1, frame)]
        spans, start = [], None
        for i, v in enumerate(loud + [False]):
            if v and start is None:
                start = i
            elif not v and start is not None:
                spans.append([start * frame, i * frame])
                start = None
        merged = []
        for s in spans:  # gộp khoảng im lặng ngắn hơn ngưỡng đóng đoạn
            if merged and s[0] - merged[-1][1] < 0.7 * SR:
                merged[-1][1] = s[1]
            else:
                merged.append(s)
        pad = int(0.5 * SR)
        out = [{"start": max(0, s - pad), "end": min(n, e + pad)} for s, e in merged]
        for a, b in zip(out, out[1:]):
            if a["end"] > b["start"]:
                mid = (a["end"] + b["start"]) // 2
                a["end"], b["start"] = mid, mid
        return out

    def transcribe_windows(self, audio, windows, lang, offset_sec=0.0, **kw):
        self.calls.append((len(audio), offset_sec, windows))
        return [(offset_sec + w["start"], offset_sec + w["end"], f"seg@{offset_sec + w['start']:.1f}") for w in windows]


class SlowEngine(FakeLiveEngine):
    """Giả lập job upload dài: gọi on_idle giữa các 'lô' như pipeline thật."""

    def transcribe_file(self, path, lang, on_stage=None, on_segment=None, should_cancel=None, on_idle=None):
        on_stage("TRANSCRIBING", {"audio_sec": 100.0, "speech_sec": 50.0, "decoded_sec": 50.0})
        segs = []
        for i in range(12):
            time.sleep(0.1)
            on_idle()
            on_segment(i * 5.0, i * 5.0 + 4, f"up{i}")
            segs.append({"start": i * 5.0, "end": i * 5.0 + 4, "text": f"up{i}"})
        return {"text": "", "segments": segs, "stages": {"extract_sec": 0, "vad_sec": 0, "stt_sec": 1, "merge_sec": 0},
                "audio_sec": 100.0, "speech_sec": 50.0, "decoded_sec": 50.0}


class Summ:
    def __init__(self, fail=False):
        self.fail, self.calls = fail, []

    def summarize(self, segments, lang, dur):
        self.calls.append(segments)
        if self.fail:
            raise SummarizeError("down")
        return {"title": "T", "overview": "O", "n": len(segments)}


def speech(sec, amp=0.3):
    return (np.random.default_rng(1).standard_normal(int(sec * SR)) * amp).astype(np.float32)


def sil(sec):
    return np.zeros(int(sec * SR), dtype=np.float32)


# 0-3 nói, 3-7 im, 7-9 nói, 9-12 im, 12-14 nói  => 14 s, chunk 5 s: [0-5][5-10][10-14]
PATTERN = np.concatenate([speech(3), sil(4), speech(2), sil(3), speech(2)])


def pcm_key(tmp, sid, seq, samples):
    d = tmp / "live" / sid / "chunks"
    d.mkdir(parents=True, exist_ok=True)
    (d / f"{seq:08d}.pcm").write_bytes((np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes())
    return f"live/{sid}/chunks/{seq:08d}.pcm"


def chunks_of(audio, sec=5):
    n = int(sec * SR)
    return [audio[i:i + n] for i in range(0, len(audio), n)]


def make(tmp, engine=None, summ=None, gap_wait=30.0, max_sessions=2):
    s = Settings("secret", tmp, "m", 7260, 7, "", "", "", max_sessions, gap_wait)
    return TestClient(create_app(s, lambda: engine or FakeLiveEngine(), summ or Summ(), probe_duration=lambda p: 100.0))


def wait_until(fn, timeout=5.0):
    end = time.time() + timeout
    while time.time() < end:
        v = fn()
        if v:
            return v
        time.sleep(0.02)
    raise AssertionError("timeout")


def start(c, sid=SID):
    r = c.post("/v1/sessions", json={"session_id": sid, "language": "vi", "chunk_sec": 5}, headers=H)
    assert r.status_code == 201, r.text
    return r.json()


def put(c, tmp, seq, samples, sid=SID, key=None):
    key = key or pcm_key(tmp, sid, seq, samples)
    return c.put(f"/v1/sessions/{sid}/chunks/{seq}", json={"media_key": key}, headers=H)


def segs(c, sid=SID, after=0):
    return c.get(f"/v1/sessions/{sid}/segments?after={after}", headers=H).json()


def finalize(c, total, mode="transcribe", wait=60, sid=SID):
    return c.post(f"/v1/sessions/{sid}/finalize", json={"total_chunks": total, "mode": mode, "wait_missing_sec": wait}, headers=H)


def result(c, sid=SID):
    wait_until(lambda: c.get(f"/v1/sessions/{sid}", headers=H).json()["state"] in ("COMPLETED", "PARTIAL_FAILED", "FAILED"))
    return c.get(f"/v1/sessions/{sid}/result", headers=H).json()


def starts(res):
    return [round(s["start"], 1) for s in res["segments"]]


def test_incremental_commit_on_silence_with_absolute_timestamps(tmp_path):
    with make(tmp_path) as c:
        start(c)
        ch = chunks_of(PATTERN)
        put(c, tmp_path, 0, ch[0])
        wait_until(lambda: segs(c)["next"] == 1)           # câu 1 chốt TRƯỚC khi ghi xong (đã thấy im lặng)
        put(c, tmp_path, 1, ch[1])
        wait_until(lambda: segs(c)["next"] == 2)
        put(c, tmp_path, 2, ch[2])
        time.sleep(0.4)
        assert segs(c)["next"] == 2                        # câu cuối còn mở: chưa chốt, chưa cắt giữa chừng
        assert finalize(c, 3).status_code == 202
        res = result(c)
        assert res["state"] == "COMPLETED" and starts(res) == [0.0, 6.5, 11.5]
        assert abs(res["audio_sec"] - 14.0) < 0.05 and res["gaps"] == []
        assert segs(c, after=1)["segments"][0]["index"] == 1


def test_out_of_order_waits_then_same_result(tmp_path):
    with make(tmp_path) as c:
        start(c)
        ch = chunks_of(PATTERN)
        put(c, tmp_path, 0, ch[0])
        put(c, tmp_path, 2, ch[2])                          # seq 1 chưa tới: bị chặn
        wait_until(lambda: segs(c)["next"] == 1)
        time.sleep(0.3)
        assert segs(c)["next"] == 1
        v = c.get(f"/v1/sessions/{SID}", headers=H).json()
        assert v["missing_seqs"] == [1] and v["next_seq"] == 1
        put(c, tmp_path, 1, ch[1])
        finalize(c, 3)
        assert starts(result(c)) == [0.0, 6.5, 11.5]


def test_duplicate_idempotent_and_conflict(tmp_path):
    with make(tmp_path) as c:
        start(c)
        ch = chunks_of(PATTERN)
        assert put(c, tmp_path, 0, ch[0]).json()["outcome"] == "accepted"
        assert put(c, tmp_path, 0, ch[0]).json()["outcome"] == "duplicate"
        r = put(c, tmp_path, 0, ch[0], key="live/other/x.pcm")
        assert r.status_code == 409 and r.json()["detail"]["code"] == "SEQ_CONFLICT"


def test_missing_chunk_becomes_gap_on_finalize(tmp_path):
    with make(tmp_path) as c:
        start(c)
        ch = chunks_of(PATTERN)
        put(c, tmp_path, 0, ch[0])
        put(c, tmp_path, 2, ch[2])                          # seq 1 không bao giờ tới
        finalize(c, 3, wait=0.2)
        res = result(c)
        assert res["state"] == "COMPLETED"
        assert [g["seq"] for g in res["gaps"]] == [1] and res["gaps"][0]["start"] == 5.0
        assert abs(res["audio_sec"] - 14.0) < 0.05          # mốc thời gian không bị xê dịch vì chunk thiếu
        assert starts(res)[0] == 0.0 and starts(res)[-1] > 10   # chunk 2 vẫn được chép đúng chỗ


def test_late_chunk_is_transcribed_alone_without_redoing_others(tmp_path):
    eng = FakeLiveEngine()
    with make(tmp_path, engine=eng, gap_wait=0.2) as c:
        start(c)
        ch = chunks_of(PATTERN)
        put(c, tmp_path, 0, ch[0])
        put(c, tmp_path, 2, ch[2])
        wait_until(lambda: c.get(f"/v1/sessions/{SID}", headers=H).json()["gaps"])   # seq 1 bị coi là khoảng trống
        n_calls = len(eng.calls)
        assert put(c, tmp_path, 1, ch[1]).json()["outcome"] == "late"
        wait_until(lambda: not c.get(f"/v1/sessions/{SID}", headers=H).json()["gaps"])
        assert len(eng.calls) == n_calls + 1                # chỉ chép THÊM chunk muộn, không chép lại chunk khác
        finalize(c, 3)
        res = result(c)
        assert res["gaps"] == [] and any(5.0 <= s["start"] < 10.0 for s in res["segments"])


def test_unreadable_chunk_becomes_gap(tmp_path):
    with make(tmp_path) as c:
        start(c)
        ch = chunks_of(PATTERN)
        put(c, tmp_path, 0, ch[0])
        put(c, tmp_path, 1, ch[1], key="live/nope/missing.pcm")
        put(c, tmp_path, 2, ch[2])
        finalize(c, 3)
        res = result(c)
        assert [g["seq"] for g in res["gaps"]] == [1] and "không đọc được" in res["gaps"][0]["reason"]


def test_summarize_mode_and_summary_failure(tmp_path):
    summ = Summ()
    with make(tmp_path, summ=summ) as c:
        start(c)
        for i, x in enumerate(chunks_of(PATTERN)):
            put(c, tmp_path, i, x)
        finalize(c, 3, mode="summarize")
        res = result(c)
        assert res["state"] == "COMPLETED" and res["summary"]["n"] == 3 and "gemini_sec" in res["stages"]
        assert [s["start"] for s in summ.calls[0]] == sorted(s["start"] for s in summ.calls[0])
    with make(tmp_path / "b", summ=Summ(fail=True)) as c:
        (tmp_path / "b").mkdir(exist_ok=True)
        start(c)
        for i, x in enumerate(chunks_of(PATTERN)):
            put(c, tmp_path / "b", i, x)
        finalize(c, 3, mode="summarize")
        res = result(c)
        assert res["state"] == "PARTIAL_FAILED" and res["summary"] is None and len(res["segments"]) == 3


def test_transcribe_mode_never_calls_summarizer(tmp_path):
    summ = Summ()
    with make(tmp_path, summ=summ) as c:
        start(c)
        for i, x in enumerate(chunks_of(PATTERN)):
            put(c, tmp_path, i, x)
        finalize(c, 3)
        result(c)
        assert summ.calls == []


def test_limits_and_state_rules(tmp_path):
    with make(tmp_path, max_sessions=1) as c:
        start(c)
        r = c.post("/v1/sessions", json={"session_id": "live-session-0002"}, headers=H)
        assert r.status_code == 429 and r.json()["detail"]["code"] == "LIVE_BUSY"
        assert start(c)["state"] == "ACTIVE"                # tạo lại cùng id: idempotent, không tính thêm
        put(c, tmp_path, 3, speech(1))
        assert finalize(c, 2).status_code == 400            # có chunk seq >= total
        assert finalize(c, 4).status_code == 202
        assert finalize(c, 4).status_code == 202            # idempotent
        assert finalize(c, 5).status_code == 409
        assert put(c, tmp_path, 4, speech(1)).status_code == 400   # vượt total_chunks
        assert c.get("/v1/sessions/unknown-session", headers=H).status_code == 404
        assert c.put(f"/v1/sessions/{SID}/chunks/0", json={"media_key": "x"}).status_code == 401


def test_empty_session_finalizes(tmp_path):
    with make(tmp_path) as c:
        start(c)
        finalize(c, 0)
        res = result(c)
        assert res["state"] == "COMPLETED" and res["segments"] == [] and res["audio_sec"] == 0


def test_silence_only_does_not_grow_buffer_or_transcribe(tmp_path):
    eng = FakeLiveEngine()
    with make(tmp_path, engine=eng) as c:
        start(c)
        for i in range(4):
            put(c, tmp_path, i, sil(5))
        wait_until(lambda: c.get(f"/v1/sessions/{SID}", headers=H).json()["next_seq"] == 4)
        assert c.get(f"/v1/sessions/{SID}", headers=H).json()["buffered_sec"] <= 1.01
        assert eng.calls == []


def test_cancel_and_cleanup(tmp_path):
    with make(tmp_path) as c:
        start(c)
        assert c.delete(f"/v1/sessions/{SID}", headers=H).json()["action"] == "cancelled"
        assert put(c, tmp_path, 0, speech(1)).status_code == 409
        assert c.delete(f"/v1/sessions/{SID}", headers=H).json()["action"] == "deleted"
        assert c.get(f"/v1/sessions/{SID}", headers=H).status_code == 404


def test_result_survives_restart(tmp_path):
    with make(tmp_path) as c:
        start(c)
        for i, x in enumerate(chunks_of(PATTERN)):
            put(c, tmp_path, i, x)
        finalize(c, 3)
        first = result(c)
    with make(tmp_path) as c2:
        again = c2.get(f"/v1/sessions/{SID}/result", headers=H).json()
        assert again["segments"] == first["segments"]


def test_live_preempts_running_upload_job(tmp_path):
    (tmp_path / "uploads").mkdir()
    (tmp_path / "uploads" / "a.wav").write_bytes(b"x")
    with make(tmp_path, engine=SlowEngine()) as c:
        start(c)
        c.post("/v1/jobs", json={"job_id": "job-00000001", "mode": "transcribe", "language": "vi", "media_key": "uploads/a.wav"}, headers=H)
        wait_until(lambda: c.get("/v1/jobs/job-00000001", headers=H).json()["status"] == "TRANSCRIBING")
        ch = chunks_of(PATTERN)
        put(c, tmp_path, 0, ch[0])
        wait_until(lambda: segs(c)["next"] == 1, timeout=3)   # live được chép khi job upload vẫn đang chạy
        assert c.get("/v1/jobs/job-00000001", headers=H).json()["status"] == "TRANSCRIBING"
        wait_until(lambda: c.get("/v1/jobs/job-00000001", headers=H).json()["status"] == "COMPLETED", timeout=10)


def test_pause_resume_continuous_seq(tmp_path):
    """Pause chỉ khiến client ngừng gửi; seq vẫn liên tục và timeline chỉ tính audio đã ghi."""
    with make(tmp_path) as c:
        start(c)
        ch = chunks_of(np.concatenate([speech(3), sil(3), speech(3), sil(2)]))  # 11 s => chunk 5,5,1 (chunk cuối ngắn)
        assert [len(x) / SR for x in ch] == [5.0, 5.0, 1.0]
        for i, x in enumerate(ch):
            put(c, tmp_path, i, x)
        finalize(c, len(ch))
        res = result(c)
        assert starts(res) == [0.0, 5.5] and abs(res["audio_sec"] - 11.0) < 0.05
