# 整套組合推薦與採用：設計

日期：2026-09-25
來源：`docs/superpowers/plans/2026-09-25-rag-value-and-trace.md` §8，2026-09-25 討論定案。
前情：知識庫開關與檢索過程顯示已 merge（master `36d635d`，spec `2026-09-25-retrieval-switch-and-trace-design.md`）。計畫 §4.2 的離線 A/B 仍往後排，等本案量過採用率再決定。

---

## 1. 問題與定位

使用者的疑慮是「RAG 借 tag 的效果會不會跟純 LLM 差不多」。本案不回答這個問題，而是換一個 RAG 才做得到的用法：**把知識庫裡真實存在、有圖、彼此搭配過的完整組合攤給使用者看，讓他採用。** LLM 寫得出 `sandals`，寫不出「這套搭起來長什麼樣」，也沒辦法讓使用者看圖決定。

定位是**生圖的推薦系統**：每次追問與每次定稿，對每個維度都推薦 2–3 套有圖的組合；不管該維度講滿了沒有。使用者看了圖可以「補上我沒講的」，也可以「放棄我原本的設定改採圖上的」。後者是純 LLM 做不到的事，也是量測 RAG 價值最有說服力的數字。

量測題目從「tag 品質」變成：

- **採用率**：有推薦的定稿輪裡，使用者採用了一套的比例。
- **擴充率**：採用時補進了幾個原本 missing 的 facet。
- **取代率**：採用時換掉了幾個原本 covered 的 facet（看圖後放棄自己的設定）。

## 2. 決定紀錄

| 題目 | 決定 | 理由 |
| :--- | :--- | :--- |
| 「整套」指什麼 | **單一維度的片段**（例：一個 Clothing 片段涵蓋頭／上／下／鞋／配件） | 尊重使用者在其他維度講過的話；沿用 facet 範圍檢索。整張圖當一個 look 的版本等量過採用率再看 |
| 採用怎麼影響既有設定 | **逐 facet 對照後採用**：每個 facet 各自選「留我的」或「照它的」 | 「只填空」與「整套照它的」都是它的特例；對照表本身就是看圖比設定的介面 |
| 推薦何時出現 | **追問時**（正在問的維度）與**每次定稿**（含重新定稿，所有適用維度） | 兩處都是使用者本來就在做決定的時候；定稿後才真的在看結果 |
| 哪些維度 | **本 profile 適用的全部維度，不設「還有 missing」的條件** | 推薦系統的邏輯：講滿了也推薦，讓使用者看到別的搭法 |
| 推薦由誰產生 | **伺服器**（模型不知道有推薦這回事）；**採用走對話**（伺服器組一句使用者訊息，模型照一般流程追問或重新定稿） | 推薦要「每次都在、每次一樣」，那是伺服器的事；改設定要理解語意、重新定稿，那是模型的事。對話仍是全 agentic |
| 追問階段的錨從哪來 | **模型在 `SetFacetStates` 把 covered facet 標 covered 時順便給英文 `tags`**（「涼鞋」→ `sandals`），伺服器存成 `session.FacetTags`，追問與定稿都用它當錨；定稿時再加上定稿 positive 的 tag | 追問是使用者第一次看到推薦的地方，沒錨的推薦第一印象會差。翻錯就抓不到錨、退回無錨，不會壞 |
| 錨過濾的順序 | **SQL 先依 `facet_tags` 過濾，再依向量排序**（同 `SearchPresets` 的做法） | 涼鞋只有 20 筆、穿著片段 2,357 筆；先取向量前 30 再過濾會漏掉大半 |
| 資料庫端 facet 層級向量 | **不在本案**，另開一案排在本案之後 | 它改的是借 tag 的檢索排序，要分開量；它完全建立在 `facet_tags` 上，本案回填完就能做 |
| 推薦要不要進 ledger | **不進**；只有採用時才記 | 模型沒看過推薦的片段；寫進 ledger 會把模型自己寫的詞誤標成 rag |

## 3. 範圍

### 3.1 做

