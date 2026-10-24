"""Test API + hàng đợi với engine/summarizer giả: không cần GPU, model hay Gemini."""
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.config import Settings
from app.main import create_app
from app.pipeline import JobCancelled
from app.summarize import SummarizeError, snap_timestamps

H = {"X-Api-Key": "secret"}
SEGS = [{"start": 0.0, "end": 4.0, "text": "xin chào"}, {"start": 65.2, "end": 70.0, "text": "chốt deadline"}]


class FakeEngine:
    config = {"device": "cpu", "model": "fake"}

    def __init__(self, delay=0.0):
        self.delay = delay

    def transcribe_file(self, path, lang, on_stage=None, on_segment=None, should_cancel=None, on_idle=None):
        on_stage("PREPROCESSING", {})
        on_stage("TRANSCRIBING", {"audio_sec": 100.0, "speech_sec": 60.0, "decoded_sec": 61.0})
        for s in SEGS:
            if should_cancel():
                raise JobCancelled()
            time.sleep(self.delay)
            on_segment(s["start"], s["end"], s["text"])
        return {"text": "x", "segments": SEGS, "stages": {"extract_sec": 0.1, "vad_sec": 0.1, "stt_sec": 0.5, "merge_sec": 0.0},
                "audio_sec": 100.0, "speech_sec": 60.0, "decoded_sec": 61.0}


class FakeSummarizer:
    def __init__(self, fail=False):
        self.fail, self.calls = fail, 0

    def summarize(self, segments, output_lang, duration_sec):
        self.calls += 1
        if self.fail:
            raise SummarizeError("Gemini down")
        return {"title": "T", "overview": "O", "key_points": [], "n_segments": len(segments)}


def make(tmp_path, engine=None, summ=None, factory=None):
    (tmp_path / "uploads").mkdir()
    (tmp_path / "uploads" / "a.wav").write_bytes(b"x")
    s = Settings("secret", tmp_path, "m", 7260, 7, "", "", "")
    app = create_app(s, factory or (lambda: engine or FakeEngine()), summ or FakeSummarizer(), probe_duration=lambda p: 100.0)
    return TestClient(app)


def wait(c, job_id, want=("COMPLETED", "PARTIAL_FAILED", "FAILED", "CANCELLED"), timeout=5):
    end = time.time() + timeout
    while time.time() < end:
        st = c.get(f"/v1/jobs/{job_id}", headers=H).json()
        if st["status"] in want:
            return st
        time.sleep(0.02)
    raise AssertionError(f"timeout, last={st}")


def body(job_id="job-00000001", mode="summarize", **kw):
    return {"job_id": job_id, "mode": mode, "language": "vi", "media_key": "uploads/a.wav", **kw}


def test_auth_and_validation(tmp_path):
    with make(tmp_path) as c:
        assert c.post("/v1/jobs", json=body()).status_code == 401
        assert c.post("/v1/jobs", json=body(), headers={"X-Api-Key": "bad"}).status_code == 401
        assert c.post("/v1/jobs", json={**body(), "job_id": "../x"}, headers=H).status_code == 422
        assert c.post("/v1/jobs", json={**body(), "media_key": None}, headers=H).status_code == 422
        assert c.post("/v1/jobs", json={"job_id": "job-00000009", "mode": "resummarize"}, headers=H).status_code == 422
        assert c.get("/health").json()["status"] in ("ok", "loading")


def test_summarize_flow_idempotent_and_cleanup(tmp_path):
    summ = FakeSummarizer()
    with make(tmp_path, summ=summ) as c:
        r = c.post("/v1/jobs", json=body(), headers=H)
        assert r.status_code == 202 and r.json()["status"] == "QUEUED"
        assert c.post("/v1/jobs", json=body(), headers=H).status_code == 202  # idempotent
        st = wait(c, "job-00000001")
        assert st["status"] == "COMPLETED" and st["progress"] == 1.0
        res = c.get("/v1/jobs/job-00000001/result", headers=H).json()
        assert res["segments"] == SEGS and res["summary"]["n_segments"] == 2 and summ.calls == 1
        assert {"extract_sec", "stt_sec", "gemini_sec", "total_sec"} <= set(res["stages"])
        part = c.get("/v1/jobs/job-00000001/segments?after=1", headers=H).json()
        assert part["next"] == 2 and part["segments"][0]["index"] == 1
        assert (tmp_path / "results" / "job-00000001.json").exists()
        # submit lại sau khi xong: không chạy lại
        c.post("/v1/jobs", json=body(), headers=H)
        assert summ.calls == 1
        assert c.delete("/v1/jobs/job-00000001", headers=H).json()["action"] == "deleted"
        assert c.get("/v1/jobs/job-00000001", headers=H).status_code == 404


def test_transcribe_mode_never_calls_gemini(tmp_path):
    summ = FakeSummarizer()
    with make(tmp_path, summ=summ) as c:
        c.post("/v1/jobs", json=body(mode="transcribe"), headers=H)
        assert wait(c, "job-00000001")["status"] == "COMPLETED"
        assert summ.calls == 0 and c.get("/v1/jobs/job-00000001/result", headers=H).json()["summary"] is None


