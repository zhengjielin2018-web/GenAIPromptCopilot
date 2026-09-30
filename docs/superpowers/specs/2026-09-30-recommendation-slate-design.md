# 推薦組法：看過延後、探索位與換一批：設計

日期：2026-09-30
來源：`docs/superpowers/specs/2026-09-25-set-recommendations-design.md` 的後續案（2026-09-29 facet 向量案 brainstorm 時排定），facet 向量案完成（master `a2287a3`）後開案。
實驗紀錄：`docs/experiments/2026-09-30-recommendation-slate.md`（本文件的數字都出自那裡）。

---

## 1. 問題與目標

**問題一：跨輪重複。** 推薦由伺服器依「使用者原話＋錨」查，描述沒大變時查詢向量不變，前 3 名就不變。同一 session 同一維度被再推一次時，86% 至少一套重複、55% 整排一模一樣。

**問題二：只給最相關的，取代不容易發生。** 取代率（看圖後放棄自己的設定，採用時換掉 covered facet）是 RAG 價值最有說服力的數字，但推薦全是「含你講的」，看到的都在使用者已講的範圍內。

**不是問題：同排撞在一起。** 同排兩套只比該維度 tag，Jaccard 平均 0.13，≥ 0.5 只有 4.6%。同排 MMR 不做。

**目標**：

- 定稿卡每排 = 2 套相關位＋1 套探索位（「換個搭法」），每套各自標理由。
- 看過的組合往後延（加權，不是踢掉），跨輪、跨批都不再整排重複。
- 定稿卡每排可以「換一批」，新的一批接在右邊。
- 量測分得出位子（相關／探索）、批次，並記下每套的名次與抽中機率。

## 2. 決定紀錄

| 題目 | 決定 | 理由 |
| :--- | :--- | :--- |
| 「太像」指什麼 | **跨輪重複**＋想要探索位；同排內的相似不處理 | 使用者實際感受；量測也支持（§1） |
| 同排 MMR | **不做** | 同排撞在一起只有 4.6%，偏像的多是有錨排，那是共用錨的本意 |
| 套用範圍 | **只有定稿卡**（含定稿後每次修改的定稿卡）；追問卡完全不動 | 定稿前使用者注意力在追問選項上，不在推薦；追問卡上出現過的也不算「看過」 |
| 看過的處理 | **加權延後，不踢掉** | 踢掉會把池子翻完（涼鞋錨只有 18 筆）；加權永遠有東西可推 |
| 排序方式 | **名次轉機率、隨機抽**；推翻 2026-09-29「隨機先不做」 | 固定「往後退 P 位」只會讓前幾名一直輪回來，實際只看得到前幾批 |
| 權重公式 | `exp(−(名次 + P×看過次數)/τ)`，不放回抽（Plackett–Luce） | 用名次不用距離：三層（字面錨／近似錨／純向量）距離尺度不同，接成一條名單後只有名次可比 |
| 相關位的抽法 | **第 1 位取有效名次最高，第 2 位抽** | 兩位都抽時第一批只有 36% 含第 1 名；混合抽法 100%，5 批內不同的仍有 9.3 / 10 |
| 第 2 位來源混搭（2026-09-30 驗收回饋） | **有效名次 ≤ 10 內有跟第 1 位不同來源（civitai／kisegae）的，第 2 位只從那些抽**；沒有才照原本從全部抽 | 使用者回報穿著排幾乎不見 Kisegae：定稿卡 Kisegae 占比 41% → 7%。同輸入重播（實驗紀錄 §5）：新組法少約 20%，原因是 Kisegae 描述只講穿著、集中在錨過濾名單前段，第 2 位改抽樣後往深處走。混搭後「至少一套 Kisegae 的排」72%（舊）／81%（未混搭）→ 100%；Kisegae 總數反而下降，因為第 1 位已是 Kisegae 的排第 2 位改給 Civitai——要的是每排兩種來源都看得到 |
| τ、P | **τ=5、P=10**（看過一次權重剩約 13.5%，兩次 1.8%） | 使用者不要抽很深，要看過的退得夠多；τ=8 抽得更深但不需要 |
| 相同 tag 的片段 | **依「該維度的 tag 集合」合併成一筆**，看過次數也用這個 key 記 | 知識庫很多片段在某維度 tag 完全相同；只認 presetId 時看過 A，一模一樣的 A' 權重仍滿 |
| 隨機種子 | **(session, turnIndex, 維度, 第幾批)** | 同樣狀態重播得到同一批，測試與除錯可重現 |
| 探索位「換」什麼 | **不看錨、看整體描述，在這個維度換一種搭法** | 取代要發生，得在使用者講過的 facet 上給不同選擇；「保留錨換其他部分」多半只是補 missing |
| 探索位的差異怎麼比 | **只比該維度 covered 的 facet**（全都沒講才比整個維度），用 facet 向量 | 比整個維度會挑到「只在沒講的地方不同」的，那不會發生取代；整套向量會混進其他維度 |
| 換一批的呈現 | **往右接**，批次間細線、標「第 N 批」；舊批可捲回去採用 | 排本來就能橫捲；加權把某套延後也不會讓人拿不到它 |
| 換一批走哪裡 | **獨立端點，不經模型、不算一輪**；只有最新一張定稿卡、非忙碌時能按 | 推薦本來就由伺服器產生、模型不知道；採用端點不檢查「是否推薦過」，換批出來的直接能採用 |
| 實作分工 | **SQL 取候選，C# 純函式合併、加權、抽樣、挑探索位** | 邏輯可單元測試；SQL 只改 LIMIT 再加一條依 id 取 facet 向量的查詢。全 SQL 做法三層接起來再加權難讀難測 |
| 看過延後套到「很像的」 | **不在本案** | 每次都要算候選與看過者的 facet 向量相似度；先做 tag 集合合併，實際用了仍覺得重複再加 |
| bandit／Thompson sampling | **不在本案** | 要跨 session 的採用回饋來學，目前只有 24 次採用；本案記下的名次與機率正好是之後需要的資料 |
| DPP（相關＋多樣一起抽） | **不做** | 同排撞在一起不常見，多樣性交給探索位就夠 |