1. 片段新欄位 `facet_tags`，回填腳本，migration，seed v2。
2. `RecommendationService` 與 `recommendations` 事件。
3. `POST /api/sessions/{id}/messages` 的 `adopt` 欄位；伺服器組句；`session.Adoptions`；tag 來源第四種 `adopted`。
4. `system.md` 一條「採用」規則；`FacetStateEntry` 加 `tags` 欄位與 `session.FacetTags`。
5. 前端：AskCard／FinalCard 的「參考組合」區塊、`AdoptDialog` 對照表、`adopted` chip 樣式。
6. audit 的 `recommendations` 與 `adoption`；`scripts/adoption_report.py`。
7. 測試與 `docs/eval-cases.md` 驗收案例；`docs/單輪流程說明.md` 同步。

### 3.2 不做

- 整張圖當一個 look（跨維度採用）。
- 同義詞（`slippers` vs `sandals`）：錨比對用字尾規則，抓不到同義詞先接受；後續的 facet 向量案可用「鞋履 facet 向量最近的」補這個缺口。
- **facet 層級向量（後續案）**：子表 `preset_facet_embeddings(preset_id, facet_id, embedding)`，從 `facet_tags` 算；`SearchPresets` 的 facetId 項目改比 facet 向量（現在是「過濾準、排序不準」：facet 過濾後仍拿整套向量跟「涼鞋」比）；錨硬過濾抓不到時退回 facet 向量最近的。排在本案之後、§4.2 A/B 之前，用檢索細節顯示量改前改後。
- `structure.py` 產生 `facet_tags`：語料現在沒在長，新片段重跑回填腳本即可。
- 計畫 §4.2 的離線 A/B。

## 4. 資料：`facet_tags`

### 4.1 欄位

```sql
ALTER TABLE prompt_knowledge_presets ADD COLUMN IF NOT EXISTS facet_tags JSONB;
```

形如 `{"clothing.footwear":["sandals","platform footwear"],"clothing.upper":["purple kimono","detached sleeves"]}`。鍵只能是該片段 `facet_ids` 裡的 facet；每個 tag 只歸一個 facet；歸不進任何 facet 的 tag 不寫入。`NULL` 表示尚未回填，推薦查詢會跳過這些列。

`db/init/001_schema.sql` 加欄位（新建資料庫用）；`db/migrations/002_facet_tags.sql` 放同一句 `ALTER TABLE … IF NOT EXISTS`（既有資料庫用）。`docker/seed.sh` 在計數之前先跑 `db/migrations/*.sql`（冪等，每次啟動都跑），這樣既有容器升級不用手動步驟；開發庫也用同一個檔案手動套。

### 4.2 回填腳本 `scripts/backfill_facet_tags.py`

- 只處理 `facet_tags IS NULL` 的列，每批 20 筆送 Gemini（沿用 `scripts/pipeline/gemini_client.py`）。每筆給 `id`、`facet_ids`（附中文標籤）、`prompt_snippet` 拆好的 tag 清單；要它回 JSON：每筆 `{id, assignments: {tag: facetId | "other"}}`。
- 寫回前驗證：facetId 必須在該筆 `facet_ids` 內，否則視為 `other`；tag 必須是輸入清單裡的原字，多出來的丟掉；一筆的 assignments 少於輸入 tag 數就補 `other`。全部 `other` 的列寫 `{}`（不是 NULL），避免重跑時再送一次。
- 每批一個交易寫回；可中斷、可重跑；`--limit`／`--dry-run` 與其他腳本一致。結束印統計：處理幾筆、`other` 佔比、每個 facet 的 tag 數分佈。
- 測試 `scripts/tests/test_backfill_facet_tags.py`：回覆解析、非法 facet 歸 other、多餘 tag 丟棄、缺的補 other、只挑 NULL 列、`{}` 不重跑。

### 4.3 Seed v2

回填完成後 `scripts/export_seed.py` 匯出 v2（data-only dump 會帶 `facet_tags`），`docker-compose.yml` 的 `SEED_URL` 預設值改成 seed-v2。v1 dump 對升級後的 schema 仍能還原（欄位可 NULL），只是推薦查不到東西，`seed.sh` 匯入後若 `facet_tags` 全 NULL 印一行提示「請跑 backfill_facet_tags.py 或改用 seed-v2」。

### 4.4 `PresetRepository`