def test_summary_failure_keeps_transcript_then_resummarize(tmp_path):
    summ = FakeSummarizer(fail=True)
    with make(tmp_path, summ=summ) as c:
        c.post("/v1/jobs", json=body(), headers=H)
        st = wait(c, "job-00000001")
        assert st["status"] == "PARTIAL_FAILED" and st["error_code"] == "SUMMARY_FAILED"
        res = c.get("/v1/jobs/job-00000001/result", headers=H).json()
        assert res["segments"] == SEGS and res["summary"] is None and "Gemini down" in res["summary_error"]
        summ.fail = False
        c.post("/v1/jobs", json={"job_id": "job-00000002", "mode": "resummarize", "language": "vi", "segments": SEGS}, headers=H)
        assert wait(c, "job-00000002")["status"] == "COMPLETED"
        assert c.get("/v1/jobs/job-00000002/result", headers=H).json()["summary"]["n_segments"] == 2


def test_media_key_safety_and_missing(tmp_path):
    with make(tmp_path) as c:
        for i, key in enumerate(["../etc/passwd", "/etc/passwd", "uploads/../../x", "C:/x.wav", "uploads/none.wav"]):
            jid = f"job-9000000{i}"
            c.post("/v1/jobs", json=body(jid, media_key=key), headers=H)
            st = wait(c, jid)
            assert st["status"] == "FAILED" and st["error_code"] in ("MEDIA_KEY_INVALID", "MEDIA_NOT_FOUND"), (key, st)


def test_too_long(tmp_path):
    (tmp_path / "uploads").mkdir()
    (tmp_path / "uploads" / "a.wav").write_bytes(b"x")
    s = Settings("secret", tmp_path, "m", 7260, 7, "", "", "")
    with TestClient(create_app(s, lambda: FakeEngine(), FakeSummarizer(), probe_duration=lambda p: 9000.0)) as c:
        c.post("/v1/jobs", json=body(), headers=H)
        st = wait(c, "job-00000001")
        assert st["status"] == "FAILED" and st["error_code"] == "AUDIO_TOO_LONG"


def test_queue_order_position_and_cancel(tmp_path):
    with make(tmp_path, engine=FakeEngine(delay=0.3)) as c:
        for i in (1, 2, 3):
            c.post("/v1/jobs", json=body(f"job-0000000{i}"), headers=H)
        time.sleep(0.15)
        assert c.get("/v1/jobs/job-00000003", headers=H).json()["queue_position"] == 2
        assert c.delete("/v1/jobs/job-00000002", headers=H).json()["action"] == "cancelled"  # đang chờ
        assert c.get("/v1/jobs/job-00000003", headers=H).json()["queue_position"] == 1
        assert c.delete("/v1/jobs/job-00000001", headers=H).json()["action"] == "cancel_requested"  # đang chạy
        assert wait(c, "job-00000001")["status"] == "CANCELLED"
        assert wait(c, "job-00000003")["status"] == "COMPLETED"
        assert c.get("/v1/jobs/job-00000002", headers=H).json()["status"] == "CANCELLED"
        assert c.get("/v1/jobs/job-00000001/result", headers=H).status_code == 409


def test_engine_load_failure_fails_jobs_not_hangs(tmp_path):
    def boom():
        raise RuntimeError("CUDA unavailable")
    with make(tmp_path, factory=boom) as c:
        c.post("/v1/jobs", json=body(mode="transcribe"), headers=H)
        st = wait(c, "job-00000001")
        assert st["status"] == "FAILED" and st["error_code"] == "STT_ENGINE_UNAVAILABLE"
        assert c.get("/health").status_code == 503


def test_restart_serves_persisted_result(tmp_path):
    with make(tmp_path) as c:
        c.post("/v1/jobs", json=body(mode="transcribe"), headers=H)
        wait(c, "job-00000001")
    with TestClient(create_app(Settings("secret", tmp_path, "m", 7260, 7, "", "", ""), lambda: FakeEngine(), FakeSummarizer())) as c2:
        assert c2.get("/v1/jobs/job-00000001/result", headers=H).json()["segments"] == SEGS
        assert c2.get("/v1/jobs/job-unknown01", headers=H).status_code == 404


def test_snap_timestamps():
    segs = [{"start": 0, "end": 5, "text": "a"}, {"start": 3725.4, "end": 3730, "text": "b"}]
    s = {"key_points": [{"text": "x", "timestamp": "00:00:00"}, {"text": "y", "timestamp": "01:02:09"}, {"text": "z", "timestamp": None}],
         "topics": [{"title": "t", "summary": "s", "timestamp": "00:00:30"}], "quality_notes": ["n"]}
    out = snap_timestamps(s, segs)
    assert out["key_points"][0]["timestamp"] == "00:00:00"
    assert out["key_points"][1]["timestamp"] == "01:02:05"   # kéo về dòng thật gần nhất ở trước
    assert out["key_points"][2]["timestamp"] is None
    assert out["topics"][0]["timestamp"] == "00:00:00"


def test_engine_load_retries_on_next_job(tmp_path):
    attempts = {"n": 0}

    def flaky():
        attempts["n"] += 1
        if attempts["n"] == 1:
            raise RuntimeError("network down")
        return FakeEngine()
    with make(tmp_path, factory=flaky) as c:
        c.post("/v1/jobs", json=body("job-00000001", mode="transcribe"), headers=H)
        assert wait(c, "job-00000001")["status"] == "COMPLETED"  # lần thử thứ 2 thành công
        assert c.get("/health").json()["status"] == "ok"
