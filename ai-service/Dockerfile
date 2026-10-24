# GPU (mặc định): CUDA 12.8 + cuDNN 9, cần thiết cho GPU Blackwell (RTX 50). Tag cần được xác nhận bằng `docker pull`.
# CPU dự phòng:  docker build --build-arg BASE_IMAGE=ubuntu:24.04 ...   (và đặt STT_DEVICE=cpu)
ARG BASE_IMAGE=nvidia/cuda:12.8.1-cudnn-runtime-ubuntu24.04
FROM ${BASE_IMAGE}

ENV DEBIAN_FRONTEND=noninteractive PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-venv ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN python3 -m venv /opt/venv
ENV PATH=/opt/venv/bin:$PATH

WORKDIR /srv
COPY requirements.txt .
RUN pip install -r requirements.txt
COPY app ./app
COPY scripts ./scripts

ENV MEDIA_ROOT=/data/media
EXPOSE 8000
# --workers 1: mỗi worker uvicorn sẽ nạp model riêng và tranh GPU
CMD ["uvicorn", "app.main:create_app", "--factory", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]
