# 實驗：推薦組法動工前的現況量測與抽樣模擬

日期：2026-09-30
目的：推薦組法案（`docs/superpowers/specs/2026-09-30-recommendation-slate-design.md`）動工前，先量「推薦太像」到底是哪一種，並用模擬選抽樣參數。
資料：開發庫 `audit_logs` 的 `Turn_Completed`，有推薦的輪 85 輪、32 個 session、採用 24 次（含驗收與 eval 跑的 session）。

## 結論

- **同一排內撞在一起不常見**：同排兩套只比該維度 tag 的 Jaccard 平均 0.13，≥ 0.5 的只有 52 / 1,129 對（4.6%），tag 完全相同 20 對。偏像的主要是有錨排，那是設計上共用錨。
- **跨輪重複很嚴重**：同一 session 同一維度被再推一次時，**86%** 至少一套重複、**55%** 整排三套一模一樣。原因是描述沒大變，查詢向量不變，前 3 名就不變。
- 所以這一案的重心是「看過的往後延」＋探索位；同排 MMR 不做。
- **抽樣參數**：候選 30 筆、每批 2 套相關位時，「兩位都抽」第一批只有 36%（τ=5）含第 1 名；「第 1 位取有效名次最高、第 2 位抽」保證第一批含第 1 名，連按 5 批的 10 個位子裡仍有 9.3 個不同。定案 τ=5、P=10（看過一次權重剩 e^(−2) ≈ 13.5%）。

## 1. 同排相似度

每個推薦列（一個維度一排）裡兩兩配對，只比該維度的 facet（`facet_id` 前綴等於維度）：

- **tag Jaccard**：兩套在該維度的 tag 集合（小寫、底線換空白）交集／聯集。
- **facet cos**：兩套共有的該維度 facet，`preset_facet_embeddings` 向量的 cos 平均。
- **整套 cos**：`preset_embedding` 的 cos。

| 維度 | 列型 | 對數 | Jaccard 平均 | Jaccard ≥ 0.5 | tag 全同 | facet cos | 整套 cos |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| appearance | 有錨 | 127 | 0.21 | 4 | 0 | 0.906 | 0.847 |
| appearance | 無錨 | 33 | 0.15 | 2 | 0 | 0.874 | 0.817 |
| camera | 有錨 | 78 | 0.28 | 5 | 1 | 0.891 | 0.856 |
| camera | 無錨 | 153 | 0.13 | 15 | 11 | 0.868 | 0.815 |
| clothing | 有錨 | 135 | 0.07 | 3 | 2 | 0.828 | 0.838 |
| clothing | 無錨 | 9 | 0.01 | 0 | 0 | 0.767 | 0.854 |
| pose | 有錨 | 90 | 0.17 | 8 | 2 | 0.868 | 0.804 |
| pose | 無錨 | 72 | 0.05 | 0 | 0 | 0.775 | 0.825 |
| scene | 有錨 | 123 | 0.24 | 12 | 4 | 0.876 | 0.838 |
| scene | 無錨 | 81 | 0.05 | 0 | 0 | 0.784 | 0.809 |
| style | 有錨 | 99 | 0.12 | 2 | 0 | 0.861 | 0.871 |
| style | 無錨 | 129 | 0.01 | 1 | 0 | 0.772 | 0.835 |
| **全部** | | **1,129** | **0.13** | **52** | **20** | **0.846** | **0.833** |

（此時資料裡還沒有近似錨列。）

**基準**：從子表抽樣（`TABLESAMPLE SYSTEM (20) REPEATABLE (7)`），同一 facet 的隨機兩筆 facet cos 平均：appearance 0.784、camera 0.801、clothing 0.748、pose 0.780、scene 0.732、style 0.739。無錨列跟隨機差不多，有錨列高約 0.08–0.12。

## 2. 跨輪重複

同一 session、同一維度，跟上一次出現的那排比：

| 有前一次可比的列 | 至少一套重複 | 三套完全相同 |
| ---: | ---: | ---: |
| 262 | 225（86%） | 144（55%） |

查詢（`presetIds` 依 session＋維度用 `lag` 取前一次，陣列 `&&` 算有交集、互相 `<@` 算完全相同）：

