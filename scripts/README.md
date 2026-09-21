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

    python seed_data.py --max-items 400

`--max-items 400` 是隨附語料實際使用的數字（見下方「續跑與規模」）；數字愈大花的
Gemini 額度愈多，第一次執行不建議直接跳到更大的數字（例如 spec 草稿裡的 3000）。

從某階段起跑：

    python seed_data.py --from structure --max-records 200

單一階段：

    python -m pipeline.fetch_civitai --max-items 500
    python -m pipeline.clean
    python -m pipeline.structure --max-records 100
    python -m pipeline.embed [--reindex]
    python -m pipeline.load

## Demo：中文描述 → 英文提示詞

`demo.py` 是子專案 1 成果的展示程式，也是子專案 2 互動流程的縮小版——它只跑一輪、
不追問。一次一個 Gemini 呼叫。

    python demo.py "昏暗雨夜的科幻城市，一個穿皮夾克的短髮女生站在霓虹招牌下"
    python demo.py "清晨山頂的日出，雲海翻騰" --top-presets 12
    python demo.py                      # 不給描述則進入互動模式，Ctrl+C 離開

流程是：把中文描述向量化 → 檢索 presets 與相似作品 → 交給 Gemini 判定題材、逐一評估
37 個 facet、組裝正負向提示詞。輸出包含六維度燈號（`●` 已涵蓋、`○` 缺少、`──` 該題材
不適用）、提示詞、實際採用了哪些知識庫片段，以及一句「最值得補上什麼」的建議。

值得注意的行為：**標為缺少的項目不會被自行發明**，會留白交給生圖模型決定（spec §5.4）。
基礎畫質詞與負向詞則一定會加，因為那是慣例而非創作選擇（spec §5.5）。

## 驗收：測試檢索效果

`query_check.py` 會把你的中文口語敘述向量化，然後同時查兩個庫：
`prompt_knowledge_presets`（可重用片段，支援 facet／tag 過濾）與
`shared_prompt_histories`（完整的參考 prompt）。距離是 cosine，**愈小愈相關**。

    python query_check.py "昏暗雨夜的科幻城市"
    python query_check.py "穿皮夾克的女生" --facet clothing.upper
    python query_check.py "霓虹燈光" --tag neon --top 10

可用的 `--facet` 值就是 `src/PromptCopilot.Api/Configuration/facets.yaml` 裡的 37 個 id
（`scene.lighting`、`clothing.upper`、`camera.shot`…）。`--tag` 則是管線產生的英文標籤。

判讀方式：同一批結果裡**距離的落差**比絕對值重要。最相關的通常落在 0.20–0.25，
0.30 以上大多只是勉強沾邊。如果整批距離擠在很窄的範圍內，代表那個查詢的語意
沒有被語料涵蓋，不是檢索壞掉。

## 查看資料庫內容

以下都是唯讀查詢，可以直接貼。

總覽：

    docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -c "SELECT (SELECT count(*) FROM shared_prompt_histories) AS histories, (SELECT count(*) FROM prompt_knowledge_presets) AS presets"

題材分布與 preset 分類分布：

    docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -c "SELECT subject_profile, count(*) FROM shared_prompt_histories GROUP BY 1 ORDER BY 2 DESC"
    docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -c "SELECT category, count(*) FROM prompt_knowledge_presets GROUP BY 1 ORDER BY 2 DESC"

最常出現的 facet（看語料偏向哪些維度）：

    docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -c "SELECT f AS facet_id, count(*) FROM prompt_knowledge_presets, unnest(facet_ids) AS f GROUP BY 1 ORDER BY 2 DESC LIMIT 10"

隨機抽幾筆實際內容出來看：

    docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -c "SELECT title, category, facet_ids, left(prompt_snippet,60) FROM prompt_knowledge_presets ORDER BY random() LIMIT 5"
    docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -c "SELECT subject_profile, user_intent FROM shared_prompt_histories ORDER BY random() LIMIT 5"

開一個互動式 psql（`\dt` 看表、`\d+ 表名` 看欄位、`\q` 離開）：

    docker exec -it prompt-copilot-db psql -U postgres -d prompt_copilot

## 本機注意事項（Windows）

這兩點在別台機器上不一定適用，但在目前這台開發機上會踩到：

- **`docker` 不在預設 PATH。** Docker Desktop 裝在使用者目錄下，所以每個新開的
  PowerShell 視窗要先跑一次：

      $env:PATH = "C:\Users\USER\AppData\Local\Programs\DockerDesktop\resources\bin;$env:PATH"

- **Python 只在 PowerShell 裡解析得到**（Git Bash 會撞到 Microsoft Store 的 stub）。
  最穩的做法是直接用 venv 的直譯器，不依賴 PATH：

      Set-Location <repo>\scripts
      & .\.venv\Scripts\python.exe -m pytest
      & .\.venv\Scripts\python.exe query_check.py "昏暗雨夜的科幻城市"

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

**在目前這批實際取得的語料上，前兩層完全沒有濾掉任何東西（實測 20 筆原始資料全部是
`nsfwLevel: "None"`、`nsfw: false`）**：即使 prompt 內文含有明顯的性暗示字眼，Civitai
回傳的 `nsfwLevel` 仍是 `"None"`。換句話說，**真正在做事的只有
第三層的固定英文關鍵詞清單**，而一份固定的英文關鍵詞清單天生就抓不到：

- 同義改寫（paraphrase）
- 其他語言（例如中文、日文的對應詞彙）
- 清單之外的新造詞、俚語

規劃中真正可靠的防線是在 API 層做「執行期安全控管」——對使用者輸入與最終合成的 prompt
各跑一次以 LLM 為基礎的檢查（見子專案後續計畫），而不是靠這份清單。**這個資料管線的
`nsfw_filter.py` 只是語料庫的衛生層（corpus hygiene），不是安全保證**，不應被誤解為
已經解決 NSFW 過濾問題。

## `pipeline/boilerplate.py::strip_boilerplate`：為什麼是程式碼過濾而不是純 prompt 指令

`pipeline/boilerplate.py` 的 `strip_boilerplate()`（由 `pipeline/structure.py` 呼叫）
會用決定性規則（非 LLM）從每個
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

隨附的正式語料是用 `python seed_data.py --max-items 400` 建立的。這個
數字刻意選得比 spec 草稿裡的 3000 小很多：clean 階段（去掉沒 meta、太短、非英文、
NSFW 的紀錄後）實測存活率約 55%，若直接跑 3000，代表上千次即時的 Gemini 結構化呼叫，
會花費數小時並有實際用盡免費額度的風險。若要擴大語料庫，直接調大 `--max-items` 重跑
同一指令即可，管線會從斷點續跑而不是從頭開始。

`embed --reindex` 會先清空輸出檔再重算全部向量（見上方「換 embedding 模型」）；如果
在 `--reindex` 執行到一半時中斷，輸出檔會停在部分寫入的狀態。復原方式是**不加**
`--reindex` 再跑一次 `python -m pipeline.embed`——它會把清空後只寫了一部分的檔案視為
「已完成的部分」，續跑補完剩下的紀錄。
