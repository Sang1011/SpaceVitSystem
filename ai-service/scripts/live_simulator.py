"""Mô phỏng client ghi âm trực tiếp: phát một file audio dưới dạng chunk PCM theo thời gian thực tới BACKEND.

Dùng để kiểm thử đầu-cuối và đo độ trễ khi chưa có giao diện ghi âm (hoặc khi muốn lặp lại cùng một kịch bản).

Ví dụ:
  python scripts/live_simulator.py meeting.mp4 --api http://localhost:5211 --space <spaceId> --mode summarize
  python scripts/live_simulator.py meeting.mp4 --speed 4 --shuffle 3 --drop 4 --dup 2 --pause-at 30:10

Độ trễ in ra = (thời gian thực đã trôi qua quy ra thời gian audio) - (mốc kết thúc segment): segment càng "trễ"
thì người dùng thấy chữ càng muộn so với lúc họ nói. Với chunk 5 giây, kỳ vọng vài giây đầu mỗi câu.
"""
from __future__ import annotations

import argparse
import statistics
import sys
import threading
import time
from pathlib import Path

import httpx
import numpy as np

SR = 16000
TERMINAL = {"Completed", "PartialFailed", "Failed", "Cancelled"}


def load_pcm(path: Path) -> np.ndarray:
    from faster_whisper import decode_audio

    return decode_audio(str(path), sampling_rate=SR)


def parse_ints(text: str) -> set[int]:
    return {int(x) for x in text.split(",") if x.strip()}