- `PresetHit` 與 `SearchAsync` 不動（`PresetDetail` 見本節最後一點）；新增 `RecommendAsync(float[] query, IReadOnlyList<string> dimensionFacets, IReadOnlyList<string> anchorFacets, IReadOnlyList<string> anchorTags, int take, CancellationToken)`。`anchorFacets` 是該維度的 covered facet，`anchorTags` 是它們的錨 tag（已 `Normalize`、去重，5.3）；兩個清單任一為空就不加錨條件，走下面「沒錨」那條：

```sql
-- 組合條件 <set>（兩種形式共用）
facet_ids && @facets                                   -- GIN 粗篩
  AND facet_tags IS NOT NULL
  AND preset_embedding IS NOT NULL
  AND (SELECT count(*) FROM jsonb_each(facet_tags) AS ft(facet_id, tags)
       WHERE ft.facet_id = ANY(@facets) AND jsonb_array_length(ft.tags) > 0) >= 2

-- 沒錨：HNSW 依向量取前 N
SELECT id, title, facet_ids, facet_tags, image_url, source_ref, preset_embedding <=> @q AS dist
FROM prompt_knowledge_presets
WHERE <set>
ORDER BY dist
LIMIT @take

-- 有錨：先整批過濾（MATERIALIZED），再排序；距離在 CTE 裡算，暫存不帶 768 維向量
WITH c AS MATERIALIZED (
  SELECT id, title, facet_ids, facet_tags, image_url, source_ref, preset_embedding <=> @q AS dist
  FROM prompt_knowledge_presets
  WHERE <set>
    -- 任一指定 facet 底下有 tag 整段相等或字尾相符
    AND EXISTS (
      SELECT 1 FROM jsonb_each(facet_tags) AS ft(facet_id, tags), jsonb_array_elements_text(ft.tags) AS t(tag)
      WHERE ft.facet_id = ANY(@anchorFacets)
        AND (replace(lower(t.tag), '_', ' ') = ANY(@anchorTags) OR replace(lower(t.tag), '_', ' ') LIKE ANY(@anchorSuffixes))
    )
)
SELECT id, title, facet_ids, facet_tags, image_url, source_ref, dist
FROM c
ORDER BY dist
LIMIT @take
```

組合條件數的是 `facet_tags` 裡「屬於這個維度、而且陣列不是空的」鍵，不是 `facet_ids` 的交集。原因是回填會對歸不進任何 facet 的列寫 `{}`，也可能留下空陣列；這種 facet 採用時拿不到 tag，組合在這個維度就要有 2 個以上採得到東西的 facet。

有錨時要用 `MATERIALIZED`。不這樣寫的話，規劃器會走 HNSW iterative scan，把錨當 Filter 邊掃邊丟；掃到 `hnsw.max_scan_tuples`（預設 20,000，全表已近兩萬筆）就停。罕見的錨（例如涼鞋只有 20 筆，穿著有 2,357 筆）會在表變大後被默默漏掉，但錨的結果必須精確。先把命中的列整批撈出來再排序，走的是 `facet_ids` 的 GIN 索引加排序，跟掃描上限無關。沒錨的池子大，HNSW 取向量前 N 正是要的，維持原路。

