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
