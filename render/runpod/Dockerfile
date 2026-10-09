# RunPod Serverless 生圖 worker：官方 worker-comfyui 加一個動漫 SDXL checkpoint。
# 由 RunPod 的 GitHub 整合建置，不必在本機 docker build；步驟見同目錄的 README.md。
# 改這個檔之後要建一個 GitHub release，RunPod 才會重建（push 不會觸發）。
FROM runpod/worker-comfyui:5.10.0-base

# NoobAI-XL 1.1：epsilon 版（不是 v-pred，用一般的 KSampler 設定即可），Danbooru tag，授權 FAIPL-1.0-SD。
# 知識庫裡動漫系提示詞最多的是 NoobAI 那層（scripts/pipeline/strata.py），先用它。
# --filename 要跟 render/workflows/txt2img-sdxl.json 的 ckpt_name 一致（scripts/tests/test_render_spike.py 會檢查）。
RUN comfy model download \
    --url https://huggingface.co/Laxhar/noobai-XL-1.1/resolve/main/NoobAI-XL-v1.1.safetensors \
    --relative-path models/checkpoints \
    --filename noobai-xl-1.1.safetensors
