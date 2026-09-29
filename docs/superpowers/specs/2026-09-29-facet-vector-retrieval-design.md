# facet 層級向量檢索：設計

日期：2026-09-29
來源：`docs/superpowers/specs/2026-09-25-set-recommendations-design.md` §3.2 排定的後續案，2026-09-29 討論定案。
實驗紀錄：`docs/experiments/2026-09-29-facet-vector-text.md`（本文件的數字都出自那裡）。
前情：known-issues #3 已修（master `934a9cd`）。計畫 §4.2 的 A/B 仍往後排；推薦組法（MMR 去相似＋探索位）另開一案，排在本案之後。

---

## 1. 問題與目標

**問題一：`SearchPresets` 的 facet 項目「過濾準、排序不準」**（known-issues §5）。facet 項目先用 `facet_ids && [facet]` 過濾，再拿整套片段的向量排序，整套向量的文字是 `title。description。標籤：全部 tag`。穿著片段涵蓋頭到腳，向量被上下身主導，鞋履、頭部這類小 facet 排不準。R2 的「涼鞋」：前 5 名只有 2 筆是 sandals，知識庫有 18 筆。

**問題二：推薦的錨抓不到同義詞**（known-issues §10）。錨比對是字面相等或空白為界的字尾，`slippers` 對不上 `sandals`，命中不到 2 筆就退回無錨。

**目標**：

- facet 項目改用 facet 自己的向量排序。同一組 17 題實驗（實驗紀錄的查詢集），前 5 名命中數 67 → 約 76（滿分 85），前 5 名相異 tag 組合數 76 → 85（滿分）。
- 推薦多一種「近似錨」：字面抓不到時，找該 facet 向量跟錨夠近的組合，前端標「接近你講的 …」。
- 維度項目、`SearchSimilarPrompts`、推薦的有錨與無錨兩條路都不變。

## 2. 決定紀錄

| 題目 | 決定 | 理由 |
| :--- | :--- | :--- |
| 範圍 | `SearchPresets` facet 項目＋推薦的近似錨，一起做 | 資料表、產生腳本、seed 兩件共用；推薦只多一條查詢 |
| facet 向量的文字 | **該 facet 的英文 tag**（正規化、照原順序、`, ` 串接） | 實驗 V1–V4 都用它；加中文 facet 名稱（V2）同一個 facet 前綴都一樣，拉不開差距，「俯視」還被帶到 `from below`，命中 0 |
| facet 項目的查詢句 | **`中文原話（英文 tag）`**；沒有英文時只用中文（實驗 V4） | 只用中文（V1）73，帶英文 81；只用英文（V3）同分，但翻譯不穩定（temperature 0 三次結果不同），帶著中文原話較穩 |
| 去重 | **相同 `tag_key` 只留最近一筆**（實驗 V4d） | 不去重時前 5 名常是同一組 tag 重複 5 次（多樣性 40）；RAG 的價值在具體、少見的詞，5 筆 `sandals` 給不出 `sandals with straps` |
| 英文 tag 從哪來 | **`SearchPresets` 每個項目加選填 `tags`**，模型查詢當下給 | 用 `session.FacetTags` 會帶進 known-issues §10 的舊錨問題（改成靴子卻沒重給 tags，查詢變成「靴子（sandals）」）；漏給時退回只用中文，頂多回到現行水準，看檢索細節就知道 |
| 儲存與查詢 | **資料庫子表，離線算好，`facet_id` B-tree＋精確排序，不建 HNSW** | 見 §7 |
| 向量型別 | `vector(768)`，跟既有表一致 | `halfvec` 可讓 seed 少約 55MB，嫌 seed 太大再換，只影響這張表 |
| 產生時呼叫 Gemini | **執行緒池併發**，共用既有的 `RateLimiter` | 單執行緒約 20–30 分鐘；限速器本來就執行緒安全、429 已有退避 |
| 推薦的近似錨 | **第三種狀態，有距離門檻**；門檻內不到 2 筆就退回無錨 | 不設門檻會把 `cowboy boots` 包裝成「接近涼鞋」；併進「有錨」則字面是錯的（組合裡沒有 slippers） |
| 推薦的多樣性／探索 | **不在本案**，另開一案 | 見 §3.2 |

## 3. 範圍

### 3.1 做

