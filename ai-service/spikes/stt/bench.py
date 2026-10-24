import json
import os
import re
import sys
import time
import unicodedata
from pathlib import Path

import jiwer

# Pipeline STT dùng chung với AI service nằm ở ai-service/app/pipeline.py (bench.py chỉ còn là công cụ đo)
sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from app.pipeline import audio_seconds, load_faster_whisper  # noqa: E402

SAMPLES = Path("samples")
RESULTS = Path("results")
# Thêm đuôi video để test trực tiếp luồng upload video (faster-whisper/PyAV tự lấy audio stream)
AUDIO_EXT = {".wav", ".mp3", ".m4a", ".flac", ".ogg", ".mp4", ".mkv", ".mov", ".webm"}


def normalize(text: str) -> str:
    text = unicodedata.normalize("NFC", text).lower()
    text = re.sub(r"[^\w\s]", " ", text)  # bỏ dấu câu
    return re.sub(r"\s+", " ", text).strip()


# ---------- Adapter: mỗi model trả về hàm transcribe(path, lang) -> dict ----------
# dict: text, segments[{start,end,text}], stages{extract_sec,vad_sec,stt_sec,merge_sec}, speech_sec, decoded_sec
# Adapter faster-whisper nằm ở app/pipeline.py (load_faster_whisper), dùng chung với service.


def load_hf_asr(model_id: str):
    # Cần: pip install torch transformers
    # Chỉ dùng làm mốc so sánh độ chính xác: pipeline này chạy tuần tự, không VAD/batch nên rất chậm với audio dài.
    from transformers import pipeline

    asr = pipeline("automatic-speech-recognition", model=model_id, chunk_length_s=30)

    def transcribe(path: Path, lang: str) -> dict:
        return {"text": asr(str(path))["text"]}

    return transcribe


MODELS = {
    "whisper-large-v3-turbo": lambda: load_faster_whisper("large-v3-turbo"),
    "phowhisper-medium": lambda: load_hf_asr("vinai/PhoWhisper-medium"),
    # Gipformer, ChunkFormer, Parakeet: thêm adapter theo hướng dẫn của từng model
}


def main(model_key: str, lang: str, only: str | None = None) -> None:
    files = sorted(
        p for p in SAMPLES.iterdir()
        if p.suffix.lower() in AUDIO_EXT and p.name.startswith(f"{lang}_")
    )
    if only:
        files = [p for p in files if only in (p.stem, p.name)]
    if not files:
        print(f"Không thấy file audio phù hợp trong {SAMPLES}")
        return

    # STT_TAG để chạy nhiều cấu hình của cùng 1 model mà không ghi đè nhau, rồi so bằng compare.py
    run_key = model_key + os.environ.get("STT_TAG", "")
    transcribe = MODELS[model_key]()
    config = getattr(transcribe, "config", {})
    text_dir = RESULTS / run_key
    text_dir.mkdir(parents=True, exist_ok=True)
    rows = []

    for audio in files:
        dur = audio_seconds(audio)

        start = time.perf_counter()
        res = transcribe(audio, lang)
        elapsed = time.perf_counter() - start  # = extract + vad + stt + merge (chưa gồm Gemini)
        hyp_raw = res["text"]
        stages = {k: round(v, 1) for k, v in res.get("stages", {}).items()}

        row = {
            "file": audio.name,
            "audio_sec": round(dur, 1),
            "elapsed_sec": round(elapsed, 1),
            "rtfx": round(dur / elapsed, 1),   # càng cao càng tốt (audio_sec / thời gian xử lý)
            "rtf": round(elapsed / dur, 3),    # càng thấp càng tốt; < 1 là nhanh hơn realtime
            "stages": {**stages, "gemini_sec": None},  # Gemini được đo ở service (job.stages.gemini_sec), bench chỉ đo STT
            "speech_sec": round(res["speech_sec"], 1) if "speech_sec" in res else None,
            "decoded_sec": round(res["decoded_sec"], 1) if "decoded_sec" in res else None,
            "config": config,
            "wer": None,
            "hypothesis": hyp_raw,
            "segments": res.get("segments", []),
        }

        # Có file .txt đáp án thì mới tính WER
        ref_path = audio.with_suffix(".txt")
        if ref_path.exists():
            ref = normalize(ref_path.read_text(encoding="utf-8"))
            row["wer"] = round(jiwer.wer(ref, normalize(hyp_raw)) * 100, 2)

        (text_dir / f"{audio.stem}.txt").write_text(hyp_raw, encoding="utf-8")
        rows.append(row)

        wer_text = f"WER {row['wer']}%" if row["wer"] is not None else "WER n/a"
        print(f"{audio.name}: {wer_text}  RTFx {row['rtfx']}  RTF {row['rtf']}  ({row['elapsed_sec']}s / {row['audio_sec']}s audio)")
        if stages:
            ratio = f"  speech {row['speech_sec']}s, decode {row['decoded_sec']}s" if row["speech_sec"] is not None else ""
            print("  stages: " + " | ".join(f"{k[:-4]} {v}s" for k, v in stages.items()) + ratio)

    out = RESULTS / f"{run_key}_{lang}.json"
    if only and out.exists():  # chạy 1 file thì giữ kết quả các file khác, không ghi đè cả JSON
        old = {r["file"]: r for r in json.loads(out.read_text(encoding="utf-8"))}
        old.update({r["file"]: r for r in rows})
        rows_to_save = [old[k] for k in sorted(old)]
    else:
        rows_to_save = rows
    out.write_text(json.dumps(rows_to_save, ensure_ascii=False, indent=2), encoding="utf-8")

    tot_audio = sum(r["audio_sec"] for r in rows)
    tot_time = max(sum(r["elapsed_sec"] for r in rows), 0.1)  # tránh chia 0 khi chạy quá nhanh
    print(f"TOTAL: {tot_audio:.0f}s audio in {tot_time:.0f}s -> RTFx {tot_audio / tot_time:.1f}, RTF {tot_time / tot_audio:.3f}")
    print(f"Saved {out}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)