## 3. 範圍與挑選

### 3.1 做

**追問卡**：不動。只推被問的維度、每排 3 套相關、不加權、不記看過、沒有換一批。

**定稿卡**：每個維度一排，每批 2 套相關位＋1 套探索位。

**相關位**：

1. 三層各取前 `RecommendationPoolSize`（30）筆：字面錨（有錨時）、近似錨（有 covered facet 錨向量時）、純向量。跟追問卡不同，三層都查、不再是「前一層不到 2 筆才退下一層」：接成一條名單後，字面錨命中多時後兩層自然排在 30 名之後、幾乎抽不到；命中少時才輪到它們。
2. 依序接成一條名單，presetId 重複的留第一次出現的。
3. 依「該維度的 tag 集合 key」合併，只留名次最前的一筆。
4. 有效名次 = 名次 + P × 看過次數（key 為單位）。
5. 第 1 位取有效名次最小的；第 2 位從其餘依 `exp(−有效名次/τ)` 抽。第 2 位先看來源混搭：其餘裡有效名次 ≤ `SourceMixWindow`（10）且來源（`source_ref` 前綴）跟第 1 位不同的，只從那些抽；一筆都沒有才從全部抽。
6. 每套帶自己的理由：含你講的（字面錨層）／接近你講的（近似錨層）／最接近你描述的（純向量層）。

**探索位**：

1. 取不過濾錨的純向量前 `RecommendationPoolSize` 筆，去掉跟相關位同 key 的。
2. 只留至少在一個「比較用 facet」帶 tag 的候選。比較用 facet = 該維度 covered 的 facet；全都沒 covered 時是該維度全部 facet。
3. 差異 = 1 −（跟相關位各自的相似度取大者）；相似度 = 兩者共有的比較用 facet 的 facet 向量 cos 平均，沒有共有 facet 記 0。相關位不到 2 套時跟有的那幾套比；一套都沒有時差異全當 1，排名退回原名次。
4. 依差異由大到小排名，再用同一個公式（名次 + P × 看過次數、τ）抽 1 套。
5. 理由標「換個搭法」。

