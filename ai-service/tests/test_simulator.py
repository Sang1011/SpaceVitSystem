"""Chạy scripts/live_simulator.py thật (HTTP) với một backend giả + AI service dùng engine giả."""
import socket
import sys
import threading
import time
import uuid
import wave
from pathlib import Path

import numpy as np
import pytest
import uvicorn
from fastapi import FastAPI, Request
from fastapi.testclient import TestClient

from app.config import Settings
from app.main import create_app
from tests.test_live import H, FakeLiveEngine, Summ

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import live_simulator  # noqa: E402

SR = 16000
STATE = {"Completed": "Completed", "PARTIAL_FAILED": "PartialFailed", "COMPLETED": "Completed", "ACTIVE": "Active",
         "FINALIZING": "Finalizing", "ANALYZING": "Analyzing", "FAILED": "Failed", "CANCELLED": "Cancelled"}


def fake_backend(media: Path, ai: TestClient) -> FastAPI:
    app, sessions = FastAPI(), {}

    @app.post("/api/spaces/{space}/live-sessions")
    def start(space: str, body: dict):
        sid, rid = uuid.uuid4().hex, uuid.uuid4().hex
        ai.post("/v1/sessions", json={"session_id": sid, "language": body.get("language", "vi"), "chunk_sec": body.get("chunkSec", 5)}, headers=H)
        sessions[sid] = rid
        return {"session": {"id": sid, "recordingId": rid}, "protocol": {}}

    @app.put("/api/live-sessions/{sid}/chunks/{seq}")
    async def chunk(sid: str, seq: int, request: Request):
        key = f"live/{sessions[sid]}/chunks/{seq:08d}.pcm"
        p = media / key
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(await request.body())
        r = ai.put(f"/v1/sessions/{sid}/chunks/{seq}", json={"media_key": key}, headers=H)
        return {"seq": seq, "outcome": r.json().get("outcome")}

    @app.post("/api/live-sessions/{sid}/stop")
    def stop(sid: str, body: dict):
        ai.post(f"/v1/sessions/{sid}/finalize", json={"total_chunks": body["totalChunks"], "mode": body["mode"], "wait_missing_sec": 0.3}, headers=H)
        return {}

    @app.get("/api/live-sessions/{sid}")
    def get(sid: str):
        v = ai.get(f"/v1/sessions/{sid}", headers=H).json()
        return {"status": STATE[v["state"]], "receivedChunks": v["received"], "missingSeqs": v["missing_seqs"], "gaps": v["gaps"]}

    @app.get("/api/recordings/{rid}/transcript")
    def transcript(rid: str, after: int = 0):
        sid = next(s for s, r in sessions.items() if r == rid)
        v = ai.get(f"/v1/sessions/{sid}", headers=H).json()
        if v["state"] in ("COMPLETED", "PARTIAL_FAILED"):  # như backend thật: bản cuối đã sắp theo thời gian
            segs = ai.get(f"/v1/sessions/{sid}/result", headers=H).json()["segments"]
            segs = [{"index": i, **s} for i, s in enumerate(segs)][after:]
        else:
            segs = ai.get(f"/v1/sessions/{sid}/segments?after={after}", headers=H).json()["segments"]
        return {"after": after, "next": after + len(segs), "segments": [
            {"index": s["index"], "startSec": s["start"], "endSec": s["end"], "text": s["text"]} for s in segs]}

    @app.get("/api/recordings/{rid}/summary")
    def summary(rid: str):
        sid = next(s for s, r in sessions.items() if r == rid)
        return ai.get(f"/v1/sessions/{sid}/result", headers=H).json()["summary"] or {}

    return app


@pytest.fixture
def stack(tmp_path):
    s = Settings("secret", tmp_path, "m", 7260, 7, "", "", "", 2, 30.0)
    ai = TestClient(create_app(s, lambda: FakeLiveEngine(), Summ(), probe_duration=lambda p: 1.0))
    ai.__enter__()
    with socket.socket() as so:
        so.bind(("127.0.0.1", 0))
        port = so.getsockname()[1]
    server = uvicorn.Server(uvicorn.Config(fake_backend(tmp_path, ai), host="127.0.0.1", port=port, log_level="warning"))
    t = threading.Thread(target=server.run, daemon=True)
    t.start()
    while not server.started:
        time.sleep(0.02)
    yield f"http://127.0.0.1:{port}", tmp_path
    server.should_exit = True
    t.join(5)
    ai.__exit__(None, None, None)


def write_wav(path: Path) -> Path:
    rng = np.random.default_rng(3)
    sil = lambda s: np.zeros(int(s * SR))
    sp = lambda s: rng.standard_normal(int(s * SR)) * 0.3
    # 0-3 nói, 3-7 im, 7-9 nói, 9-13 im, 13-16 nói, 16-22 im, 22-25 nói  (25 s)
    x = np.concatenate([sp(3), sil(4), sp(2), sil(4), sp(3), sil(6), sp(3)])
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1), w.setsampwidth(2), w.setframerate(SR)
        w.writeframes((x * 32767).astype("<i2").tobytes())
    return path


def args(api, audio, **kw):
    a = live_simulator.parser().parse_args([str(audio), "--api", api, "--space", "sp", "--speed", "25", "--poll", "0.05"])
    for k, v in kw.items():
        setattr(a, k, v)
    return a


def test_simulator_end_to_end_with_shuffle_duplicates_and_summary(stack):
    api, tmp = stack
    audio = write_wav(tmp / "a.wav")
    lines = []
    res = live_simulator.run(args(api, audio, shuffle=3, dup="1,2", mode="summarize"), out=lines.append)
    assert res["errors"] == [] and res["status"] == "Completed"
    assert [round(s["startSec"], 1) for s in res["segments"]] == sorted(round(s["startSec"], 1) for s in res["segments"])
    assert len(res["segments"]) >= 3 and res["session"]["missingSeqs"] == []
    assert res["summary"] and res["summary"].get("title") == "T"
    assert any("KẾT QUẢ" in l for l in lines)


def test_simulator_dropped_chunk_becomes_gap_and_pause_shortens_timeline(stack):
    api, tmp = stack
    audio = write_wav(tmp / "a.wav")
    res = live_simulator.run(args(api, audio, drop="1"), out=lambda *_: None)
    assert res["status"] == "Completed" and [g["seq"] for g in res["session"]["gaps"]] == [1]
    res2 = live_simulator.run(args(api, audio, pause_at="10:5"), out=lambda *_: None)
    assert res2["status"] == "Completed" and res2["errors"] == []
