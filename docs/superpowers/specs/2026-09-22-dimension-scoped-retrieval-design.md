# 分維度檢索與兩段式組裝 — 設計規格

日期：2026-09-22
狀態：已實作並驗收（`scripts/demo.py`、`scripts/pipeline/retrieval.py`，2026-09-22）；§10 改寫稿已併入主規格 §9，待子專案 2 採用
範圍：`scripts/demo.py` 的檢索與組裝流程；同時改寫主規格 [§9 檢索策略](2026-09-21-genai-prompt-copilot-design.md#9-檢索策略)，供子專案 2 的 `SearchPresets` 遵循。

---

## 1. 問題

demo 對「夜晚的湖畔，滿月，湖上很多燈籠，遠處是高山，一個少女坐在橋的扶手上，紫色雙馬尾，綠色眼睛，穿著夾克、熱褲、拖鞋，手中拿著攝影機」印出「命中 8 個片段」，最後只採用 1 個。追查後確認三件事：

**「命中 8 個」是 `LIMIT 8`，不是命中。** SQL 沒有距離門檻，只要庫裡有 8 筆就印 8。

**只採用 1 個是模型的正確拒絕。** 8 筆裡 7 筆與描述矛盾（`standing under streetlamp` vs 坐在扶手上；三筆 `pink hair, purple eyes` vs 紫髮綠眼；`squatting`；`holding a glass`）。prompt 規則要求完整反映使用者描述，模型只能拒絕。

**根因是單一查詢向量。** 整句話（六個維度）壓成一個向量，比對每筆只涵蓋 2–3 個 facet 的片段，向量是六維度的模糊平均，最貼近它的永遠是最泛用的「1girl solo」。全庫 859 筆對這句話的距離分布 `min=0.279, p1=0.318, p50=0.405`；top-8 跨 0.279–0.317，整個 top-8 就是勉強高於雜訊的 top 1%，沒有明顯贏家。而報告說「風格 0/4、鏡頭 0/5」全缺，top-8 裡 Style 與 Camera 卻各 0 筆 —— 使用者沒講的維度，整句向量裡就沒有東西能把它們拉上來。**檢索補不了它剛告訴使用者缺的那兩格。**

### 1.1 實證：GIN 過濾不夠，關鍵是每個維度自己的查詢語句

主規格 §9 已設計「GIN 先過濾候選，向量再排序」，demo 沒實作。實測發現光過濾不夠：

| 候選池 | 排序向量 | Top-1 |
| :--- | :--- | :--- |
| Style（238 筆） | 整句 | `0.354` 科幻女戰士側影 — 垃圾 |
| Style（238 筆） | 子查詢「夢幻唯美的動漫插畫風格，柔和月光色調」 | `0.229` 動漫風格、`0.234` 新海誠動畫風 |
| Camera（78 筆） | 整句 | `0.303` 海邊城市少女特寫 |
| Camera（78 筆） | 子查詢「全身景，仰角，坐在欄杆上的人物構圖」 | `0.234` 側身凝視全身照、`0.254` 半身正面視角 |
| Appearance | 子查詢「一個少女，紫色雙馬尾，綠色眼睛」 | `0.190` 粉紅雙馬尾少女 `short twintails, pink hair, green eyes, blush` |

Appearance 那筆同時給了 `twintails` 與 `green eyes`（皆正確）、只有 `pink hair` 錯，證明「端上桌讓 LLM 挑」可行：模型取兩個對的、丟一個錯的，還撿到 `blush, hair ribbon` 這種自己不會想到的詞。

同時暴露知識庫覆蓋缺口：pose「坐在橋的扶手上，拿攝影機」最近的是 `sitting on the mushroom, holding a laptop`（0.299）；vehicle 的 pose 候選池只有 2 筆；object 的 appearance 池只有 51 筆。這些不在本任務範圍，但畫面必須讓它們看得見（§7）。

分級門檻跨題材驗證：landscape / object / vehicle / portrait 的可用命中都落在 0.18–0.25。

## 2. 目標與非目標

**目標**

- 每個維度用自己的查詢語句、在自己的候選池裡檢索。
- 檢索高召回，LLM 做精度過濾：不替模型預篩，但把相似度分級標給它看。
- 使用者已描述的維度：撈來借 tag、校準寫法。使用者沒描述的維度：撈來給建議，**絕不寫入提示詞**。
- 「採用」從全有全無改成記錄借了哪些 tag，讓 RAG 的實際貢獻可審核。
- 有缺的維度都給建議，每則點名缺的 facet，並給 2–3 個真正不同的方向。
- 把「GIN 過濾不夠、關鍵是分維度子查詢」寫回主規格 §9。

**非目標**

- 不補知識庫的覆蓋缺口（資料管線的事）。
- 不做多輪追問（子專案 2）。
- 不動 C# 端；本設計只改 Python demo 與規格文字。

## 3. 資料流

```text
① analyze   (Gemini #1)   中文描述
                          → subject_profile
                          → 六維度 facet 狀態（covered / missing / notApplicable）
                          → 每個適用維度的檢索子查詢：
                              covered 維度：1 句，用使用者原話
                              missing 維度：最多 2 句，依整體畫面推想、方向要對比

② retrieve  (無 LLM)      一次 embed_batch（整句 + 全部子查詢，≤ 13 段，仍在單次 32 段上限內）
                          每個子查詢各跑一次：GIN 過濾該維度 facet + 向量排序
                          histories：整句向量 top-3，WHERE subject_profile = ①.profile
                          跨維度去重、相似度分級

③ assemble  (Gemini #2)   候選（含分級與 facet 覆蓋標記）+ ① 的 facet 狀態
                          → positive / negative prompt
                          → borrowed: [{preset_id, tags}]
                          → suggestions: 每個有缺 facet 的維度一則
```

三個規則：

- **facet 狀態以 ① 為準，③ 不得更改。** 六維度燈號來自 ①。
- **`grounded` 不問 LLM，Python 從 ① 的 facet 狀態推出**：該維度有任一 facet 為 covered 即 grounded。少一個可被捏造的欄位。
- **API 往返由 2 次變 3 次**（generate → embed → generate）。

## 4. 契約

### 4.1 分析（①）

```python
Dimension = Literal["style", "scene", "camera", "appearance", "pose", "clothing"]

class DimensionQuery(BaseModel):
    dimension: Dimension
    query: str          # 繁中。covered 維度用使用者原話；missing 維度依畫面推想

class AnalysisResult(BaseModel):
    subject_profile: Profile
    facets: list[FacetAssessment]
    queries: list[DimensionQuery]   # 同一 dimension 可出現至多 2 筆（missing 維度的對比方向）
```

Python 端整理 `queries` 的規則（§6.1）。

### 4.2 組裝（③）

```python
class BorrowedFrom(BaseModel):
    preset_id: int
    tags: list[str]              # 從這筆片段借進 positive / negative prompt 的 tag

class SuggestionOption(BaseModel):
    label: str                   # 繁中方向名，如「新海誠風」
    tags: str                    # 可直接貼的英文 tag
    preset_id: int               # 來源片段

class DimensionSuggestion(BaseModel):
    dimension: Dimension
    missing_labels: list[str]    # 該維度缺的 facet 顯示名，逐一點名
    options: list[SuggestionOption]   # 2–3 個方向

class AssemblyResult(BaseModel):
    positive_prompt: str
    negative_prompt: str
    borrowed: list[BorrowedFrom]
    suggestions: list[DimensionSuggestion]
```

### 4.3 內部候選結構

```python
@dataclass
class Candidate:
    preset: dict            # id, title, category, facet_ids, prompt_snippet, negative_snippet
    dimension: Dimension    # 去重後歸屬的維度
    dist: float
    band: Literal["高", "中", "低"]
    grounded: bool          # 歸屬維度是否 grounded
    facet_coverage: dict[str, FacetState]   # 該片段每個 facet_id 對本次使用者的狀態
```

### 4.4 兩段 prompt 的必含規則

實作時兩個 template 各自至少要寫進這些；措辭可調，條目不可少。

**①**

- 判定 `subject_profile`（動物歸 object）。
- 清單裡**每一個** facet 都要判 covered / missing / notApplicable，一個不能漏。
- covered 維度給 1 句子查詢，**用使用者原話**，不改寫、不補充。
- missing 維度給至多 2 句子查詢，依整體畫面推想，兩句方向要**對比**（例：寫實攝影 vs 動漫插畫），不是同一路的兩種說法。

**③**

- 使用者已描述的內容必須**完整**反映在提示詞裡。
- **複合屬性用複合 tag 完整表達**：雙色髮、半邊、漸層、混色這類，要寫成 `split-color hair, two-tone hair, purple hair, pink hair` 這種能讓生圖模型理解結構的組合，不可被候選片段裡的單色詞（`pink hair`）取代或吃掉一半。知識庫沒有的詞由模型自己翻譯，不因為片段裡沒有就省略。
- 標 missing 的 facet **不要自行發明**，留白；基礎畫質詞與基礎負向詞例外（慣例 boilerplate）。
- 候選清單裡每筆片段旁標了 facet 覆蓋狀態（§5.4）：**標 missing 的 facet 對應的詞不可借入提示詞，只可進建議。**
- 相似度「低」的片段仍可借用其中與使用者描述相符的詞（例：`sitting on the mushroom` 裡的 `sitting`），但不可借入與描述矛盾的詞。
- `borrowed` 只列**真的寫進提示詞**且**真的來自該片段**的詞。
- 每個有 missing facet 的維度都要給一則建議，點名該維度所有缺的 facet，給 2–3 個**不同方向**的選項，每個選項附來源片段 id。

## 5. 檢索

### 5.1 SQL

```sql
SELECT id, title, category, facet_ids, prompt_snippet, negative_snippet,
       preset_embedding <=> %(q)s AS dist
FROM prompt_knowledge_presets
WHERE facet_ids && %(facets)s          -- idx_presets_facet_ids (GIN)
ORDER BY dist
LIMIT %(k)s
```

- `%(facets)s` = `catalog.profiles[profile][dimension]`，自動吃到 profile 限制（landscape 沒有 clothing / appearance / pose 就不會撈）。
- `k`：covered 維度的單一查詢 5；missing 維度每句 3（兩句共 6）。上限約 6 維 × 6 = 36 筆，去重後通常更少。
- **不設距離門檻。** 分級：`dist < 0.25` 高、`0.25 ≤ dist < 0.30` 中、`≥ 0.30` 低。
- **不用 `tags && $2`。** 主規格 §9 有它，但 OR 上 tags 會把候選池撐到維度外，與分維度前提衝突。決定不做。

### 5.2 histories

```sql
SELECT user_intent, positive_prompt, subject_profile, intent_embedding <=> %(q)s AS dist
FROM shared_prompt_histories
WHERE subject_profile = %(profile)s
ORDER BY dist LIMIT %(k)s
```

用整句向量（它比對的是整段紀錄）。profile 過濾是主規格 §9 原本就有的，之前 demo 做不到是因為 profile 到最後才知道。

### 5.3 跨維度去重

一筆 preset 的 `facet_ids` 可能橫跨數維（Combined 類），會在多個候選池出現（實測 859 筆裡有 134 筆、15.6% 橫跨 2 維以上）。只留一次，歸屬規則是 **grounded 優先、距離次之**：

1. 若有任何 grounded 維度撈到它，歸給這些維度中距離最小的那個。
2. 完全沒有 grounded 維度撈到，才退回全體最小距離。

`grounded` 取歸屬維度的值。

**為什麼不是單純的最小距離。** 歸屬決定借用資格——§6.2 的驗證讀的就是去重後的 `grounded`，False 即整筆降級成「僅供建議」。而 `grounded` 不是片段的屬性，是「這個維度使用者講了沒」的屬性。單純比距離的話，一個 grounded 維度正當撈到的片段，會因為某個 missing 維度**推想出來**的查詢剛好更近，就失去借用資格。同為 grounded 時才比距離——每維用不同子查詢向量，「哪一維最小」就是「這筆最像哪一維的需求」，那時比距離有意義。

（初版實作是單純的最小距離，2026-09-22 修正為本節規則，見 `test_dedupe_prefers_a_grounded_dimension_even_when_an_ungrounded_one_is_closer`。）

### 5.4 facet 覆蓋標記

去重後，對每筆候選的每個 `facet_id`，查 ① 的狀態填入 `facet_coverage`。這是給 ③ 看的：候選清單裡每筆片段旁標明「這筆涉及的 facet 中，哪些對本次使用者是 covered、哪些是 missing」。

## 6. 驗證（Python 端，不信任 LLM 自述）

### 6.1 整理 ① 的 queries

- `dimension` 不在 `catalog.profiles[profile]` 裡 → 丟。
- 每個維度的 query 數量有上限，超過的只留前面的：grounded 維度 1 句（用使用者原話，k=5）、missing 維度 2 句（對比方向，各 k=3）。LLM 多給的一律丟。§3 的「單次 embed_batch ≤ 13 段」與 §5.1 的候選上限都建立在這個上限上。
- grounded 維度若沒有任何 query → 用整句描述當該維度的 query 頂上。一次漏答不能毀掉整個維度。
- 全部 missing 維度都沒有 query → 允許（該維度就沒有建議），畫面照實顯示。

### 6.2 驗證 ③ 的 borrowed

**驗證只修改 `borrowed` 這份歸屬紀錄，絕不動 `positive_prompt` / `negative_prompt`。** 提示詞是 ③ 的產出，使用者描述的內容（如「紫色雙馬尾」→ `purple hair`）本來就該在裡面；驗證問的只是「LLM 說這個詞是從某片段借的，是真的嗎」。

逐筆檢查，不合的**從 `borrowed` 移除並在畫面標示**（現行 `demo.py` 是靜默丟）：

- `preset_id` 不在本次候選集 → 整筆移除。
- 候選的 `grounded == False` → 整筆 `BorrowedFrom` 移除。程式保證的只是**這筆歸屬會被拿掉並顯示給使用者看**，不是「這個詞沒寫進提示詞」——詞是否真的沒進 `positive_prompt`，是 §4.4 ③ 規則 4（標 missing 的 facet 對應詞不可借入提示詞）這條 prompt 約束在管。兩者合起來才構成「不自行發明」的保障。
- `tags` 裡每個詞必須大小寫無關地同時出現在**兩邊**：該片段的 `prompt_snippet ∪ negative_snippet`（真的來自這裡）**且** ③ 產出的 `positive_prompt ∪ negative_prompt`（真的用了）。任一邊沒有 → 該詞移除。例：LLM 宣稱從 id 483〈`pink hair, purple eyes`〉借了 `purple hair` → 483 沒有這個詞，歸屬是假的，移除；`purple hair` 本身留在提示詞裡不受影響。

這驗的是**來源**，不是**正確性**。LLM 若真的從 483 借了 `pink hair` 進提示詞，子串檢查會過 —— 那跟使用者的「紫色」矛盾，是 ③ prompt 裡「使用者已提供的內容必須完整反映」那條規則要管的，驗證不保證。子串比對也防捏造不防偷懶（`hair` 會命中 `long hair`），已知且接受。

### 6.3 驗證 ③ 的 suggestions

- `preset_id` 不在本次候選集 → 該 option 丟。
- 來源**不受** grounded 限制：scene 是 grounded 但 `scene.weather` 缺，scene 片段裡的 `mist` 可以進「場景」建議。閘門只管提示詞。
- `dimension` 在 ① 沒有任何 missing facet → 整則丟（不該有建議）。

### 6.4 粒度誠實聲明

`grounded` 閘門是**維度級**硬保證。「不發明」的規則是 **facet 級**，硬閘門做不到（分不出 snippet 裡哪個詞屬於哪個 facet）。facet 級靠 §5.4 的覆蓋標記 + ③ 的 prompt 明文約束：「標 missing 的 facet 對應的詞不可借入 prompt，只可進建議」。這是 prompt 約束而非程式保證，寫在這裡讓後人知道邊界在哪。

## 7. 輸出畫面

```text
[1/3] 分析：夜晚的湖畔，…
      題材 portrait；子查詢：scene「夜晚的湖畔，滿月…」 appearance「一個少女，紫色雙馬尾…」 …
                     推想：style「夢幻唯美的動漫插畫風」/「寫實夜景攝影」 camera「…」/「…」
[2/3] 檢索：scene 池 292 → 5（高 0 中 5）  appearance 池 200 → 5（高 5）  pose 池 108 → 5（低 5）
           style 池 238 → 6（高 4 中 2）    camera 池 78 → 6（高 6）        clothing 池 87 → 5（中 5）
           相似作品 3（portrait）
[3/3] 組裝…

━━ 題材判定 ━━ / ━━ 六維度充足度 ━━                    （不變）

━━ 正向提示詞 ━━ / ━━ 負向提示詞 ━━                    （不變）

━━ 借用的知識庫片段 ━━
  [高 0.190] 粉紅雙馬尾少女（Appearance）→ 借入 twintails, green eyes
  [中 0.252] 夜空與滿月（Scene）          → 借入 full moon, night sky
  ✗ 來源不符：id 483〈粉髮紫瞳少女〉沒有 "purple hair"，不計入借用（提示詞不受影響）

━━ 建議 ━━
  風格（缺：藝術流派／媒材、參照畫師或作品、渲染引擎／技術風格詞、色調傾向）
    A. 新海誠風        Makoto Shinkai Style, Anime Illustration, Soft Realism   〈新海誠動畫風〉
    B. 寫實夜景攝影    photo realism, night photography                          〈寫實攝影〉
    C. 平滑漸層插畫    flat colors, gradient                                     〈平滑漸層插畫風〉
  鏡頭（缺：景別、視角高度、焦段、景深、構圖）
    A. …
  場景（缺：前景元素、天氣氛圍、季節）
    A. 薄霧           mist                                                       〈夜空與滿月〉
  …
```

- `[2/3]` 那行讓知識庫缺口直接暴露（vehicle pose 池 2 筆會印出來）。
- `--verbose`：在 `[2/3]` 後把每維度全部候選連分級與 facet 覆蓋標記印出。除錯用，也是展示 RAG 運作的最直接方式。
- 被移除的 borrowed 歸屬一定顯示，不靜默；措辭必須說明是「來源不符」且提示詞不受影響，不能寫成像是詞被拿掉了。

## 8. 程式結構

| 檔案 | 職責 |
| :--- | :--- |
| `scripts/pipeline/retrieval.py`（新） | `dimension_facets`、`band`、`dedupe`、`annotate_coverage`、`retrieve_presets`、`retrieve_histories`。子專案 2 的 C# `SearchPresets` 照這個形狀寫 |
| `scripts/demo_render.py`（新，從 `demo.py` 搬出） | `display_width`、`pad`、`wrap_tags`、`render_dimension_row`、`render`。純函式，現有 9 個測試跟著搬 |
| `scripts/demo.py` | 兩段 schema、兩個 prompt template、§6 驗證、編排、CLI |

`retrieval.py` 中純函式（`dimension_facets`、`band`、`dedupe`、`annotate_coverage`、queries 整理）與吃 `conn` 的函式分開，前者可無 DB 測試。

## 9. 測試

沿用既有 pattern：純函式直測、Gemini 用 `FakeModels` 注入、DB 用 `@pytest.mark.integration` + `db_available()` 跳過。

**單元（無 DB、無網路）**

- `dimension_facets`：landscape 不回 clothing / appearance / pose；portrait 回六維；vehicle 的 pose 只有 `motion_state, terrain`。
- `band`：0.249 → 高、0.25 → 中、0.299 → 中、0.30 → 低。
- `dedupe`：同一 preset 在兩個維度命中，只留距離小的那個，`grounded` 取該維度的值。
- queries 整理（§6.1）四條各一個測試。
- `validate_borrowed`（§6.2）三條各一個測試；tag 子串比對大小寫無關；可從 `negative_snippet` 借；宣稱借了但提示詞裡沒有的詞被移除；**驗證前後 `positive_prompt` / `negative_prompt` 完全相同**（驗證不動提示詞）。
- `validate_suggestions`（§6.3）三條。
- render：每個有 missing facet 的維度都出現一則建議且點名 facet；剔除的 borrowed 有顯示；`[2/3]` 行含池大小與分級計數。

**整合（需 DB）**

- 分維度檢索只回含該維度 facet 的片段。
- 同一組子查詢在 portrait 與 landscape 下候選池不同（landscape 沒有 clothing 結果）。

**端到端（FakeModels）**

- 兩段依序呼叫；embed 只呼叫一次且包含整句與全部子查詢；① 的 facet 狀態原封到輸出。

## 10. 主規格 §9 改寫稿

以下取代 [2026-09-21-genai-prompt-copilot-design.md](2026-09-21-genai-prompt-copilot-design.md) 的 §9 全文：

> ## 9. 檢索策略
>
> | Tool | 策略 | SQL 概念 |
> | :--- | :--- | :--- |
> | `SearchPresets` | **分維度**：每次呼叫鎖定一個維度，用該維度專屬的查詢語句，GIN 過濾該維度 facet 後向量排序 | `WHERE facet_ids && $facetIdsOfDimension ORDER BY preset_embedding <=> $dimensionQueryVec LIMIT k` |
> | `SearchSimilarPrompts` | 向量 Top-K + profile 過濾 | `WHERE subject_profile = $1 ORDER BY intent_embedding <=> $2 LIMIT k` |
>
> **GIN 過濾本身不夠。** 實測（2026-09-22）：同樣過濾到 Style 候選池，用使用者整句描述的向量排序撈回無關片段（dist 0.354），用該維度專屬的查詢語句撈回正確風格（0.229–0.234）。單一整句向量是六維度的模糊平均，只會貼近最泛用的片段，且使用者沒提到的維度永遠撈不到。因此 agent 呼叫 `SearchPresets` 時：
>
> - 一次呼叫一個維度，`facetIds` 給該維度在當前 profile 下的 facet 集合。
> - `query` 是該維度的專屬語句：使用者已描述的維度用其原話；未描述的維度由 agent 依整體畫面推想，且應給對比方向（例：寫實攝影 vs 動漫插畫）分兩次呼叫。
> - 使用者未描述的維度撈到的片段只可用於追問與建議，不得直接寫入提示詞。
> - 不設距離門檻；tool result 帶相似度分級（`<0.25` 高、`<0.30` 中、其餘低），由 agent 判斷。
> - `tags && $2` 決定不做：OR 上 tags 會把候選池撐到維度外，與分維度前提衝突。
>
> 查詢向量於 runtime 以同一 embedding 模型計算；多個維度的查詢語句合併為單次 `embed_batch` 呼叫。
>
> 完整推導與 Python 參考實作見 [2026-09-22-dimension-scoped-retrieval-design.md](2026-09-22-dimension-scoped-retrieval-design.md)。

同時 §4 的工具表中 `SearchPresets` 一列的說明由「混合檢索」改為「分維度檢索，見 §9」；`tags: string[]` 參數移除。

## 11. 已知限制與後續

- **facet 級的「不發明」只有 prompt 約束**（§6.4）。要做到硬保證需要片段內 tag 到 facet 的對應，那是資料管線的結構化階段要多產出的欄位，另案。
- **知識庫覆蓋缺口**：vehicle pose 池 2 筆、object appearance 池 51 筆、「坐欄杆＋拿相機」類 pose 無命中。畫面會暴露，補資料另案。
- **分級門檻是絕對值**，跨四種 profile 驗過但只有數個題目。若之後發現漂移，改成池內相對分位。
- **missing 維度的推想子查詢已隱含創作決定**（推想「動漫」就只撈動漫）。兩句對比方向是緩解不是解決；子專案 2 的追問流程才是正解。
- **本檔的去重是「一次看得到全部維度」的模型**，子專案 2 的 `SearchPresets` 逐維度各自呼叫，看不到全局。對應作法見主規格 §9：不在檢索時去重，改由 session ledger 累積 `presetId → [(dimension, dist, grounded)]`，到定稿驗證時才套 §5.3 的歸屬規則。