1. `preset_facet_embeddings` 子表、`db/migrations/003`、`001_schema.sql` 同步。
2. `scripts/embed_facet_tags.py`（併發、可續跑）與測試；開發庫跑一次；seed-v3。
3. `SearchQuery.tags`、查詢句組法、`PresetRepository.SearchFacetAsync`（`tag_key` 去重）、facet 項目的分級門檻、舊資料庫退路、檢索細節的 `tags`／`method`。
4. `RecommendationService` 的近似錨、`RecommendedDimension.Similar`、前端文案、audit 的 `similar`。
5. 測試、驗收（離線重跑實驗、查詢速度、Playwright 線上）、文件。

### 3.2 不做

- **推薦組法**：同一排 2–3 套去相似（MMR）、每排留一套「換個搭法」探索位、每套各自標示推薦理由。使用者 2026-09-29 選定、另開一案，排在本案之後。本案只加近似錨這條退路，不改推薦怎麼組一整排。隨機抽樣先不做。
- 維度項目（沒講的維度的對比方向）改用 facet 向量：維度項目的池是整個維度，本來就該比整套片段。
- Python 參考實作（`scripts/pipeline/retrieval.py`、`scripts/demo.py`）跟著改：它是單輪 demo，差異記在文件裡。
- 定稿 positive 的 tag 參與近似錨：它們沒有標屬於哪個 facet，拿去比某個 facet 的向量會亂配；它們照樣參與既有的字面錨過濾。
- `backfill_facet_tags.py` 加併發。

## 4. 資料層

### 4.1 子表

`db/migrations/003_preset_facet_embeddings.sql`（冪等，`docker/seed.sh` 每次啟動都跑；開發庫手動 `psql -f` 一次），新建的資料庫在 `db/init/001_schema.sql` 放同一份：

```sql
CREATE TABLE IF NOT EXISTS preset_facet_embeddings (
    preset_id  BIGINT NOT NULL REFERENCES prompt_knowledge_presets(id) ON DELETE CASCADE,
    facet_id   TEXT   NOT NULL,
    tag_key    TEXT   NOT NULL,        -- 正規化後排序去重、', ' 串接：去重鍵，也用來判斷 tag 改過沒
    embedding  VECTOR(768) NOT NULL,
    PRIMARY KEY (preset_id, facet_id)
);
CREATE INDEX IF NOT EXISTS idx_pfe_facet ON preset_facet_embeddings (facet_id);
-- 刻意不建 HNSW：單一 facet 的池最多約 4,000 筆，B-tree 縮池後精確排序就夠快，
-- 也沒有「HNSW 先搜再過濾」漏筆的問題（known-issues #2）。理由見 facet 向量設計 §7。
```

`facet_tags` 裡每個非空的 facet 一列。2026-09-29 開發庫共 37,011 列，最大的 facet（`scene.location`）3,671 列。

### 4.2 文字與 `tag_key`

- **正規化**：跟 C# `TagAttribution.Normalize` 相同：小寫、底線換空白、剝 `:數字` 權重與外層括號、合併連續空白。Python 端實作一份，兩邊共用同一組測試案例（例：`(Sandals:1.2)` → `sandals`、`platform_sandals` → `platform sandals`）。空字串丟掉。
- **embedding 文字**：正規化後照原順序、去掉重複，`, ` 串接，例 `sandals, white socks`。實驗沒有剝權重，這裡比實驗乾淨。
- **`tag_key`**：正規化後排序、去重、`, ` 串接。`sunset, golden hour` 與 `golden hour, sunset` 同鍵，修掉實驗裡「順序不同算兩組」的問題。

### 4.3 `scripts/embed_facet_tags.py`

- 待辦：`facet_tags` 非 NULL 的片段，對每個非空 facet 算 `tag_key`。表裡沒有這一列、或 `tag_key` 不同（tag 改過）→ 要算；表裡有、`facet_tags` 已沒有這個 facet → 刪。
- 依 `RETRIEVAL_DOCUMENT` embed。`--workers N`（預設 4）用執行緒池同時送多批，每批 32 筆（`gemini_client.BATCH_SIZE`）；全部共用同一個 `GeminiClient`，也就共用它的 `RateLimiter`（`.env` 的最小間隔 0.1 秒 → 總速率上限每秒 10 次請求），429／5xx 照 `_call` 既有的退避重試。
- 只有主執行緒寫資料庫（psycopg 連線不能跨執行緒共用）：每完成一批就 upsert 並提交一個交易，中斷後重跑會從沒寫進去的繼續。
- `--limit`、`--dry-run` 與其他腳本一致；結束印統計：新增、更新、刪除、略過、耗時。
- 預估約 1,160 次請求；4 個執行緒應在 5–8 分鐘，實際看這把 key 的速率限制。