`@anchorTags` 是全部錨 tag，`@anchorSuffixes` 是每個錨跳脫 LIKE 的 `\`、`%`、`_` 之後前面加 `'% '`（字尾相符，與 `TagAttribution.EndsWithWord` 同義）。`@anchorFacets` 是該維度全部 covered facet（定稿時 positive 的 tag 歸到每個 covered facet，所以不只有 `FacetTags` 的那幾個）；facet 與 tag 的配對放寬成「該維度任一 covered facet 命中任一錨」，錨本來就是該維度的詞，跨 facet 誤中的機率低，SQL 也簡單得多。資料庫這一側只做小寫與底線換空白，不剝權重與括號（`docs/known-issues.md` §9）。

回 `PresetCandidate(Id, Title, FacetIds, FacetTags: IReadOnlyDictionary<string, IReadOnlyList<string>>, ImageUrl, SourceRef, Dist)`。

- `GetAsync` 回的 `PresetDetail` 加 `FacetTags`（採用時伺服器要用）。

## 5. 推薦的產生

### 5.1 觸發

`AgenticOrchestrator` 在 `apply` 階段之後、`Turn_Completed` audit 之前：本輪 `Outcome` 是 `AskOutcome` 或 `FinalizedOutcome`，且 `session.RetrievalEnabled`，就呼叫 `RecommendationService.BuildAsync(session, outcome, turnIndex, ct)`，結果非空就 `writer.TryWrite(new RecommendationsEvent(...))`。推薦失敗（DB／embedding 例外，或超過推薦自己的逾時 `Orchestrator:RecommendationTimeoutSeconds`，預設 20 秒）只記 log 與 audit `Recommendation_Failed`，不影響本輪結果——推薦是附加的。

### 5.2 哪些維度

- `AskOutcome`：`asks[].dimension` 的維度，順序照 asks。
- `FinalizedOutcome`：本 profile 適用的全部維度，順序照 facets.yaml 的維度順序。`FacetCatalog` 新增 `DimensionsOf(profile)`（現在只有 `FacetsOf(profile, dimension)`）。

### 5.3 每個維度的查詢

1. **查詢向量**：本 session 所有使用者訊息原文（不含伺服器組的採用句）依序串接、取最後 500 字，用 `RetrievalQuery` 任務嵌入。一輪只嵌入一次，各維度共用。
2. **錨**：該維度每個 **covered** facet 的錨 tag ＝ `session.FacetTags[facetId]` 拆出的 tag（5.5）；`FinalizedOutcome` 時再加上本次定稿 `positive` 拆出的 tag（歸到該維度所有 covered facet；基礎畫質詞以 `TagAttribution.IsBase` 排除，它們每次定稿都有、不屬於任何 facet）。拆與正規化用 `TagAttribution.Split`＋`Normalize`（這兩個與 `EndsWithWord` 原本是 private，改 public，規則只有一份）。沒有 covered facet、或 covered facet 都沒有 tag 的維度，錨為空。
3. **候選**：`RecommendAsync(vec, facets, covered, anchors, take)`，`take` 是 `Orchestrator:RecommendationTake`（預設 3）。有錨且回 ≥ 2 筆：`anchored=true`，`anchorTags` 是候選 covered facet 底下實際命中的錨（C# 照 SQL 的規則算：資料庫 tag 等於錨，或以「空白＋錨」結尾；不算反方向，列出來的就是過濾用到的錨。例外：C# 對資料庫 tag 用完整的 `Normalize`，`facet_tags` 保留權重或括號時可能多列一個 SQL 沒比中的錨，見 `docs/known-issues.md` §9）。有錨但回 < 2 筆：兩個錨清單都傳空再查一次，`anchored=false`。沒錨：直接查一次，`anchored=false`。
4. 追問與定稿走同一條邏輯，差別只在定稿多了 positive 的 tag 當錨。
5. 候選少於 1 筆的維度不列。

### 5.4 事件

```ts
{ type: 'recommendations', turnIndex: number,
  dimensions: [{ dimension: string, label: string, anchored: boolean, anchorTags: string[],
    sets: [{ presetId: number, title: string, imageUrl: string | null, sourceRef: string | null, dist: number,
      facets: [{ facetId: string, label: string, state: 'covered' | 'missing' | 'waived' | 'notApplicable', tags: string[] }] }] }] }
