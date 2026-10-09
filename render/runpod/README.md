# RunPod Serverless 生圖 worker

生圖後端的試用（spike）：官方 [worker-comfyui](https://github.com/runpod-workers/worker-comfyui) 加一個動漫 SDXL checkpoint，部署成 RunPod Serverless endpoint，再用 [`scripts/render_spike.py`](../../scripts/render_spike.py) 量延遲與費用。為什麼選 RunPod、要量什麼，見 [ComfyUI 整合可行性](../../docs/ComfyUI整合可行性.md) §2、§9。

正式的 API（[定稿後生成預覽](../../docs/superpowers/specs/2026-10-09-render-preview-design.md)）用做法 B 的 endpoint；金鑰除了 spike 腳本，也由 api 容器從 `.env` 的 `RUNPOD_API_KEY`、`RUNPOD_ENDPOINT_ID` 讀（docker compose 映射成 `Render__ApiKey`、`Render__EndpointId`）。

| 檔案 | 內容 |
| :--- | :--- |
| [`Dockerfile`](Dockerfile) | 做法 B 用：`runpod/worker-comfyui:5.10.0-base` 加 NoobAI-XL 1.1（epsilon 版，Danbooru tag，授權 FAIPL-1.0-SD） |
| [`../workflows/txt2img-sdxl.json`](../workflows/txt2img-sdxl.json) | ComfyUI 的 API 格式 workflow：832×1216、28 步、Euler a、CFG 5。提示詞與 seed 由呼叫端填（節點 6、7、3） |

模型有兩種放法，endpoint 吃的 workflow 與 API 都一樣：

2026-10-09 兩種都部署了、都保留，實測比較見[可行性文件](../../docs/ComfyUI整合可行性.md) §9.1；做法 B 跑 20 張的結果見 §9.2。

| | A. 網路磁碟 | B. 模型包進映像檔 |
| :--- | :--- | :--- |
| 怎麼做 | 官方 base 映像檔，模型放在網路磁碟上 | Runpod 的 GitHub 整合從 `Dockerfile` 建置 |
| 誰能做 | 全部可以透過 Runpod 的 MCP connector 完成（Claude 代做） | 要在 Runpod 網頁上連 GitHub、建 endpoint |
| 多出的費用 | 網路磁碟每 GB 每月 US$0.07（15 GB 約 US$1.05） | 無 |
| 限制 | endpoint 只能用磁碟所在資料中心的 GPU | 任何資料中心 |
| 冷啟動載入模型 | 從網路磁碟，實測約 20 秒 | 從本機磁碟，實測約 4 秒 |
| 第一次部署 | 下載模型約 25 秒 | GitHub 建置約 12.5 分鐘 |
| 換模型 | 開一台 CPU 機器下載到磁碟 | 改 `Dockerfile`、建 GitHub release |

兩種做法都要先在 [runpod.io](https://www.runpod.io) 註冊並儲值。Runpod 是預付制，餘額用完就停，所以**先少量儲值，餘額就是花費上限**。

## 做法 A：網路磁碟

在 US-IL-1 建置（2026-10-09 查詢時那裡 4090 的 serverless 庫存是 HIGH，也有 CPU 機器與 STANDARD 網路磁碟）：

1. **網路磁碟**：`prompt-copilot-models`，15 GB，US-IL-1。
2. **下載模型**：開一台 CPU 機器（cpu3c、2 vCPU，每小時 US$0.06），映像檔 `alpine:3.20`，把磁碟掛在 `/runpod-volume`，啟動指令 `/bin/sh -c` 執行：

   ```sh
   set -e; D=/runpod-volume/models/checkpoints; F=noobai-xl-1.1.safetensors; mkdir -p $D
   apk add --no-cache curl coreutils
   curl -L --fail --retry 5 -sS -o $D/$F.part https://huggingface.co/Laxhar/noobai-XL-1.1/resolve/main/NoobAI-XL-v1.1.safetensors
   echo '6681e8e4b134c81f16533acedb0d406d7e5e366e1624b4105178c64d00b05d51  '$D/$F.part | sha256sum -c -
   mv $D/$F.part $D/$F; echo STAGING_DONE; sleep infinity
   ```

   7.1 GB 約 25 秒下載完，log 出現 `STAGING_DONE` 後就刪掉這台機器。
3. **endpoint**：`prompt-copilot-render`，映像檔 `runpod/worker-comfyui:5.10.0-base`，掛上同一個磁碟、資料中心固定 US-IL-1、GPU pool `ADA_24`（4090，每小時 US$1.10）、CUDA 12.8 以上、Active Workers 0、Max Workers 1、Idle Timeout 5 秒、FlashBoot 開、Execution Timeout 180 秒、Container Disk 15 GB。serverless worker 把磁碟掛在 `/runpod-volume`，worker-comfyui 會在 `/runpod-volume/models/checkpoints` 找 workflow 裡 `ckpt_name` 指的檔案。

## 做法 B：GitHub 建置映像檔（第一次）

1. **連 GitHub**：RunPod 設定裡連接 GitHub，授權時選「Only select repositories」，只勾這個 repo。一個 RunPod 帳號只能連一個 GitHub 帳號。
2. **建 endpoint**：Serverless → New Endpoint → 從 GitHub repo 建立，填：

   | 欄位 | 值 | 為什麼 |
   | :--- | :--- | :--- |
   | Repository／Branch | 這個 repo；分支選 Dockerfile 所在的分支（合併前是開發分支，合併後改 `master`） | |
   | Dockerfile Path | `render/runpod/Dockerfile` | |
   | GPU | 首選 24 GB PRO（4090，每小時 US$1.10）；可再勾 24 GB（L4、A5000、3090，每小時 US$0.69）當備援 | SDXL 至少要 8 GB；多勾一種比較不會等不到機器 |
   | Active Workers | `0` | 沒人用時不計費 |
   | Max Workers | `1` | 對應後端設計裡的槽位數；試用期間也限制同時花錢的 worker 數 |
   | Idle Timeout | `5` 秒 | spike 期間用預設；之後做暖機時再調長 |
   | FlashBoot | 開 | 縮短冷啟動 |
   | Execution Timeout | `120` 秒 | 單張卡住時不要一直計費 |
   | Container Disk | `20` GB | 映像檔約 7 GB 的模型加 base |
   | 環境變數 | 不設 | 沒設 S3 時圖片以 base64 回傳，spike 腳本只收這種 |

   建立時出現「`runpod.serverless.start()` handler not found in your repo」的警告可以忽略：Runpod 只在 repo 的原始碼裡找這個呼叫，但 handler 在 base 映像檔裡（`/handler.py`，由映像檔的 `CMD ["/start.sh"]` 啟動），我們的 `Dockerfile` 沒有覆寫 `CMD`。

3. **等建置完成**：endpoint 的 Builds 分頁出現 Completed（要下載約 7 GB 的模型，會花一些時間）。

## 金鑰與 endpoint id（兩種做法都要）

Settings → API Keys 建一把 key（可以選權限的話，給能呼叫 serverless endpoint 的最小權限）；endpoint 的 Overview 頁有 endpoint id。**金鑰絕對不要 commit、不要貼進 issue、PR 或聊天**，這個 repo 是公開的。放在跑 spike 的那台機器上，二選一：

- **在自己電腦上跑**：在自己電腦上 clone 下來的資料夾根目錄建一個 `.env`（跟 `.env.example` 同一層）。`.gitignore` 第一條就是 `.env`，git 不會追蹤它，`git status` 也看不到它；不放心可以跑 `git check-ignore -v .env` 確認。

  ```dotenv
  RUNPOD_API_KEY=...
  RUNPOD_ENDPOINT_ID=...
  ```

- **在 Claude Code 雲端 session 裡跑**：不要建檔案，改在雲端環境的設定裡加（session 標題列的環境選單 → Edit）：`RUNPOD_API_KEY` 放在 Network secrets（舊版 app 叫 API credentials；沒有這一區就放環境變數），`RUNPOD_ENDPOINT_ID` 放環境變數。新開的 session 才讀得到。`render_spike.py` 兩邊都讀，同一個名字兩邊都有時以環境變數為準。

不用了就到 Runpod 把這把 key 刪掉。

## 跑 spike

```bash
cd scripts
python render_spike.py --gpu-price-per-hour 1.10            # 五個提示詞各一張，第一張含冷啟動
python render_spike.py --gpu-price-per-hour 1.10 --runs 4   # 五個提示詞各四張，共 20 張，P50／P95 才有意義
```

圖與報表存在 `scripts/data/render_spike/<時間>/`（不進版控）：`report.md` 是每張的 delayTime（排隊＋冷啟動）、executionTime、來回總時間、P50／P95、估計費用，最後附每個提示詞要看的要素；`results.json` 是原始數字。

- **量冷啟動**：等 worker 閒置超過 idle timeout、縮回 0（endpoint 頁面的 worker 數變成 0）之後再跑，報表的「第一張的 delayTime」就是冷啟動。建議隔 30 分鐘以上再量一次。
- **費用以帳單為準**：腳本的估計只用 GPU 每小時價格乘上秒數；RunPod 從 worker 啟動算到停止，跑之前跟跑之後各看一次餘額最準。報表有兩種費用：「這一輪估計」是這次連續跑實際大約花多少（idle timeout 只在最後算一次），拿來對照餘額差；「每張費用」的零星使用是正式上線時一張一張零散生圖的估計（每張都各自等一次 delayTime 與 idle timeout），拿來估每月預算。
- **看圖**：生成的圖只留在本機，不要提交進 repo。動漫模型就算提示詞乾淨也可能生出不當內容，負向詞已固定帶 `nsfw`，正式整合時每張圖都要過審查（可行性 §8）。

## 換模型或升級

候選模型與為什麼要換，見[可行性文件](../../docs/ComfyUI整合可行性.md) §9.3。先在做法 A 的磁碟上試，選定之後才改 `Dockerfile`。動手前先看模型頁的架構（Base Model）與授權：

| 類型 | 例子 | 要改什麼 |
| :--- | :--- | :--- |
| SDXL epsilon 版 | Illustrious／NoobAI epsilon 的調整版、合併模型 | 只換檔：`ckpt_name` 改新檔名，`steps`、`cfg`、`sampler_name` 照模型頁的建議 |
| SDXL v-pred 版 | NoobAI-XL v-pred | 同上，再加一個 `ModelSamplingDiscrete` 節點（`v_prediction`）；不加的話圖會整張灰掉或壞掉 |
| 不同架構 | Anima、Lumina 系 | 另做一份 workflow（模型頁或 ComfyUI 內建範本，用「Export (API)」匯出）；確認 worker-comfyui 映像檔裡的 ComfyUI 版本支援；`render_spike.py` 還不能指定別的 workflow，要先改 |

**做法 A**：照「做法 A」第 2 步，把新的 checkpoint 下載到同一個磁碟（磁碟不夠大就先加大），workflow 的 `ckpt_name` 改成新檔名；升級 worker-comfyui 版本是改 endpoint 的映像檔 tag。

**做法 B**：改了 `Dockerfile`（換模型、升級 worker-comfyui 版本）之後，**push 不會觸發重建**，要在 GitHub 建一個 release（tag 例如 `render-worker-v2`），RunPod 才會重新建置。建置失敗或新版有問題，可以在 Builds 分頁對舊的建置按 Rollback。

做法 B 換 checkpoint 時，`Dockerfile` 的 `--filename` 與 workflow 的 `ckpt_name` 要一起改；`scripts/tests/test_render_spike.py` 會檢查兩者一致。

## 不用時

Active Workers 為 0 時，沒有工作就不會有 worker 在跑，endpoint 本身不計費。要完全停掉，把 Max Workers 設成 0 或刪掉 endpoint。

做法 A 的網路磁碟不管有沒有用都會計費（15 GB 每月約 US$1.05）。確定不再用才刪磁碟；刪掉之後要用時，得重新建磁碟、下載模型。
