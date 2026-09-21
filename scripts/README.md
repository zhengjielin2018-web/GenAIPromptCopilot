# 資料管線

Civitai 公開 API → 清洗 → Gemini 結構化 → Gemini embedding → PostgreSQL (pgvector)。

## 前置

- Python 3.12+、Docker Desktop
- repo 根目錄 `.env`（由 `.env.example` 複製）填入 `GEMINI_API_KEY`
- `docker compose up -d db`（首次啟動自動執行 `db/init/001_schema.sql`）

## 安裝

    cd scripts
    python -m venv .venv
    .\.venv\Scripts\Activate.ps1
    pip install -r requirements.txt

## 執行

全量（會花時間，可中斷後重跑同一指令續跑）：

    python seed_data.py --max-items 3000

從某階段起跑：

    python seed_data.py --from structure --max-records 200

單一階段：

    python -m pipeline.fetch_civitai --max-items 500
    python -m pipeline.clean
    python -m pipeline.structure --max-records 100
    python -m pipeline.embed [--reindex]
    python -m pipeline.load

## 驗收

    python query_check.py "昏暗雨夜的科幻城市"
    python query_check.py "穿皮夾克的女生" --facet clothing.upper

## 資料流

| 階段 | 讀 | 寫 | 續跑機制 |
| --- | --- | --- | --- |
| fetch | Civitai API | `data/raw/images.jsonl` | `state.json` 的 cursor |
| clean | raw | `data/clean/records.jsonl` | 全量重算（便宜） |
| structure | clean | `data/structured/{histories,presets}.jsonl` | 跳過已有 `source_ref` |
| embed | structured | `data/embedded/*.jsonl` | 跳過已有 `source_ref`；`--reindex` 重算 |
| load | embedded | PostgreSQL | `ON CONFLICT (source_ref)` upsert |

## 換 embedding 模型

1. 改 `.env` 的 `GEMINI_EMBEDDING_MODEL` / `EMBEDDING_DIMENSIONS`
2. 若維度變了：改 `db/init/001_schema.sql` 的 `VECTOR(...)`，然後 `docker compose down -v`、再 `docker compose up -d db`（init 只在 volume 首次建立時執行）
3. `python seed_data.py --from embed --reindex`

## 測試

    python -m pytest              # 單元測試（不需 DB、不需金鑰）
    python -m pytest -m integration   # 需要 DB 已啟動
    ruff check .

## 關於結構化模型的坑

實際使用的結構化模型是 **`gemini-3.5-flash-lite`**（見 `pipeline/config.py` 的
`gemini_structure_model` 預設值）。

開發過程中曾嘗試 `gemini-2.5-flash-lite`：這個型號**會出現在** `client.models.list()`
的清單裡，看起來完全可用，但實際呼叫 `generate_content` 時回傳
`404 ... no longer available to new users`。也就是說，**一個型號出現在 `models.list()`
裡，不代表它真的可以被呼叫** —— 換模型時務必先用一筆真實請求驗證，不要只信清單。

## Known limitations（誠實說明，不美化）

NSFW 過濾實際上分三層：

1. Civitai API 查詢參數 `nsfw=None`
2. 回傳資料的 `nsfwLevel` 欄位檢查
3. `pipeline/nsfw_filter.py` 的關鍵詞清單（`is_nsfw_text`）

**在目前這批實際取得的語料上，前兩層幾乎沒有濾掉任何東西**：即使 prompt 內文含有明顯
的性暗示字眼，Civitai 回傳的 `nsfwLevel` 仍是 `"None"`。換句話說，**真正在做事的只有
第三層的固定英文關鍵詞清單**，而一份固定的英文關鍵詞清單天生就抓不到：

- 同義改寫（paraphrase）
- 其他語言（例如中文、日文的對應詞彙）
- 清單之外的新造詞、俚語

規劃中真正可靠的防線是在 API 層做「執行期安全控管」——對使用者輸入與最終合成的 prompt
各跑一次以 LLM 為基礎的檢查（見子專案後續計畫），而不是靠這份清單。**這個資料管線的
`nsfw_filter.py` 只是語料庫的衛生層（corpus hygiene），不是安全保證**，不應被誤解為
已經解決 NSFW 過濾問題。

## `_strip_boilerplate`：為什麼是程式碼過濾而不是純 prompt 指令

`pipeline/structure.py` 的 `_strip_boilerplate()` 會用決定性規則（非 LLM）從每個
snippet 裡移除：

- `score_*`（如 `score_9`、`score_8_up`）這類評分標籤
- `masterpiece` / `best quality` / `highly detailed` 等品質樣板詞
- `*_neg` 結尾的 negative embedding 名稱

這刻意做成程式碼層的決定性過濾，而不是完全交給 `PROMPT_TEMPLATE` 裡的文字指令去要求
LLM「不要把這些當成片段」。原因是：純靠 prompt 指令這件事**在實測中會有可量測的漏放
（leak）**——LLM 仍然偶爾會把 `score_9`、`masterpiece` 之類的樣板詞當成合法 preset
片段吐回來。程式碼層的決定性過濾器保證這類詞不論 LLM 是否遵守指令都會被擋掉。

## 續跑與規模

五個階段（fetch / clean / structure / embed / load）**全部可續跑**：`fetch` 靠
`state.json` 記 cursor，`structure`／`embed` 靠已存在的 `source_ref` 跳過重複，`load`
用 `ON CONFLICT (source_ref)` upsert。因此重新執行

    python seed_data.py --max-items <更大的數字>

會從上次停下的地方繼續，不會重跑已完成的部分，也不會產生重複資料。

隨附的正式語料是用 `python seed_data.py --max-items 400` 建立的（詳見
`.superpowers/sdd/2026-09-21-subproject-1-data-foundation/task-11-report.md`）。這個
數字刻意選得比 spec 草稿裡的 3000 小很多：在觀察到的約 55% 存活率下，3000 筆原始資料
代表上千次即時的 Gemini 結構化呼叫，會花費數小時並有實際用盡免費額度的風險。若要擴大
語料庫，直接調大 `--max-items` 重跑同一指令即可，管線會從斷點續跑而不是從頭開始。