```

- `facets` 列該維度對本 profile 的全部 facet（順序照 facets.yaml），`tags` 取 `facet_tags[facetId]`，沒有就空陣列；`state` 是本輪結束時的 facet 狀態。
- `RecommendationsEvent` 加進 `AgentEvent` 與 `AGENT_EVENT_TYPES`；SSE 序列化與其他事件一致。
- `GET /api/sessions/{id}` 不回推薦：對話流本來就在前端 sessionStorage。

### 5.5 `session.FacetTags`：模型給的英文 tag

- `FacetStateEntry` 加 `tags`（`string?`，英文、逗號分隔）：「facet 標 covered 時，附使用者那一項的英文 tag（例：涼鞋 → `sandals`；銀色雙馬尾 → `silver hair, twintails`）」。四個帶 `facetStates` 的工具（`SetFacetStates`、`AskUser`、`Discuss`、`FinalizePrompt`）共用這個型別，任何一處都能更新。
- `SessionPlugin.Apply`：`tags` 非空就寫 `session.FacetTags[facetId]`；狀態改成非 covered 時移除該 facet 的 tags；`SetProfile` 重設時清空。納入 `SessionSnapshot`／`Restore`。
- `system.md`「## 流程」第 1 條「把使用者這句話已經描述到的 facet 標 `covered`」後面加「，並在 `tags` 附上那一項的英文 tag」。工具描述同步。
- `dimensions` 事件加 `tags`（給檢索細節顯示：儀表板 facet 旁看得到模型把「涼鞋」翻成什麼）。
- 模型沒給或翻錯：該 facet 沒有錨，退回無錨推薦；不重試、不報錯。

## 6. 採用

### 6.1 請求

`MessageRequest` 改為 `(string? Text, AdoptRequest? Adopt)`，`AdoptRequest(long PresetId, string Dimension, IReadOnlyList<string> Take)`。

- `Adopt` 為 null：行為與現在完全相同（`Text` 必填）。
- `Adopt` 非 null：`Text` 忽略。驗證失敗回 400 `ErrorBody`：preset 不存在／`facet_tags` 為 NULL、`Dimension` 對本 profile 不存在、`Take` 有不屬於該維度的 facet、`Take` 裡有 facet 在該 preset 的 `facet_tags` 沒有 tag、`Take` 為空。session `retrieval` 為 off 回 409。Profile 尚未設定回 409。
- 通過後由伺服器組出使用者訊息文字（6.2），之後與一般訊息走同一條 orchestrator 路徑（同樣的忙碌檢查、SSE、audit `raw_input` 就是這句）。

### 6.2 伺服器組句

固定格式，`Take` 依 facets.yaml 順序，其餘該維度 facet（`notApplicable` 除外）列為保留：

```
採用〈{title}〉（知識庫 #{id}）：{facet 中文名}照它的（{tags 以「, 」相接}）、{…}；{facet 中文名}、{…}保留我的。
```

沒有保留項時省略分號後半段。這句同時是使用者泡泡顯示的文字、模型看到的訊息與 audit 的 `raw_input`。

### 6.3 Session 記帳

```csharp
public sealed record Adoption(int TurnIndex, long PresetId, string Dimension,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Taken, IReadOnlyList<string> Kept,
    IReadOnlyList<string> Filled, IReadOnlyList<string> Replaced);