def run(args: argparse.Namespace, out=print) -> dict:
    audio = load_pcm(Path(args.audio))
    pause_at, pause_len = None, 0.0
    if args.pause_at:
        a, b = args.pause_at.split(":")
        pause_at, pause_len = float(a), float(b)
        cut = slice(int(pause_at * SR), int((pause_at + pause_len) * SR))
        audio = np.concatenate([audio[: cut.start], audio[cut.stop:]])  # tạm dừng = audio lúc đó không được ghi
        out(f"[pause] bỏ {pause_len:.0f}s audio từ giây {pause_at:.0f}; timeline transcript chỉ tính phần đã ghi")

    pcm = (np.clip(audio, -1, 1) * 32767).astype("<i2")
    chunk_n = int(args.chunk_sec * SR)
    chunks = [pcm[i:i + chunk_n].tobytes() for i in range(0, len(pcm), chunk_n)]
    total = len(chunks)
    drop, dup = parse_ints(args.drop), parse_ints(args.dup)
    out(f"[audio] {len(audio) / SR:.1f}s -> {total} chunk x {args.chunk_sec}s; speed x{args.speed}")

    http = httpx.Client(base_url=args.api.rstrip("/"), timeout=60)
    r = http.post(f"/api/spaces/{args.space}/live-sessions", json={"language": args.language, "chunkSec": args.chunk_sec})
    r.raise_for_status()
    sid, rid = r.json()["session"]["id"], r.json()["session"]["recordingId"]
    out(f"[session] {sid} recording {rid}")

    t0 = time.time()
    sent_log: list[tuple[int, float]] = []
    errors: list[str] = []

    def put(seq: int) -> None:
        resp = http.put(f"/api/live-sessions/{sid}/chunks/{seq}", content=chunks[seq],
                        headers={"Content-Type": "application/octet-stream"})
        if resp.status_code >= 400:
            errors.append(f"chunk {seq}: HTTP {resp.status_code} {resp.text[:120]}")
        sent_log.append((seq, time.time() - t0))

    def sender() -> None:
        # Chunk k chỉ tồn tại sau khi đã ghi đủ (k+1)*chunk_sec giây (cộng thời gian tạm dừng nếu có).
        order = list(range(total))
        w = args.shuffle
        if w > 1:  # xáo thứ tự trong cửa sổ w: mô phỏng chunk tới sai thứ tự
            order = [s for i in range(0, total, w) for s in reversed(order[i:i + w])]
        for seq in order:
            ready = (seq + 1) * args.chunk_sec
            if pause_at is not None and ready > pause_at:
                ready += pause_len
            wait = ready / args.speed - (time.time() - t0)
            if wait > 0:
                time.sleep(wait)
            if seq in drop:
                out(f"[drop] không gửi seq {seq}")
                continue
            put(seq)
            if seq in dup:
                put(seq)  # gửi trùng: backend phải idempotent

    th = threading.Thread(target=sender, daemon=True)
    th.start()

    seen: list[dict] = []
    latencies: list[float] = []
    after = 0
    th_done_at: float | None = None
    stopped = False
    final_session: dict = {}
    deadline = time.time() + len(audio) / SR / args.speed + 600
    while time.time() < deadline:
        time.sleep(args.poll)
        if not th.is_alive() and not stopped:
            th.join()
            lo = http.post(f"/api/live-sessions/{sid}/stop", json={"totalChunks": total, "mode": args.mode})
            if lo.status_code >= 400:
                errors.append(f"stop: HTTP {lo.status_code} {lo.text[:200]}")
                break
            stopped, th_done_at = True, time.time() - t0
            out(f"[stop] mode={args.mode} total_chunks={total} sau {th_done_at:.1f}s")
        page = http.get(f"/api/recordings/{rid}/transcript", params={"after": after}).json()
        for s in page["segments"]:
            lat = (time.time() - t0) * args.speed - s["endSec"]
            if not stopped:
                latencies.append(lat)
            seen.append(s)
            out(f"  [{s['startSec']:7.1f}-{s['endSec']:7.1f}] (trễ {lat:5.1f}s) {s['text']}")
        after = page["next"]
        final_session = http.get(f"/api/live-sessions/{sid}").json()
        if final_session["status"] in TERMINAL:
            break

    final = http.get(f"/api/recordings/{rid}/transcript", params={"after": 0}).json()["segments"] if final_session else []
    summary = None
    if final_session.get("status") in ("Completed", "PartialFailed"):
        sr = http.get(f"/api/recordings/{rid}/summary")
        summary = sr.json() if sr.status_code == 200 else None

    out("\n=== KẾT QUẢ ===")
    out(f"trạng thái: {final_session.get('status')}  chunk nhận: {final_session.get('receivedChunks')}/{total}  "
        f"thiếu: {final_session.get('missingSeqs')}  khoảng trống: {final_session.get('gaps')}")
    out(f"segment cuối: {len(final)}  (nhận dần trong lúc ghi: {len(latencies)})")
    if latencies:
        out(f"độ trễ khi ghi: trung vị {statistics.median(latencies):.1f}s, p95 {sorted(latencies)[int(len(latencies) * .95) - 1]:.1f}s")
    if summary:
        out(f"tóm tắt: {summary.get('title')!r} ({summary.get('content_type')})")
    for e in errors:
        out(f"[LỖI] {e}")
    return {"status": final_session.get("status"), "segments": final, "latencies": latencies, "summary": summary,
            "errors": errors, "session": final_session, "sent": sent_log}


def parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("audio")
    p.add_argument("--api", default="http://localhost:5211", help="URL backend")
    p.add_argument("--space", required=True, help="Id của Space (phải tồn tại trong DB)")
    p.add_argument("--language", default="vi", choices=["vi", "en"])
    p.add_argument("--mode", default="transcribe", choices=["transcribe", "summarize"])
    p.add_argument("--chunk-sec", type=float, default=5.0)
    p.add_argument("--speed", type=float, default=1.0, help="1 = thời gian thực; 4 = nhanh gấp 4")
    p.add_argument("--poll", type=float, default=1.0, help="chu kỳ lấy transcript (giây)")
    p.add_argument("--shuffle", type=int, default=0, help="xáo thứ tự gửi trong cửa sổ N chunk")
    p.add_argument("--drop", default="", help="seq không bao giờ gửi, ví dụ 4,9")
    p.add_argument("--dup", default="", help="seq gửi hai lần, ví dụ 2,3")
    p.add_argument("--pause-at", default="", help="GIÂY:ĐỘ_DÀI, ví dụ 30:10 (bỏ 10 giây audio từ giây 30)")
    return p


if __name__ == "__main__":
    result = run(parser().parse_args())
    sys.exit(0 if result["status"] in ("Completed", "PartialFailed") and not result["errors"] else 1)
