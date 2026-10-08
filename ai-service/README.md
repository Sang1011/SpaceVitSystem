# ai-service

Dịch vụ AI (Python) cho luồng **upload video/audio**: trích audio, VAD, STT (faster-whisper `large-v3-turbo`) và
tóm tắt bằng Gemini. Chỉ backend ASP.NET Core gọi dịch vụ này; frontend không bao giờ gọi trực tiếp.

## Cấu trúc

```
app/pipeline.py        STT: lõi theo mảng audio + transcribe_file (dùng chung với bench)
app/jobs.py            hàng đợi, 1 worker, tiến độ, transcript từng phần, hủy job
app/summarize.py       gọi Gemini, validate, chuẩn hóa mốc thời gian
app/summary_prompt.py  prompt + schema tóm tắt
app/main.py            API FastAPI /v1
scripts/gpu_smoke_test.py   kiểm tra GPU trong container
spikes/stt/            bench.py, compare.py (công cụ đo, import app.pipeline)
tests/                 test với engine/Gemini giả
```

## Chạy

Cần `AI_SERVICE_TOKEN` (cùng giá trị với `AiService:ApiKey` của backend), `MEDIA_ROOT`, và `ai-service/.env`
(`GEMINI_MODEL`, `GEMINI_API_KEY`; xem `.env.example`). Qua Docker: xem comment đầu `docker-compose.yml`.
Trên host: `uvicorn app.main:create_app --factory --port 8000 --workers 1`. Luôn `--workers 1`.

Thiếu `AI_SERVICE_TOKEN` thì service từ chối khởi động. Lỗi nạp model (ví dụ CUDA) **không** tự lùi về CPU:
`/health` trả 503, job báo `STT_ENGINE_UNAVAILABLE`, và service thử nạp lại ở job kế tiếp.

## Hợp đồng API (header `X-Api-Key` bắt buộc, trừ `/health`)

| Endpoint                             | Mô tả                                                                                                 |
| ------------------------------------ | ----------------------------------------------------------------------------------------------------- |
| `POST /v1/jobs`                      | `{job_id, mode, language, output_language?, media_key?, segments?}` -> 202. Idempotent theo `job_id`. |
| `GET /v1/jobs/{id}`                  | `status, progress, queue_position, segments_ready, audio_sec, error, error_code`                      |
| `GET /v1/jobs/{id}/segments?after=N` | Transcript từng phần: segment có chỉ số >= N                                                          |
| `GET /v1/jobs/{id}/result`           | Segments, summary, summary_error, stages (timing), config. 409 nếu chưa có kết quả                    |
| `DELETE /v1/jobs/{id}`               | Đang chờ: hủy. Đang chạy: yêu cầu hủy. Đã xong: xóa kết quả                                           |
| `GET /health`                        | `ok` / `loading` / `degraded` (503)                                                                   |

`mode`: `transcribe` (không gọi Gemini), `summarize` (STT rồi Gemini), `resummarize` (chỉ Gemini, cần `segments`).
`media_key` là đường dẫn **tương đối** trong `MEDIA_ROOT` (path traversal bị chặn).
`status`: `QUEUED, PREPROCESSING, TRANSCRIBING, ANALYZING, COMPLETED, PARTIAL_FAILED, FAILED, CANCELLED`.
`PARTIAL_FAILED` = STT xong nhưng Gemini lỗi: transcript vẫn lấy được, chạy lại bằng `resummarize`.
`progress` (0..1): với `summarize`, STT chiếm 0..0.95 và tóm tắt phần còn lại.

## Cấu hình STT (biến môi trường)

`STT_DEVICE` (auto/cuda/cpu), `STT_COMPUTE_TYPE`, `STT_BATCH` (8; 0 = tuần tự), `STT_BEAM` (1), `STT_CPU_THREADS`,
`STT_VAD`, `STT_VAD_THRESHOLD` (0.4), `STT_VAD_PAD_MS` (500), `STT_VAD_MIN_SILENCE_MS` (300), `MAX_AUDIO_SEC` (7260).

## Test và đo

```bash
pip install -r requirements-dev.txt
python -m pytest -q tests                      # không cần GPU, model hay Gemini
cd spikes/stt && STT_TAG=-new python bench.py whisper-large-v3-turbo vi
```