### 4.4 Seed

- `scripts/export_seed.py` 的 `TABLES` 加 `preset_facet_embeddings`，匯出 **seed-v3**。
- `docker/seed.sh` 匯入後多一條提醒：片段有資料但 `preset_facet_embeddings` 是空的，印「facet 檢索會退回整套向量；跑 `scripts/embed_facet_tags.py` 或改用 seed-v3 以上的種子」。
- 大小：seed-v2 是 105MB，37k 個 768 維向量幾乎壓不下去，v3 估計約 210MB。
- compose 預設 `SEED_URL` 改指向 v3，**要等 Release 上傳之後**另外一個 commit（§11）。

## 5. `SearchPresets`

### 5.1 介面

`SearchQuery` 加選填 `tags`（英文 SD tag，逗號分隔）。只有 facet 項目用；維度項目帶了也忽略。

- 工具與參數說明：facet 項目附上使用者對這個 facet 的描述翻成的英文 tag，寫法跟 `SetFacetStates` 的 `tags` 一樣。
- `system.md`／`SystemPromptBuilder` 第 1 條的範例改成 `clothing.footwear`＋「拖鞋」＋`slippers`、`appearance.hair`＋「銀色雙馬尾」＋`silver hair, twintails`。
- 伺服器**不**拿它更新 `session.FacetTags`。

### 5.2 查詢句

有 `tags`：`{query}（{tags}）`，例 `涼鞋（sandals）`；沒有：`{query}`。仍在同一次 `EmbedAsync`（`RETRIEVAL_QUERY`）整批送出。

### 5.3 檢索

facet 項目改走新的 `PresetRepository.SearchFacetAsync(float[] query, string facetId, int k, CancellationToken)`：

```sql
WITH d AS (
    SELECT DISTINCT ON (tag_key) preset_id, embedding <=> @q AS dist
    FROM preset_facet_embeddings
    WHERE facet_id = @facet
    ORDER BY tag_key, dist          -- 每種 tag 組合只留最近的一筆
)
SELECT p.id, p.title, p.facet_ids, p.prompt_snippet, p.negative_snippet, p.image_url, d.dist, p.source_ref
FROM d JOIN prompt_knowledge_presets p ON p.id = d.preset_id
ORDER BY d.dist
LIMIT @k
```

- 回傳仍是 `PresetHit`（整筆片段），只是排序與 `dist` 來自 facet 向量。ledger、「可借入／僅供建議」、facet 覆蓋標記、`HistoryTrimmer` 壓縮都不用改。
- **候選池筆數**：facet 項目改報 `preset_facet_embeddings` 裡該 facet 的列數（有這個 facet tag 的片段數；鞋履 396 → 338），這才是被搜的範圍。
- **舊資料庫退路**：該 facet 在子表是 0 筆 → 走原本的 `SearchAsync`（`facet_ids` 過濾＋整套向量）與原本的池計數。
- 維度項目不變。

### 5.4 分級門檻

facet 項目改用另一組常數：**高 < 0.22、中 < 0.27**（實驗 V4d 前 10 名：正解距離中位數 0.221、非正解 0.278）。維度項目與退路維持 0.25／0.30。模型看分級決定借不借詞（system prompt：相似度「低」的片段仍可借與描述相符的詞），所以門檻一定要跟距離的來源一致。

### 5.5 檢索細節

`SearchPresetsItem` 加：

- `tags`：這一項用了什麼英文（沒有就 null）。前端工具卡的查詢句顯示成「涼鞋（sandals）」。
- `method`：`facet` 或 `preset`（退路）。

回給模型的 JSON 不變。audit 的 `Tool_Invoked` 本來就記完整參數（#7），`tags` 會跟著進去，驗收靠它統計模型有沒有填。

## 6. 推薦的近似錨

### 6.1 流程

`RecommendationService.BuildAsync` 每個維度：

1. **字面錨**（不變）：錨＝covered facet 的 `FacetTags`＋定稿 positive；`RecommendAsync` 過濾後命中 ≥ 2 → `anchored`。
2. **近似錨**（新）：字面錨不到 2 筆時，對這個維度裡「covered 且有 `FacetTags`」的每個 facet，拿該 facet 的錨去比子表裡同一個 facet 的向量；距離在門檻內、且符合組合條件的列取前 `RecommendationTake` 筆。各 facet 的結果合併，同一片段取最小距離，依距離排序取前 `RecommendationTake`。≥ 2 筆 → `similar`。
3. **無錨**（不變）：上面都不到 2 筆。