```

- `Filled`＝`Take` 裡採用前狀態是 missing 的；`Replaced`＝採用前是 covered 或 waived 的。
- `session.Adoptions` 列表；納入 `SessionSnapshot`／`Restore`（本輪失敗要回滾）。
- 同時 `Ledger.Record` 該 preset（`LedgerHit(dimension, dist: 0, grounded: true)`）並 `MarkOffered(id, OfferedRef(turnIndex, dimension, "採用"))`。這樣定稿卡的 chip 能開抽屜，system prompt 的 offered 區段也會列它。

### 6.4 提示詞規則

`system.md`「## 流程」加第 6 條：

> 6. 使用者訊息以「採用〈」開頭時，那是他從推薦的組合裡挑了一套：「照它的」facet 寫入括號內的 tag（原字，不改寫）、狀態設 `covered`、`tags` 填同樣的字、note 記「採用知識庫 #編號」；「保留我的」facet 維持原狀。然後照第 1 條判斷：工具清單裡有 `AskUser` 而且還有 missing 的維度就 `AskUser`（不要再問剛採用的那些 facet），否則直接 `FinalizePrompt`。

本案的 prompt 改動只有這一條加上 5.5 的半句。`SystemPromptBuilderTests` 各加一條驗證存在。

### 6.5 tag 來源 `adopted`

`TagAttribution.Attribute` 多收 `IReadOnlyList<Adoption> adoptions`。優先序：base → **adopted** → rag → llm。adopted 比對：tag 與任一 `Adoption.Taken` 的 tag 整段相等或字尾相符（同 rag 規則），命中就 `TagSource(tag, "adopted", [presetId], title)`。多筆 adoption 命中時取最近一輪的。`TagOrigins` 計數加 `adopted`。

## 7. 前端

### 7.1 推薦區塊 `RecommendationStrip.vue`

- 掛在 AskCard 與 FinalCard 底部，資料來源是該輪的 `recommendations` 事件（transcript 條目上多一個 `recommendations?` 欄位，hydrate 時依 `turnIndex` 對上）。
- 一個維度一列：維度名；`anchored` 時副標「含你講的 {anchorTags}」，否則副標「最接近你描述的組合」；3 張縮圖（沿用現在的縮圖與署名做法）＋標題＋「採用」按鈕。縮圖點開既有抽屜。
- 「採用」只在**最新一張** ask／final 卡上可用（舊卡的狀態已失效，按鈕停用並提示「已有新的定稿」）。session 已 finalized 之後又追問時同理，以最新一張為準。
- 「顯示檢索細節」開關不影響推薦區塊。

### 7.2 對照表 `AdoptDialog.vue`

- 上方：標題、大圖（既有署名）、來源連結。
- 表格一列一個 facet（`notApplicable` 不列）：facet 名｜**你的**（covered→「保留你講的」、missing→「空白」、waived→「不指定」）｜**這套**（`tags` 以 chip 顯示；空則「這套沒有」）｜切換（留我的／照它的）。
- 預設：missing→照它的；covered、waived→留我的；「這套沒有」的列停用、固定留我的。狀態以目前的 `facetStates` 為準，不是推薦事件那份：出卡之後的討論輪可能改過狀態。
- 底部：「全部照它的」快捷（把可用列全切到照它的）、「確定採用」（至少一列照它的才可按）、取消。
- 確定後：`s.adopt({ presetId, dimension, take })`，store 走 `send` 同一條路徑（busy、SSE、persist），只是 body 帶 `adopt` 而非 `text`。使用者泡泡顯示伺服器組的那句：`SessionEvent`（串流的第一個事件）加可省略的 `text` 欄位，只在採用輪帶值；前端收到就把該輪的使用者條目文字換成它（一般訊息 `text` 為 null，不動）。

### 7.3 chip 樣式

`TagSource.origin` 加 `'adopted'`；`PromptBlock` 多一種樣式（與 rag 區分，同樣可點開抽屜），title 寫「採用〈標題〉帶進來的」。定稿卡的「檢索貢獻」區塊把 adopted 與 rag 分開列。

### 7.4 持久化

`recommendations` 事件與其他事件一樣存 transcript；`adopt` 送出當下 persist 的 `draft` 存空字串：採用沒有使用者原文可以放回輸入框，輪次中重載時輸入框是空的，要再按一次「採用」（一般訊息則是原文回到輸入框）。

## 8. audit 與量測

- `Turn_Completed` payload 加：
  - `recommendations: { dimensions: [{ dimension, anchored, presetIds }] }`（有發推薦事件的輪）。
  - `adoption: { presetId, dimension, take, filled, replaced }`（本輪是採用的輪）。
  - `tagOrigins` 多 `adopted`。
- 新事件 `Recommendation_Failed`（payload：`stage`、`errorClass`）。
- `scripts/adoption_report.py`：讀 `audit_logs`，輸出 Markdown：
  - 採用率＝有 `recommendations` 的定稿輪中，後來被採用的比例；另列追問輪的採用率。採用輪往回對：同 session 在它之前最近的一張追問卡或定稿卡就是它採用的那張（前端只讓人從最新那張卡採用，中間的討論輪不換卡），同一張卡被採用兩次只算一次；對不到推薦輪的採用另列一行。
  - `anchored` 與否的採用率對比：「有錨列採用率」與「無錨列採用率」。分母是推薦輪上出現過的維度列（`recommendations.dimensions[]` 每筆算一列），依該列的 `anchored` 分；分子是被採用的列，依採用對到的那張卡上同維度那列的 `anchored` 分，同一列採用兩次只算一次。另列「採用時該維度有錨」（以採用次數為分母）。
  - 平均 `filled` 數、平均 `replaced` 數、各維度採用次數。
  - 定稿 tag 裡 `adopted` 的佔比（來自 `tagOrigins`）。
  - `--since` 日期參數；沒有資料時印「尚無採用紀錄」。

## 9. 錯誤處理

| 情況 | 行為 |
| :--- | :--- |
| 推薦查詢或嵌入失敗 | 不發事件；audit `Recommendation_Failed`；本輪其他結果照常 |
| `facet_tags` 尚未回填（全 NULL） | 推薦事件沒有任何維度就不發；前端沒有區塊；`seed.sh` 印提示 |
| `adopt` 驗證失敗 | 400／409，session 狀態不變，不進 orchestrator |
| 採用後模型沒有 `FinalizePrompt` | 在追問卡上採用（session 還在收集、`AskUser` 在清單上）而且還有 missing 的維度時，追問剩下的維度是第 6 條的預期：定稿閘門本來就不放行沒問過的 missing。其他情況（Discuss 等）由現有規則處理（追問額度、協定違規重試）。兩種都照樣記 `Adoption`，tag 來源在下次定稿時生效 |
| 採用那一輪被攔截或失敗 | `Restore(snapshot)` 一併回滾 `Adoptions` 與 ledger |

## 10. 測試

### 10.1 後端單元（xUnit）

- `RecommendationServiceTests`：`AskOutcome` 只查被問的維度；`FinalizedOutcome` 查全部維度；錨來自 `FacetTags`（追問）與 `FacetTags`＋positive（定稿）；有錨命中 ≥2 取前 3 且 `anchorTags` 正確；命中 <2 退回無錨查詢並 `anchored=false`；沒有 covered facet 或沒有 tags 的維度不加錨；候選為 0 的維度不列；`retrieval` off 不呼叫；查詢向量只嵌入一次；例外不外拋。
- `PresetRepositoryTests`（既有整合測試風格）：`RecommendAsync` 的 ≥2 facet 與 `facet_tags IS NOT NULL` 過濾；錨的整段相等與字尾相符（`white sandals` 命中 `sandals`）；錨為空不加條件。
- `SessionPluginTests`：`tags` 寫入／狀態改非 covered 時移除／`SetProfile` 清空；snapshot／restore。
- `TagAttributionTests`：adopted 優先於 rag、次於 base；字尾規則；多筆 adoption 取最近。
- `SessionEndpointsTests`：`adopt` 的每一種 400／409；組句格式（含無保留項）；`Text` 被忽略。
- `SessionTests`：`Adoptions` 進 snapshot／restore；`Filled`／`Replaced` 分類。
- `SystemPromptBuilderTests`：第 6 條存在（採用後照第 1 條判斷追問或定稿）且 off 模式也存在（規則無害）。
- `AgenticOrchestratorTests`：`Turn_Completed` 的 `recommendations`／`adoption` payload；推薦失敗寫 `Recommendation_Failed`；在追問卡上採用、以 ask 收尾的輪照樣記採用。

### 10.2 前端（Vitest）

- `RecommendationStrip`：依事件渲染、`anchored` 副標、只有最新卡可採用。
- `AdoptDialog`：三種 state 的預設值、「這套沒有」停用、「全部照它的」、送出的 `take`。
- store：`adopt` 走 `send` 路徑、transcript 對上 `turnIndex`、重載還原。
- `PromptBlock`：`adopted` chip。

### 10.3 腳本（pytest）

- `test_backfill_facet_tags.py`（4.2）。
- `test_adoption_report.py`：用假 audit 列算採用率／filled／replaced。

### 10.4 手動驗收

`docs/eval-cases.md` 加 S1–S7（S5 重載、S6 `adoption_report.py`、S7 在追問卡上採用，見該檔）：

- S1：「一個少女穿涼鞋」→ 儀表板鞋履 facet 顯示模型給的 `sandals`；追問卡下有穿著維度的推薦、`anchored=true`、副標含 `sandals`。
- S2：直接定稿後定稿卡下每個維度都有推薦；穿著維度 `anchored=true` 且副標含 `sandals`。
- S3：採用一套、上身照它的、鞋留我的 → 使用者泡泡是伺服器組句 → 新定稿卡上身 tag 是 `adopted` chip、鞋仍是原詞 → audit `adoption.filled` 含 `clothing.upper`。
- S4：`retrieval` off 的 session 沒有推薦區塊；直接打 `adopt` 回 409。

## 11. 文件同步

- `docs/單輪流程說明.md`：加「推薦與採用」一節（與程式同一個 commit）。
- 主規格 `2026-09-21-genai-prompt-copilot-design.md`：§5 tag 來源加 `adopted`；§9 ledger 加採用時寫入；§12.3 指到 S1–S6。
- `README.md`：功能列表加一行。
- `docs/known-issues.md`：加「錨的同義詞抓不到（等 facet 向量案）」與「錨靠模型翻譯，翻錯就退回無錨」兩條已知限制。