**看過**：session 為每個維度記「tag 集合 key → 次數」。定稿卡產生的那一批與每次換一批，三套各加一次。

**換一批**：`POST /api/sessions/{id}/recommendations/next`，帶 `{ dimension, turnIndex }`。只有最新一張定稿卡、session 沒在跑回合時接受。錨、查詢句、定稿 positive tag 從 session 現況重算，session 多存最後一次定稿的 positive tag。批次編號從定稿卡那批 = 1 起算。

**設定**（`OrchestratorOptions`）：`RecommendationPoolSize` = 30、`RecommendationSeenPenalty` = 10、`RecommendationTemperature` = 5。

### 3.2 不做

- 追問卡的任何改動，包括追問輪推哪些維度（F3 驗收的觀察：追問輪只推被問的維度、模型常不問穿著，近似錨很少出現。重心移到定稿卡後，定稿卡涵蓋全部維度，近似錨自然有機會出現）。
- 同排 MMR、DPP、bandit、看過延後套到相似者（§2）。
- 跨 session 的「看過」：session 在記憶體，壽命同 session。
- IPS（依抽中機率反向加權）的採用率估計：資料先記，報表先不算。

## 4. 後端元件

### 4.1 `SlateSelector`（新，純函式）

放在 `Orchestration/`，不碰資料庫與 session，輸入都是值：

- `Merge(anchored, similar, plain, keyOf)`：三層接成一條名單，presetId 去重、tag 集合 key 合併，回傳帶「原名次」與「理由」的候選。
- `PickRelevant(candidates, seen, P, τ, rng)`：回 2 套，第 1 位取有效名次最小（同分取原名次小者），第 2 位依權重抽；每套附 `rank` 與 `prob`（第 1 位 `prob` = 1）。
- `PickExplore(candidates, relevant, compareFacets, facetVectors, seen, P, τ, rng)`：依 §3.1 探索位規則回 0 或 1 套，附 `rank`（差異排名）與 `prob`。
- `TagSetKey(facetTags, dimensionFacets)`：該維度全部 facet 的 tag 正規化（`TagAttribution.Normalize`）後排序、去重、串接。
- `Seed(sessionId, turnIndex, dimension, batch)`：SHA-256 取前 4 bytes 當種子（BCL 內建，不用多裝 `System.IO.Hashing`）。不用 `string.GetHashCode`：它每次程序啟動換隨機值，重啟後無法重現。

### 4.2 `RecommendationService`

- `BuildAsync` 依 outcome 分流：`AskOutcome` 走現行邏輯，一行不改；`FinalizedOutcome` 走新的 `BuildSlateAsync`。
- `BuildSlateAsync(session, dimension, batch, ct)`：取三層候選（`RecommendationPoolSize` 筆）、純向量候選（探索用，跟第三層同一次查詢）、候選與相關位的 facet 向量，交給 `SlateSelector`，成功後把三套的 key 記進 `session.SeenSets`。定稿時對每個維度呼叫一次（batch = 1），換一批時呼叫一次（batch = 該維度目前批次 + 1）。
- 定稿時把 `finalTags`（已排除基礎詞）存進 `session.LastFinalTags`，換一批時從這裡重算錨。

### 4.3 `PresetRepository`

- 既有三條 SQL 只換 LIMIT 參數（`@take` 傳 `RecommendationPoolSize`）。
- 新增 `FacetVectorsAsync(presetIds, facetIds)`：`SELECT preset_id, facet_id, embedding FROM preset_facet_embeddings WHERE preset_id = ANY(@ids) AND facet_id = ANY(@facets)`。子表主鍵就是 `(preset_id, facet_id)`，預期走主鍵，實際看 §8 的 EXPLAIN。

### 4.4 Session