子表是空的（舊資料庫）就跳過第 2 步，行為跟現在一樣。

### 6.2 錨的文字與向量

該 facet 的 `FacetTags` 正規化後 `, ` 串接，只有英文（英文比英文，對應實驗 V3：正解距離中位數 0.19、非正解 0.27）。這些向量與使用者原話的查詢向量**併進同一次** `EmbedAsync`（`RETRIEVAL_QUERY`）：一開始就把所有要推薦的維度裡、covered 且有 `FacetTags` 的 facet 的錨一起 embed，事後沒用到也無妨（每個錨是一小段文字）。這樣不管哪個維度走到近似錨，都不會多一次呼叫。

### 6.3 SQL

新增 `PresetRepository.RecommendSimilarAsync(float[] anchor, string facetId, IReadOnlyList<string> dimensionFacets, double maxDist, int take, CancellationToken)`：

```sql
SELECT p.id, p.title, p.facet_ids, p.facet_tags::text, p.image_url, p.source_ref, e.embedding <=> @a AS dist
FROM preset_facet_embeddings e
JOIN prompt_knowledge_presets p ON p.id = e.preset_id
WHERE e.facet_id = @facet
  AND e.embedding <=> @a <= @maxDist
  AND <組合條件>                        -- 同 set-recommendations 設計 §4.4：這個維度至少 2 個採得到 tag 的 facet
ORDER BY dist
LIMIT @take
```

**不去重**：推薦要的是整套組合，鞋履一樣是 `sandals`、整套穿搭不同，正是要給使用者比的。`PresetCandidate.Dist` 在這條路上是 facet 距離。

### 6.4 門檻

新設定 `OrchestratorOptions.RecommendationSimilarMaxDist`，預設 **0.23**（介於實驗 V3 的正解中位數 0.19 與非正解中位數 0.27 之間）。驗收時用 `slippers`、`flip-flops` 對涼鞋實測，不合理再調。

### 6.5 事件、前端、audit

- `RecommendedDimension` 加 `Similar`（bool）。`Anchored` 仍只代表字面命中；兩者不會同時為 true。`AnchorTags` 在近似時列出真的有命中的錨（該 facet 的 `FacetTags`，正規化後）。
- 前端 `types/api.ts` 加 `similar`；`RecommendationStrip.vue` 的文案：`anchored` →「含你講的 …」、`similar` →「接近你講的 …」、否則「最接近你描述的組合」。
- audit `Turn_Completed` 的 `recommendations.dimensions[]` 加 `similar`，之後看得出近似錨出現的頻率。
- 採用（`AdoptionComposer`）不變，照舊從 `facet_tags` 拿 tag。

## 7. 為什麼不建 HNSW

1. **池很小**：`facet_id = @facet` 用 B-tree 縮到單一 facet 後，最多約 3,700 列（`scene.location`）。768 維精確距離算 3,700 次，預期只要幾毫秒；驗收時對最大的池跑 `EXPLAIN ANALYZE` 記下來。
2. **HNSW 在「先過濾」的查詢上會漏筆**：pgvector 的 HNSW 是先依向量取近鄰再套 `WHERE`。known-issues #2 就是這個問題（候選池幾千筆時回 0 筆），靠 iterative scan 補救；iterative scan 又有 `hnsw.max_scan_tuples` 上限（set-recommendations 設計 §4.4 為此在有錨時改用 `MATERIALIZED` 整批撈）。子表若建 HNSW，`facet_id` 過濾一樣會碰到，而且 37k 列裡單一 facet 常只佔幾百列，比例更差。
3. **`DISTINCT ON (tag_key)` 本來就要看完整個池**：去重需要池裡每一列的距離，HNSW 的近似取前 N 幫不上忙。
4. **精確結果可重現**：驗收要跟實驗逐題對照，近似索引會讓數字每次略有不同。

之後單一 facet 的池若長到數萬筆、查詢明顯變慢，再考慮按 facet 分區或建部分索引。

## 8. 測試

**Python**（`scripts/tests/test_embed_facet_tags.py`）：

- 正規化（與 C# 共用的案例）、embedding 文字、`tag_key` 不受順序影響。
- 待辦判斷：缺列要算、`tag_key` 變了要算、facet 消失要刪、`facet_tags` 為 NULL 或 `{}` 的片段略過。
- `--dry-run` 不寫入。
- 假 client＋`--workers 3`：每一批都寫進去且只寫一次；某一批失敗時已完成的批次留著、重跑只補失敗的。

**C#**：