```sql
WITH r AS (
  SELECT a.session_id sid, a.turn_index ti, d->>'dimension' dim, jsonb_array_elements_text(d->'presetIds')::bigint pid
  FROM audit_logs a, jsonb_array_elements(a.payload->'recommendations'->'dimensions') d
  WHERE a.event_type='Turn_Completed' AND jsonb_typeof(a.payload->'recommendations')='object'
), rows AS (SELECT sid, ti, dim, array_agg(pid) ids FROM r GROUP BY 1,2,3),
seq AS (SELECT *, lag(ids) OVER (PARTITION BY sid, dim ORDER BY ti) prev FROM rows)
SELECT count(*) FILTER (WHERE prev IS NOT NULL),
       count(*) FILTER (WHERE prev IS NOT NULL AND ids && prev),
       count(*) FILTER (WHERE prev IS NOT NULL AND ids <@ prev AND prev <@ ids)
FROM seq;
```

注意：這些是追問卡與定稿卡混在一起算的。定案後追問卡不動，只有定稿卡套用新機制。

## 3. 抽樣模擬

模型：候選名次 0–29；權重 `exp(−(名次 + P×看過次數)/τ)`；每批 2 套相關位，不放回抽（Plackett–Luce）；一批出來後兩套各記看過一次；連按 5 批。每組 4,000 次，種子固定。

| 抽法 | τ | P | 第一批含第 1 名 | 5 批 10 位中不同的 | 抽到最深名次（平均） |
| :--- | ---: | ---: | ---: | ---: | ---: |
| 兩位都抽 | 3 | 5 | 51% | 7.9 | 11.0 |
| 兩位都抽 | 3 | 10 | 51% | 9.2 | 12.3 |
| 兩位都抽 | 5 | 5 | 36% | 8.3 | 15.5 |
| 兩位都抽 | 5 | 10 | 36% | 9.1 | 16.3 |
| 兩位都抽 | 8 | 5 | 25% | 8.6 | 20.0 |
| 兩位都抽 | 8 | 10 | 25% | 9.1 | 20.4 |
| 第 1 位取最高、第 2 位抽 | 5 | 5 | 100% | 8.0 | 13.2 |
| **第 1 位取最高、第 2 位抽** | **5** | **10** | **100%** | **9.3** | **14.4** |
| 第 1 位取最高、第 2 位抽 | 8 | 5 | 100% | 8.4 | 17.0 |
| 第 1 位取最高、第 2 位抽 | 8 | 10 | 100% | 9.3 | 17.8 |

讀法：P 管「看過的退多少」，τ 管「往深處抽多少」。使用者不要求抽很深，要的是看過的退得夠多，所以選 τ=5、P=10，不選 τ=8。

模擬沒有模型化「tag 集合相同的算同一套」（設計 §3 的合併），實際不重複的比例會再高一點。

## 4. 查詢執行計畫與耗時（2026-09-30 實測）

環境：開發庫 `prompt-copilot-db`（docker，pgvector/pgvector:pg16），19,354 筆 `prompt_knowledge_presets`；API 伺服器跑在 `localhost:5000`（`feat/recommendation-slate` 分支，前面 Task 已完成、瀏覽器驗收過）。查詢向量與字面錨取自 preset id 8886（`clothing.footwear = sandals`）；`@facets` 用 portrait 的 clothing 六個 facet（`clothing.head`／`clothing.upper`／`clothing.lower`／`clothing.footwear`／`clothing.material`／`clothing.accessories`）；`@anchorFacets = {clothing.footwear}`、`@anchorTags = {sandals}`；`RecommendSimilarSql` 的 `@maxDist = 0.30`。索引確認：`idx_presets_embedding`（HNSW，`vector_cosine_ops`）、`idx_presets_facet_ids`（GIN）、`preset_facet_embeddings_pkey`（`(preset_id, facet_id)`）、`idx_pfe_facet`（btree）；`hnsw.ef_search` 為預設值 40。

每條查詢 `EXPLAIN (ANALYZE, BUFFERS)` 跑 5 次。

### `RecommendSql`（純向量，SetFilter + ORDER BY dist LIMIT n）

- LIMIT 3，5 次 `Execution Time`：2.482, 1.840, 1.777, 1.819, 1.964 ms → 中位數 **1.840 ms**
- LIMIT 30，5 次：3.933, 2.467, 2.323, 2.377, 2.114 ms → 中位數 **2.377 ms**（第一次 3.933ms 略高，判斷是磁碟頁快取尚未熱身，非容器重啟後第一次呼叫，不算異常，中位數計算未排除）
- 頂層節點（兩種 LIMIT 相同）：`Limit` → `Index Scan using idx_presets_embedding`（HNSW；子查詢 `@q` 走 `prompt_knowledge_presets_pkey`）

