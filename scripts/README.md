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

全量分層抓取（fetch 階段免費，約 1 分鐘量級；structure 階段是唯一的付費瓶頸，5,000+
次 Gemini 結構化呼叫，會花時間也會花額度）：

    python seed_data.py

fetch 階段抓取邏輯見 `pipeline/strata.py` 的 9 層配額表（`baseModels x period`），
用來解決「同一組固定查詢參數永遠抓到同一批熱門人物向內容」的題材偏斜問題，設計細節見
[docs/superpowers/specs/2026-09-22-corpus-expansion-design.md](../docs/superpowers/specs/2026-09-22-corpus-expansion-design.md)。
先用已抓好的 raw 小量跑過結構化，跑 `coverage_report.py` 驗證候選池是否符合預期，
確認沒問題再跑全量結構化（`--quota-scale` 只影響 fetch 階段抓幾筆 raw，候選池要
structure＋embed＋load 都跑過才量得出來，不能拿 `--quota-scale` 驗證候選池）：

    python seed_data.py --from structure --max-records 500   # 先用已抓好的 raw 小量跑過結構化
    python coverage_report.py                                # 檢查候選池與 facet 是否達門檻
    python seed_data.py --from structure                     # 確認沒問題後跑全量結構化

只想快速試跑 fetch 階段（免費）、不想連帶跑到付費的 structure 階段，要單獨呼叫
fetch 這支模組，**不要**跑不帶 `--from` 的 `seed_data.py`——那會依序跑完 fetch／
clean／structure／embed／load 全部五個階段，`--max-records` 預設不限筆數，等於把
這次抓到的所有 clean 紀錄全部送進付費的 Gemini 結構化（約 2,800 次呼叫）：

    python -m pipeline.fetch_civitai --quota-scale 0.5   # 只抓每層一半配額，用於快速試跑 fetch

從某階段起跑：

    python seed_data.py --from structure --max-records 200

單一階段：

    python -m pipeline.fetch_civitai --quota-scale 0.1   # 只抓每層 10% 配額，快速驗證分層設定
    python -m pipeline.clean
    python -m pipeline.structure --max-records 100
    python -m pipeline.embed [--reindex]
    python -m pipeline.load

## 一次性回填：`facet_tags`（整套組合推薦用）

把每筆片段的 tag 歸到它 `facet_ids` 裡的哪個 facet，寫進 `prompt_knowledge_presets.facet_tags`。
整套組合推薦的對照表與「只採用這幾個 facet」靠它。只處理 `facet_tags IS NULL` 的列，可中斷、可重跑；
19k 筆約 1,000 次 Gemini 呼叫。既有資料庫先套 `db/migrations/002_facet_tags.sql`。

    python backfill_facet_tags.py --dry-run --limit 20   # 先看 20 筆拆得對不對
    python backfill_facet_tags.py                        # 全量

## 一次性補充語料：Kisegaeningyou 服裝集

補 clothing 維度的候選池缺口用的一次性匯入，**不是** `seed_data.py` 的階段之一
（fetch／clean 對它不適用）。它只產 presets、不寫 histories：來源是同一角色的換裝集，
每個檔就是一套服裝組合，不是完整的 SD prompt（沒有 style／scene／camera），
塞進 RAG 1 會汙染「找相似完整提示詞」的檢索。

來源、授權與署名要求見 [docs/資料來源.md](../docs/資料來源.md)。**圖片不轉存進本 repo。**

    # 先小量試跑，看產出的繁中 description 品質（檢索命中率取決於它）
    python -m pipeline.kisegae --source-dir "<Kisegaeningyou 本機路徑>" --max-records 20

    # 確認沒問題再跑全量（378 筆，約 378 次 Gemini 結構化呼叫）
    python -m pipeline.kisegae --source-dir "<Kisegaeningyou 本機路徑>"

    # 它 append 到 structured/presets.jsonl，後續接既有階段即可
    python seed_data.py --from embed
    python coverage_report.py

`--max-records` 算的是**成功寫出**的筆數，不是嘗試筆數（與 `pipeline.structure`
的同名參數語意不同）。重跑安全：以 `source_ref` 續跑，且片段去重是跨來源的
（civitai 已收錄同一組 tag 就不重複收）。