- 正規化對齊：同一組案例跑 `TagAttribution.Normalize`。
- `KnowledgePluginTests`：有 `tags` 的查詢句組法；維度項目帶 `tags` 被忽略；子表 0 筆走退路且 `method = preset`；facet 項目用新門檻分級；檢索細節帶 `tags`／`method`；池計數來源。
- `RecommendationServiceTests`：字面錨 ≥ 2 不走近似；字面不到 2 → 近似；門檻外不算；近似不到 2 → 無錨；子表空跳過；錨向量跟查詢向量同一次 embed；`Similar`／`AnchorTags` 正確。
- `AgenticOrchestratorTests`：audit 的 `recommendations.dimensions[].similar`。
- `SystemPromptBuilderTests`：第 1 條的新範例。
- 整合測試（`PC_INTEGRATION=1`）：`SearchFacetAsync` 依 `tag_key` 去重、`RecommendSimilarAsync` 遵守門檻與組合條件。

**前端**：`RecommendationStrip` 三種文案、reducer 處理 `similar`、工具卡顯示「涼鞋（sandals）」。

## 9. 驗收

1. **離線重跑實驗**：同一組 17 題，查詢句照 §5.2（英文 tag 用實驗紀錄結果表的「英文 tag」欄，跟實驗同一組），走正式的 `SearchFacetAsync`。預期前 5 名命中約 76、相異組合約 85。結果補進實驗紀錄。
2. **查詢速度**：`scene.location` 跑 `EXPLAIN ANALYZE`，記下耗時。
3. **線上**（Claude 用 Playwright 驅動 headless Edge，對新分支建的 compose，實際呼叫 Gemini），案例寫進 `docs/eval-cases.md` 新的一節：
   - R2 同一句「一個銀髮少女穿涼鞋站在雨夜街頭」：鞋履前 5 名是不同寫法的涼鞋；工具卡顯示「涼鞋（sandals）」與 `method: facet`。
   - `tags` 填寫率：多跑幾句不同的描述，從 audit 的 `SearchPresets` 參數統計 facet 項目帶 `tags` 的比例；太低就加強工具說明。
   - 近似錨：用字面抓不到的描述（拖鞋→`slippers`、夾腳拖→`flip-flops`），推薦出現「接近你講的 …」，檢查門檻 0.23。
   - 退路：對一個沒有子表資料的庫（或暫時清空子表）確認 `method: preset` 且照常回結果。

## 10. 文件

- `docs/known-issues.md`：§5「過濾準、排序不準」與 §10「同義詞抓不到」移到已修正，附 commit。
- 主規格 §9（C# 檢索現況）：facet 項目改用子表；`docs/superpowers/specs/2026-09-22-dimension-scoped-retrieval-design.md` §13 補一條與 Python 端的差異。`PresetRepository` 開頭「與 `retrieval.py` 的 `PRESETS_SQL` 一致」的註解改成只指 `SearchAsync`。
- `docs/SK架構說明.md`：若有描述 `SearchPresets` 參數的地方，補 `tags`。
- `docs/單輪流程說明.md`：只描述 Python 單輪 demo，預期不用改；實作時確認。
- 實驗紀錄：補驗收結果。

## 11. 交付順序

1. Opus 子代理在 worktree 實作（本文件 → 實作計畫 → subagent-driven），使用者說 merge 才 merge 進 master 並 push。
2. merge 後：開發庫套 `003` migration、跑 `embed_facet_tags.py`、跑驗收 1–3。
3. `export_seed.py --version 3`，使用者照印出的 `gh` 指令上傳 Release。
4. 上傳之後，另一個 commit 把 compose 預設 `SEED_URL` 改成 v3，並用全新 volume 驗證一次 seed。

## 12. 風險與待觀察

- **模型不填 `tags`**：退回只用中文（實驗 V1d 69，跟現行 67 差不多），不會更差；驗收統計填寫率。
- **翻譯錯**：`tags` 翻錯時中文原話還在查詢句裡；比只用英文穩，但無法完全抵銷。
- **分級門檻**：0.22／0.27 只來自 17 題。驗收後若「高」太少或太多再調。
- **近似錨門檻**：0.23 是從 facet 查詢的分布推的，錨的文字（多個 tag 串接）可能讓距離整體偏移；驗收實測決定。
- **seed 變大**：約 210MB；真的造成困擾就把子表換成 `halfvec`。
- **`facet_tags` 本身的錯誤**：回填歸錯 facet 的 tag 會被算進錯的 facet 向量；這是回填品質問題，不在本案處理。