- `SeenSets`：`Dictionary<string 維度, Dictionary<string key, int 次數>>`。
- `SlateBatches`：`Dictionary<string 維度, int>`，目前批次，每張新定稿卡歸零後從 1 起算。
- `LatestSlateTurn`：最新一張定稿卡的 turnIndex；換一批只接受這個 turnIndex。
- `LastFinalTags`：見 §4.2。
- 這些都不進 `Snapshot`／`Restore`：推薦在一輪成立之後才產生，被攔截的輪不會走到這裡。

### 4.5 換一批端點

`POST /api/sessions/{id}/recommendations/next`，body `{ dimension, turnIndex }`：

- session 不存在 → 404。
- `turnIndex` 不等於 `LatestSlateTurn`，或 retrieval 關閉 → 409。
- `dimension` 不屬於本 profile → 400。
- `session.Lock` 用 `WaitAsync(0)` 搶，搶不到（正在跑回合）→ 409。
- 成功 → 200，body 是一個 `RecommendedDimension`（跟 SSE 事件內的同形），寫 `Recommendations_Next` audit。
- 逾時（`RecommendationTimeoutSeconds`）或例外 → 503，寫 `Recommendation_Failed`（`stage: "next"`），`SeenSets` 與批次不更新。

### 4.6 採用

`AdoptRequest` 加選填 `Batch`，伺服器不驗證，照寫進 `Turn_Completed.adoption.batch`。採用端點本來就不檢查「這套是否推薦過」，換批出來的直接能採用。

## 5. 事件與 audit

### 5.1 `recommendations` 事件

追問卡欄位全部照舊。定稿卡：

- `RecommendedSet` 加 `reason`（`anchored`／`similar`／`query`／`explore`；追問卡為 null）、`anchorTags`（該套命中或有貢獻的錨；`query`／`explore` 為空）、`rank`、`prob`。
- `RecommendedDimension` 加 `batch`（定稿卡那批 = 1）。定稿卡這一排的 `anchored`／`similar` 固定 false、列層級 `anchorTags` 為空，理由看每一套。
- 每批的順序：相關位第 1、第 2，探索位最後。

### 5.2 audit

- `Turn_Completed.recommendations.dimensions[]` 加 `batch` 與 `sets: [{ presetId, reason, rank, prob }]`；`presetIds` 保留（舊報表與查詢相容）。
- 新事件 `Recommendations_Next`：`turn_index` 記那張定稿卡的輪次，`payload` `{ dimension, batch, sets: [{ presetId, reason, rank, prob }] }`，另記延遲。
- `Turn_Completed.adoption` 加 `batch`（前端有送才有）。
- `Recommendation_Failed.stage` 多一個值 `next`。

## 6. 前端

`RecommendationStrip.vue`：

- 定稿卡的每一套，圖下多一行理由：含你講的 X／接近你講的 X／最接近你描述的／換個搭法（`lib/copy.ts` 新增 `setReasonLabel`）。探索位外框改虛線。
- 排尾一個跟卡片同大小的「換一批」格子，只在 `adoptable` 時出現；按下後變成載入中，成功就把新批接在右邊，批次間一條細直線、標「第 N 批」；回空批顯示「這個維度沒有更多了」並收起按鈕；失敗顯示「換一批失敗，再按一次」。
- 新批寫進 store 裡那張卡的 `recommendations`（該維度的 `sets` 後面接上，另記每套所屬批次），跟對話一起存在前端（現行 persist 機制）。
- 採用對話框送出時帶該套的 `batch`。
- 追問卡畫面不變：沒有 `reason` 時照舊顯示列層級的 `recommendationLead`。

## 7. 量測報表

`scripts/adoption_report.py` 新增一節「定稿卡推薦組法」，資料來自 `Turn_Completed` 與 `Recommendations_Next`：

- **各理由採用率**：以套為單位，分母是出現過的套數（含換一批），分子是被採用的套數。
- **各理由取代率**：該理由的採用裡，`replaced` 非空的比例，與平均換掉幾個 facet。本案最主要看的數字，看探索位與相關位的差別。
- **換一批**：定稿排有按過換一批的比例、每排平均按幾次、採用來自第 2 批以後的比例。
- **名次與採用**：依原名次分桶（0、1–4、5–9、10+），列出現次數與採用次數。
- **採用對回哪一套**：同一張定稿卡的所有批次裡找該維度、該 presetId；有 `adoption.batch` 就用它指定的那批，沒有取最後出現的那批。
- **舊資料**：沒有 `sets` 的列照 `anchored`／`similar` 推回理由（anchored → `anchored`、similar → `similar`、都沒有 → `query`），批次當 1、`rank`／`prob` 當缺值，不進名次分桶。