## 匯出公開的知識庫種子

`docker compose up` 首次啟動灌的 dump 從這裡來。它開一個用完即丟的 pgvector 容器、套上
`db/init/001_schema.sql`，把開發庫的兩張知識表以 `pg_dump --data-only` 串流灌進去，
在丟棄庫裡刪掉使用者自己存的紀錄（`source = 'user'`），再從丟棄庫匯出，最後停掉容器。
開發庫只被讀，API 開著也沒關係；灌進全新 schema 這一步順便證明 dump 灌得回去。約 2 分鐘。

    python export_seed.py --version 1        # 產出 data/seed/prompt_copilot_seed_v1.dump（不進版控）

它會印出 `gh release create seed-v1 …` 指令，上傳是手動的。語料擴增或換 embedding 模型後
版本號 +1，並同步 `docker-compose.yml` 的 `SEED_URL` 預設值與 `db/init/001_schema.sql` 的維度。
授權與免責聲明見 [docs/資料來源.md](../docs/資料來源.md)。

## Demo：中文描述 → 英文提示詞

`demo.py` 是子專案 1 成果的展示程式，也是子專案 2 互動流程的縮小版——它只跑一輪、
不追問。流程是三段：Gemini 先分析（題材、六維度 facet 狀態、每個維度的檢索子查詢）→
每個維度在自己的 facet 候選池裡做向量檢索 → Gemini 組裝提示詞。使用者沒描述的維度撈到的
片段只會進「建議」，不會寫進提示詞。

    python demo.py "昏暗雨夜的科幻城市，一個穿皮夾克的短髮女生站在霓虹招牌下"
    python demo.py "清晨山頂的日出，雲海翻騰" --verbose      # 印出每維度全部候選與相似度分級
    python demo.py "山上的日出" --k-covered 8 --k-missing 2  # 每維度撈幾筆
    python demo.py                      # 不給描述則進入互動模式，Ctrl+C 離開

`[2/3] 檢索` 印出後，緊接著的下一行會列每個維度的候選池大小與高／中／低命中數；池很小
或全是「低」就是知識庫在那個維度的覆蓋缺口。

每個步驟實際做了什麼、配上一次真實執行的逐段輸出，見
[docs/單輪流程說明.md](../docs/單輪流程說明.md)。

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

想看候選池大小、facet 分布是否達門檻，優先用 `python coverage_report.py`——它直接
算的就是 `retrieval.py` 實際查詢用的池子，不用手動拼 SQL。以下這些是唯讀查詢，留給
不想跑腳本、只想直接貼指令看資料的人。

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
| fetch | Civitai API | `data/raw/images.jsonl` | `state.json` 的各層 cursor |
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
`state.json`（每層各自的 cursor，見 `pipeline/strata.py`）續跑，`structure`／`embed`
靠已存在的 `source_ref` 跳過重複，`load` 用 `ON CONFLICT (source_ref)` upsert。因此
重新執行 `python seed_data.py` 會從每個階段上次停下的地方繼續，不會重跑已完成的部分，
也不會產生重複資料。

擴增前的正式語料是用舊版單層 CLI 抓 400 筆建立的（420 raw / 258 clean）。分層抓取的配額表與規模換算見
[docs/superpowers/specs/2026-09-22-corpus-expansion-design.md](../docs/superpowers/specs/2026-09-22-corpus-expansion-design.md) §2、§6：
9 層合計 12,800 raw（初版 9,000，實跑 clean 只有 4,499、存活率 50%，已依實測回填；
見 spec §6 的「配額修訂紀錄」）。要調整規模就改
`pipeline/strata.py::STRATA` 裡各層的 `quota`（程式碼常數，改了要走 code review，
不是隱藏在設定檔裡的旋鈕）。

`embed --reindex` 會先清空輸出檔再重算全部向量（見上方「換 embedding 模型」）；如果
在 `--reindex` 執行到一半時中斷，輸出檔會停在部分寫入的狀態。復原方式是**不加**
`--reindex` 再跑一次 `python -m pipeline.embed`——它會把清空後只寫了一部分的檔案視為
「已完成的部分」，續跑補完剩下的紀錄。