HNSW 漏筆檢查（spec §9.2 移過來的檢查，用真實 SELECT 不加 EXPLAIN）：LIMIT 30 的前 5 筆 id 依序為 `8886, 10507, 14229, 41647, 20789`；LIMIT 3 全部 3 筆 id 為 `8886, 10507, 14229`。取兩者的前 3 筆比對，逐筆相同，本次量測沒有出現 HNSW 漏筆。

### `AnchoredRecommendSql`（字面錨，MATERIALIZED CTE）

- LIMIT 3，5 次：15.712, 11.858, 11.578, 11.425, 12.198 ms → 中位數 **11.858 ms**
- LIMIT 30，5 次：11.449, 11.991, 11.224, 11.765, 10.914 ms → 中位數 **11.449 ms**
- 頂層節點：`Limit` → `Sort`（LIMIT 3 為 top-N heapsort，LIMIT 30 改 quicksort）← `CTE c` = `Bitmap Heap Scan on prompt_knowledge_presets`（`cost=90.83..33211.05 rows=891`）← `Bitmap Index Scan on idx_presets_facet_ids`（GIN，不走 HNSW）。CTE 內先用 GIN 篩 `facet_ids` 交集，逐列算 `jsonb_each`／`jsonb_array_length`／anchor 的 `EXISTS` 子查詢，兩種 LIMIT 下 CTE 實際都只有 **17 列**（涼鞋錨的候選量級）
- LIMIT 3 與 LIMIT 30 中位數幾乎相同（11.858 對 11.449ms），印證程式註解「MATERIALIZED 擋住規劃器把 ORDER BY／LIMIT 推進 HNSW，物化的 CTE 本來就把符合的列全算完」

### `RecommendSimilarSql`（近似錨，maxDist=0.30）

- LIMIT 3，5 次：5.636, 4.337, 4.495, 4.406, 4.960 ms → 中位數 **4.495 ms**
- LIMIT 30，5 次：4.424, 4.276, 4.552, 4.480, 5.061 ms → 中位數 **4.480 ms**
- 頂層節點：`Limit` → `Sort`（top-N heapsort，LIMIT 3 記憶體 28kB、LIMIT 30 為 59kB）← `Nested Loop`（`cost=6.31..1133.14 rows=7`，`actual rows=329`）：`Bitmap Index Scan on idx_pfe_facet`（`actual rows=338`）join `Index Scan using prompt_knowledge_presets_pkey`（走主鍵，`loops=338`）。`maxDist <= 0.30` 篩完剩 329 列，兩種 LIMIT 下 Nested Loop 結果一致

### `FacetVectorsSql`

`@ids` = `RecommendSql` LIMIT 30 的 30 個 id 再加 2 個（5, 6），共 32 個。

- 單一 facet（`@facets = {clothing.footwear}`），5 次：0.355, 0.186, 0.221, 0.182, 0.178 ms → 中位數 **0.186 ms**；`Index Scan using preset_facet_embeddings_pkey`（純走複合主鍵）
- 6 個 clothing facet，5 次：0.658, 0.509, 0.491, 0.582, 0.484 ms → 中位數 **0.509 ms**；`Bitmap Heap Scan` ← `BitmapAnd`(`idx_pfe_facet` 交集 `preset_facet_embeddings_pkey`)，規劃器改用 `BitmapAnd`、仍用得到主鍵，只是混用 facet_id 次索引，82 列（32 個 preset 不是每筆都補了全部 6 個 clothing facet）

兩種情況都在 1ms 以內，對整批延遲影響可忽略。

### 換一批延遲（`Recommendations_Next` audit）

Session `a62c51fd8bb74a63b39dff3a7d2189bd`（瀏覽器驗收用、`turnIndex=4`、`status: Finalized`）對 6 個維度各補呼叫 2 次（共 12 次，全部回 200，無 404／409）：