## 8. 效能

實測環境：開發庫 `prompt-copilot-db`（19,354 筆 preset）。查詢向量與字面錨取自 preset id 8886（`clothing.footwear = sandals`）；`@facets` 用 portrait 的 clothing 六個 facet（`clothing.head`／`clothing.upper`／`clothing.lower`／`clothing.footwear`／`clothing.material`／`clothing.accessories`）；`@anchorFacets = {clothing.footwear}`、`@anchorTags = {sandals}`；`RecommendSimilarSql` 的 `@maxDist = 0.30`。每條 `EXPLAIN (ANALYZE, BUFFERS)` 跑 5 次取中位數；完整 5 次原始輸出與 13 筆換一批延遲樣本見 `docs/experiments/2026-09-30-recommendation-slate.md` §4。

| 查詢 | LIMIT | 中位數 (ms, n=5) | 頂層節點 |
| :--- | ---: | ---: | :--- |
| `RecommendSql`（純向量） | 3 | 1.84 | `Limit` → `Index Scan using idx_presets_embedding`（HNSW） |
| `RecommendSql`（純向量） | 30 | 2.38 | 同上 |
| `AnchoredRecommendSql`（字面錨） | 3 | 11.86 | `Limit` → `Sort`(top-N heapsort) ← `CTE c` = `Bitmap Heap Scan` on `idx_presets_facet_ids`（GIN，不走 HNSW），CTE 實際只 17 列 |
| `AnchoredRecommendSql`（字面錨） | 30 | 11.45 | 同上（`Sort` 改 quicksort，一樣只 17 列） |
| `RecommendSimilarSql`（近似錨） | 3 | 4.50 | `Limit` → `Sort`(top-N heapsort) ← `Nested Loop`：`Bitmap Index Scan on idx_pfe_facet` join `Index Scan using prompt_knowledge_presets_pkey`（走主鍵），maxDist 內共 329 列 |
| `RecommendSimilarSql`（近似錨） | 30 | 4.48 | 同上 |
| `FacetVectorsSql`（1 facet：`clothing.footwear`，32 個 id） | – | 0.19 | `Index Scan using preset_facet_embeddings_pkey`（純走複合主鍵 `(preset_id, facet_id)`） |
| `FacetVectorsSql`（6 個 clothing facet，32 個 id） | – | 0.51 | `Bitmap Heap Scan` ← `BitmapAnd`(`idx_pfe_facet` 交集 `preset_facet_embeddings_pkey`)，仍用得到主鍵，只是混用 facet_id 次索引，82 列 |

關鍵結論：

- `RecommendSql` 走 HNSW（`idx_presets_embedding`），LIMIT 3→30 中位數只差 0.5ms 上下，池子變大幾乎不增加成本。HNSW 前 3 名比對（spec §9.2 移過來的檢查）：LIMIT 30 前 3 筆 id（8886、10507、14229）與 LIMIT 3 的結果逐筆相同，本次量測沒出現漏筆。`hnsw.ef_search` 預設 40，LIMIT 30 在範圍內；**池子若調到 40 以上要一起調 `ef_search`**，否則 HNSW 回不滿。
- `AnchoredRecommendSql` 完全不走 HNSW——`idx_presets_facet_ids`（GIN）bitmap scan 篩出候選後才算距離，MATERIALIZED CTE 把符合的列全算完，LIMIT 3 與 30 幾乎同耗時（11.86 對 11.45ms），印證原設計預期；涼鞋錨的池子實測 17 列。
- `FacetVectorsSql` 查單一 facet 時純走複合主鍵；查全部 6 個 clothing facet 時規劃器改用 `BitmapAnd`（主鍵 bitmap 交集 facet_id 索引 bitmap），仍用得到主鍵，只是混用次索引；兩種情況都在 1ms 以內，對整批延遲影響可忽略。

