"""Kiểm tra GPU trong container trước khi xây tiếp: CTranslate2 có thấy CUDA và chạy được large-v3-turbo không.

Chạy:  docker compose --profile ai run --rm --no-deps ai-service python scripts/gpu_smoke_test.py
Lần đầu sẽ tải model (~1.6 GB) vào volume hf-cache. Thoát mã 0 = đạt, khác 0 = không đạt.
"""
import sys
import time

import numpy as np


def main() -> int:
    import ctranslate2

    n = ctranslate2.get_cuda_device_count()
    print(f"ctranslate2 {ctranslate2.__version__}; CUDA devices: {n}")
    if n == 0:
        print("FAIL: CTranslate2 không thấy GPU (kiểm tra --gpus/deploy.reservations và phiên bản CUDA của image)")
        return 1
    print("compute types (cuda):", sorted(ctranslate2.get_supported_compute_types("cuda")))

    from faster_whisper import WhisperModel

    t = time.perf_counter()
    model = WhisperModel("large-v3-turbo", device="cuda", compute_type="float16")
    print(f"model loaded in {time.perf_counter() - t:.1f}s")

    # 15 giây nhiễu: ép chạy cả encoder và decoder trên GPU (VAD tắt để không bị bỏ qua)
    audio = (np.random.randn(16000 * 15) * 0.05).astype("float32")
    t = time.perf_counter()
    segs, _ = model.transcribe(audio, language="vi", beam_size=1, vad_filter=False)
    text = " ".join(s.text for s in segs)
    print(f"transcribe OK in {time.perf_counter() - t:.1f}s, output={text[:60]!r}")
    print("PASS")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:  # kernel không hỗ trợ kiến trúc GPU thường lỗi ở đây
        print(f"FAIL: {type(e).__name__}: {e}")
        sys.exit(2)