| dimension | batch | latency_ms |
| --- | ---: | ---: |
| style | 2 | 585 |
| style | 3 | 389 |
| scene | 2 | 538 |
| scene | 3 | 625 |
| camera | 2 | 543 |
| camera | 3 | 484 |
| appearance | 2 | 513 |
| appearance | 3 | 535 |
| pose | 2 | 461 |
| pose | 3 | 464 |
| clothing | 2 | 546 |
| clothing | 3 | 544 |

加上瀏覽器驗收留下的舊樣本（`turn_index=2`、`batch=2`、`latency_ms=2432`），共 13 筆：

```sql
SELECT count(*), percentile_cont(0.5) WITHIN GROUP (ORDER BY latency_ms), max(latency_ms)
FROM audit_logs WHERE event_type = 'Recommendations_Next';
```

→ `count=13, median=538, max=2432`。排除那筆舊樣本後（`turn_index=4` 的 12 筆）：`count=12, median=536.5, max=625, min=389`。2432ms 那筆比其餘 12 筆高出 4 倍以上，判斷是容器重啟後第一次呼叫帶到的 embedding API 暖機時間，不計入常態範圍；這次補測的 12 筆全在伺服器已經跑了一陣子之後呼叫，沒有再遇到第二筆異常高的樣本。

定稿卡整張（6 個維度）推薦延遲：沒有獨立的整批 audit 紀錄，用「換一批單排延遲中位數 × 6」估：538ms × 6 ≈ 3.2 秒（**估計值**，偏高——換一批每次呼叫各自帶一次 embedding API 呼叫，定稿卡實際是 6 個維度共用同一次 embedding 呼叫再依序跑 6 次 SQL，真正的整張延遲應低於這個估計）。

結論已寫入 `docs/superpowers/specs/2026-09-30-recommendation-slate-design.md` §8（表格與結論摘要）；本節保留原始 5 次數字與 13 筆延遲樣本供之後重新量測時對照。

## 5. Kisegae 在穿著排的占比（2026-09-30 驗收回饋）

使用者回報：推薦的人物穿著幾乎不見 Kisegae 來源。

**現況**：Kisegae 377 筆、其中 364 筆符合穿著「組合」條件（Civitai 1,295 筆），沒有被過濾掉。定稿卡穿著排的 Kisegae 占比：改前 71/174（41%），改後 2/27（7%）；換一批的批次也很少。改前的有錨排，Kisegae 在第 1 名的有 28/57（49%）。

**同輸入重播**：從 audit 重建 57 個改前定稿卡穿著排的查詢句（使用者原話最後 500 字）、錨（`Tool_Invoked` 的 `SetFacetStates`／`FinalizePrompt` 重放出 FacetTags＋定稿 positive）與三層候選，舊組法與新組法用同一份輸入比。能重建出錨與字面錨命中的有 18 排（舊 audit 的工具參數有截斷，其餘重建不出來；前 3 名與當時實際推薦完全相同的 4 排）。每排 Kisegae 的期望套數：

| 組法 | 第 1 位 | 第 2 位 | 探索位 | 合計 | 至少一套 Kisegae 的排 |
| :--- | ---: | ---: | ---: | ---: | ---: |
| 舊（前 3 名） | | | | 1.94 | 72% |
| 新（未混搭） | 0.72 | 0.56 | 0.25 | 1.53 | 81% |
| 新＋第 2 位來源混搭 | 0.72 | 0.28 | 0.27 | 1.27 | 100% |

**讀法**：第 1 位跟舊組法相同。Kisegae 的描述只講穿著，在錨過濾後依整段描述排序的名單裡集中在前段（有一排前 5 名全是 Kisegae）；舊組法固定給前 3 名，新組法第 2 位往深處抽、探索位從不過濾錨的純向量名單取（Civitai 的描述涵蓋人物＋場景＋穿著，跟整段描述比較近），所以 Kisegae 少了約 20%。近期回報的那個 session 題材是男性、金色長袍，Kisegae（幾乎都是女性服裝）本來就少，所以看起來掉得更多。

**決定**：第 2 位來源混搭（設計 §2）。目的是每排兩種來源都看得到，而不是把 Kisegae 總數拉回來；混搭後總數反而下降，因為第 1 位已是 Kisegae 的排，第 2 位改給 Civitai。重播樣本偏 Kisegae（第 1 位 72% 是 Kisegae），題材偏男性或場景時效果相反：第 1 位多半是 Civitai，第 2 位會補上 Kisegae。