換一批延遲（`Recommendations_Next` audit）：對 `a62c51fd8bb74a63b39dff3a7d2189bd`（瀏覽器驗收用的 session，turnIndex 4）的 6 個維度各補呼叫 2 次（n=12 新樣本），加上驗收本身留下的 1 筆舊樣本，共 13 筆：中位數 538ms、最大 2432ms。最大值就是那筆驗收留下的舊樣本（turnIndex 2），比其餘 12 筆（389–625ms）高出 4 倍以上——判斷是容器重啟後第一次呼叫帶到的 embedding API 暖機時間，不算進常態範圍；扣掉這筆後 12 筆中位數 536.5ms、最大 625ms、最小 389ms。

定稿卡整張（6 個維度）推薦延遲：沒有獨立的整批 audit 紀錄，用「換一批單排延遲中位數 × 6」估：538ms × 6 ≈ 3.2 秒（**估計值**）。這個估法會偏高：換一批每次呼叫各自帶一次 embedding API 呼叫，定稿卡實際是 6 個維度共用同一次 embedding 呼叫（`EmbedAsync` 一次把查詢句與所有近似錨文字一起送出）再依序跑 6 次 SQL，所以真正的整張延遲應該低於這個估計，缺口大概是少算的 5 次 embedding 往返。

## 9. 測試

### 9.1 後端單元（xUnit）

- `SlateSelector`：三層接起來的去重與 key 合併；有效名次；第 1 位固定取最高；同種子同結果；看過後抽中機率下降；探索位只比 covered facet、沒 covered 時比全部；沒有可用探索候選時回 0 套；`prob` 正確；固定種子跑 10,000 次，抽中頻率與權重誤差在容許範圍內。
- `Seed`：同輸入同值，換任一欄位值不同。
- `RecommendationService`：追問卡輸出與現行逐欄相同（既有測試全過）；定稿卡每排 2＋1、`reason`／`batch` 正確；`SeenSets` 更新；`LastFinalTags` 存下。
- 換一批端點：404／409（非最新卡、忙碌、retrieval 關閉）／400／503；批次遞增；失敗不更新 `SeenSets`。

### 9.2 整合（資料庫）

- `FacetVectorsAsync` 回傳正確的列、空 id 清單不查資料庫。LIMIT 30 的前 3 名是否等於 LIMIT 3，靠開發庫真實資料才有意義，放在 §8 量測時一起比對。

### 9.3 前端（Vitest）

- reducer／store：新批接在後面、批次標記正確；持久化存了再讀回一樣。
- `setReasonLabel` 四種理由的文字。
- 採用請求帶 `batch`。

### 9.4 腳本（pytest）

- `adoption_report` 新段落的各項數字；舊資料推回理由；採用對回批次的規則。

### 9.5 瀏覽器驗收（Claude 用 Playwright 跑，寫進 `docs/eval-cases.md`）

- G1：定稿卡每排 2＋1，理由標示正確，探索位虛線。
- G2：換一批接成第 2 批，跟第 1 批沒有整批重複。
- G3：從第 2 批採用，`Turn_Completed.adoption.batch` = 2。
- G4：定稿後修改一次，新定稿卡不會整排重複上一張。
- G5：追問卡畫面與推薦跟改前一樣。

## 10. 文件同步

跟程式放在同一個 commit：

- `docs/單輪流程說明.md`：C# 推薦那段（「C# 端從 2026-09-25 起多一層伺服器端的推薦」）補上定稿卡的組法與換一批。
- 主規格 `2026-09-21-genai-prompt-copilot-design.md`：audit `event_type` 清單（`Recommendations_Next`、`Turn_Completed.recommendations` 新欄位、`adoption.batch`、`Recommendation_Failed.stage`）、推薦段、SSE 事件表 `recommendations` 列、新端點。
- `2026-09-25-set-recommendations-design.md`：開頭加一行指向本案。
- `docs/eval-cases.md`：G1–G5。
- `scripts/adoption_report.py` docstring。
