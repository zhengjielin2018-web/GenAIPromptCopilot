# 資料庫擴增計畫

狀態：已定案（設計階段；尚未實作）。

## 1. 動機

子專案 1 完成時的正式語料是 `python seed_data.py --max-items 400` 跑出來的：420 raw
→ 258 clean → 258 histories + 859 presets。RAG 要展示出價值，前提是候選池要夠大、
夠多樣；目前規模小到某些 (profile, 維度) 組合的候選池只有個位數，向量檢索排不出有意
義的排序。

單純把 `--max-items` 調大**不能解決多樣性問題**，因為 `civitai_client.py` 目前用固定的
`sort=Most Reactions&period=AllTime`、不限 `baseModels`，撈到的永遠是同一批「全站最熱門」
的內容——實測 78% 的內容命中人物或人物向片段，載具只有約 4%。放大 20 倍只會等比例放
大同一個偏斜，vehicle 相關的候選池依舊接近空。

本計畫的範圍**只涵蓋 fetch 階段**（分層抓取，擴大來源多樣性）。`clean`／`structure`／
`embed`／`load` 四個階段完全不動——付費（Gemini 結構化）的那段管線一行都不改，把改動
與風險集中在免費的 fetch 階段。

## 2. 規模與成本

目標：clean 產出規模落在 **5,000 筆以上**。

換算依據（全部為子專案 1 正式語料的實測數字）：

| 量 | 數值 | 來源 |
| :--- | :--- | :--- |
| clean 存活率（對全部 raw） | 61.4% | 258 / 420 |
| baseline 分層 meta 命中率 | 78%（本次重新探測） | 見 §4 探測記錄 |
| clean 存活率（對「有 meta」的 raw） | 約 79% | 61.4% / 78% |
| 每筆 clean 產出的 preset 數 | 約 3.33 | 859 / 258 |
| structure 呼叫次數 | 1 次／筆 clean | `structure.py` 逐筆呼叫 |
| embed 呼叫次數 | 32 筆一批 | `gemini_client.py::BATCH_SIZE` |

成本結構：**structure 階段是唯一的付費瓶頸**（一次一筆 Gemini 呼叫）；embed 階段已批次
化，17,000+ presets、5,000+ histories 合計僅約 700 次呼叫；fetch 完全免費，9,000 筆配額
只需約 45 次 API 請求（見 §4），節流下約 1 分鐘量級，不是限制因素。

**額度策略（使用者已定案）**：開付費額度，一次跑完 5,000+ 次結構化呼叫，不分批分日、
不改 `structure.py` 的重試邏輯（現有 `retry()` 對 429/5xx 重試 6 次即拋例外，這次擴增
不動它）。

## 3. 對「補冷門 facet」的取捨（已定案）

**只用真實語料，缺就承認**——不用 LLM 合成 preset、不做人工 curated 小清單去湊。這代表
某些 Civitai 結構性稀少的 facet（見 §7 預期結果表）擴增後仍然薄，本計畫不假裝解決它。

## 4. 探測記錄（唯讀公開 API，本次設計會議做的驗證）

以下為對 `GET /api/v1/images`、`GET /api/v1/models` 的唯讀探測，用來決定哪些過濾軸
真的有效。**這些發現要寫進 §8 明確不做清單**，避免未來重複嘗試已驗證無效的路徑。

### 4.1 有效的分層軸

`baseModels` 與 `period` 兩個查詢參數確實會改變回傳內容（非裝飾性參數）：

- `sort=Most Reactions&period=AllTime` 不限 baseModel（下稱 baseline）與
  `baseModels=Flux.1 D`（同排序週期）100 筆抽樣重疊僅 6/20（30%）
- baseline 與 `sort=Newest&period=Week` 重疊 0/20
- 同一 `baseModels`、`AllTime` vs `Year` 兩個週期重疊率：SD 1.5 2%（8/400）、
  SDXL 1.0 4%（16/400）、NoobAI 8%（33/400）——都夠低，兩個週期可視為互補的獨立配額，
  不是同一批語料算兩次

### 4.2 各分層的 meta 命中率與題材傾向（每層 100 筆抽樣，關鍵字啟發式，會高估但層間落差可信）

| 分層 | meta 率 | 人物 | 風景 | 載具 | 物件 |
| :--- | ---: | ---: | ---: | ---: | ---: |
| baseline（現行參數） | 78% | 60% | 33% | 4% | 31% |
| SD 1.5 / Year | 92% | 10% | **75%** | 1% | 2% |
| SDXL 1.0 / AllTime | 83% | 33% | 37% | **6%** | 31% |
| SDXL 1.0 / Year | 69% | 14% | 43% | 4% | 23% |
| SD 1.5 / AllTime | 91% | 48% | 25% | 1% | 26% |
| NoobAI / Year | 93% | 86% | 34% | 6% | **42%** |
| NoobAI / AllTime | 92% | 87% | 30% | 4% | 30% |
| Illustrious / AllTime | 88% | 81% | 38% | 1% | 39% |
| Pony / AllTime | 83% | 77% | 19% | 2% | 33% |
| SD 3.5 / AllTime | 56% | 39% | 18% | 2% | 9% |
| SD 3.5 / Year | — | — | — | — | — | 只有 6 筆，深度不足 |
| Flux.1 D / AllTime | 64% | 39% | 39% | 5% | 25% |

Flux.1 D 及其他自然語言方言分層（Krea、Qwen、OpenAI）樣本 prompt 為長句自然語言描述
（例：`"A close-up of a face adorned with intricate black and blue patterns..."`），與
spec `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md` §15 定調的
SD/SDXL tag 方言不同，**已定案排除**，不列入分層。

### 4.3 已驗證無效、放棄的槓桿

- **`modelId` 針對性抓取**：本來設想用 `GET /api/v1/models?query=car` 找目標模型、
  再以 `modelId` 反查圖片，精準補 vehicle。實測三個不同 modelId（25399「CarDos
  Anime」、632048「Flux-XL Scenery」、105328「Carrying person」）各抓 50–100 筆，
  **回傳圖片 100% 沒有 `meta.prompt`**。沒有 prompt 的圖片會在 clean 階段被
  `_to_record()` 的 `if not prompt: return None` 整批丟棄，這條路完全沒有價值。
- **`tags` 查詢參數**：`GET /api/v1/images?tags=vehicle` 回傳 **400 Bad Request**，
  該參數在此端點不受支援（或拼寫/格式與文件不符，總之目前不可用）。

## 5. 元件與邊界

| 檔案 | 動作 | 職責 |
| :--- | :--- | :--- |
| `scripts/pipeline/strata.py` | **新增** | `Stratum` 資料類別、9 層配額表常數。純資料，無 I/O |
| `scripts/pipeline/civitai_client.py` | 修改 | `iter_images()` 的 `sort`/`period` 從寫死值改為參數（`base_models` 已是既有參數，不用改） |
| `scripts/pipeline/fetch_civitai.py` | 修改 | `run_fetch()` 已接受並透傳 `base_models`，比照新增 `sort`/`period` 形參；`main()` 改為依序對每層呼叫；state schema 升級為多層 |
| `scripts/coverage_report.py` | **新增** | 覆蓋度報告，量測 (profile, 維度) 候選池與 facet 筆數，可當驗收 gate |
| `scripts/README.md` | 修改 | 「續跑與規模」段落改寫；補分層抓取用法 |
| `docs/單輪流程說明.md` | 修改 | 池子數字（238/292/78/161/106/87 等）用擴增後的真實執行重新產生 |
| `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md` §15 | 修改 | 補一筆「modelId／tags 針對性抓取已實測不可行」的決定紀錄 |

`db/init/001_schema.sql`、`pipeline/clean.py`、`pipeline/structure.py`、
`pipeline/embed.py`、`pipeline/load.py`、`pipeline/retrieval.py` **不動**。

## 6. Stratum 定義與配額表

```python
@dataclass(frozen=True)
class Stratum:
    key: str                      # state.json 的鍵；一旦寫入 state 就不可更名（改名=整層重抓）
    base_models: list[str] | None # None = 不過濾（僅 baseline 使用）
    period: str                   # "AllTime" | "Year" | "Month" | "Week" | ...
    quota: int                    # 這一層目標新增幾筆 raw
    sort: str = "Most Reactions"
```

| key | base_models | period | quota (raw) | 依據（§4.2） |
| :--- | :--- | :--- | ---: | :--- |
| `baseline` | 無 | AllTime | 600 | 續用現有 cursor（已有 420/600），與既有語料同分布 |
| `sd15/year` | SD 1.5 | Year | 1,600 | 風景主力（景 75%） |
| `sdxl10/alltime` | SDXL 1.0 | AllTime | 1,600 | 最均衡、載具最高（車 6%） |
| `sdxl10/year` | SDXL 1.0 | Year | 1,200 | 風景次要 + 載具 4% |
| `noobai/year` | NoobAI | Year | 1,200 | 物件 42% + 載具 6% |
| `noobai/alltime` | NoobAI | AllTime | 800 | 物件 |
| `sd15/alltime` | SD 1.5 | AllTime | 800 | 通用 |
| `illustrious/alltime` | Illustrious | AllTime | 700 | 人物 + 物件 |
| `pony/alltime` | Pony | AllTime | 500 | 人物（已飽和，刻意少給） |
| **合計** | | | **9,000** | |

配額表寫成模組層級常數（非設定檔），因為它是這次擴增一次性的決定，不是長期可調參數；
若要重新配置，改程式碼、走 code review，比隱藏在設定檔裡更誠實。`fetch_civitai.py` 提供
`--quota-scale FLOAT`（乘上每層 quota，四捨五入），用於 §9 的分階段驗收先跑小規模。

估算：9,000 raw × 平均 meta 命中率約 85% × clean 存活率約 79% ≈ 6,000，扣掉跨層重複
（不同分層抓到同一張熱門圖，baseline 與其他分層有限重疊）估計 10–15% → **落在
5,200–5,500 clean**，對應約 17,300 presets、約 5,200 次結構化呼叫。

## 7. 抓取順序：逐層循序（非交錯）

**依序**對 9 個 stratum 呼叫既有的 `run_fetch()`，每層跑到「quota 滿足」或「cursor 耗盡」
為止，才換下一層。這是重新檢視後從「逐頁交錯輪流」簡化來的（見下方「設計取捨」）。

`run_fetch()` 目前已經是 stratum 無關的：它接受一個共用 client、`max_items`、`out_path`、
`state_path`、`base_models`（既有參數，已透傳給 `iter_images()`），用
`existing_keys(out_path, "id")` 對**同一個** `RAW_PATH` 去重。這代表：

- 不需要新寫跨層去重邏輯——任何一層抓到別層已寫入的圖片 id，會被既有的 `seen` 集合
  自動跳過，不計入該層配額，也不會產生重複記錄
- 唯一要做的改動：`iter_images()` 裡寫死在 `params` dict 的 `sort="Most Reactions"`、
  `period="AllTime"` 兩個值比照 `base_models` 現有的做法改成參數，`run_fetch()` 新增
  對應形參透傳；`fetch_civitai.py::main()` 改成
  `for stratum in STRATA: run_fetch(client, max_items=stratum.quota, base_models=stratum.base_models, sort=stratum.sort, period=stratum.period, ...)`，
  同一個 client 實例（共用同一個 `RateLimiter`）在 9 層之間重用

**設計取捨（放棄逐頁交錯輪流的理由）**：原設計是為了避免「中途中斷造成語料偏斜」而讓
9 層逐頁交錯抓取，代價是要把 `iter_images()`／`run_fetch()` 的生成器介面重寫成可交錯
排程、且要在每一頁後都存 state。重新估算後放棄這個設計：9,000 筆配額只需約 45 次請求
（200 筆／頁），節流下抓取本身是分鐘量級操作，不是「全量會花時間」指的那個規模（那指的
是後續 5,200 次 LLM 結構化呼叫）。分鐘量級的操作中途被打斷、且打斷點剛好落在某層完全
沒抓到的機率低；就算發生，重跑同一指令也只是那一層從頭開始，不會造成永久性的語料偏斜。
逐層循序換來的是：改動範圍更小、重用已測試過的既有函式、不需要新的交錯排程邏輯。

## 8. state.json 遷移

```json
{
  "version": 2,
  "strata": {
    "baseline":  {"cursor": "400|1753734600000", "fetched": 420, "done": false},
    "sd15/year": {"cursor": null, "fetched": 0, "done": false}
  }
}
```

沒有 `version` 欄位的舊檔視為 v1，整個物件即是 `baseline` 這一層的狀態，讀入時原地
包成 `{"version": 2, "strata": {"baseline": <舊物件>}}` 並寫回。`baseline` stratum 的
參數（無 `base_models` 過濾、`period=AllTime`、`sort=Most Reactions`）刻意定義成與現行
寫死參數完全一致，所以**既有的 420 筆與其 cursor 可以無縫接續，不會重抓**。遷移邏輯是
純函式，單元測試覆蓋（v1 檔案 → 正確包裝、v2 檔案 → 原樣讀回、遺失欄位時的預設值）。

## 9. 分階段驗收（避免一次燒掉 5,200 次呼叫才發現配額表設錯）

不需要新程式，用既有的 `--from`／`--max-records`：

1. `python -m pipeline.fetch_civitai`（全部 9 層跑完，免費，分鐘量級）→
   `python -m pipeline.clean`（免費）
2. `python seed_data.py --from structure --max-records 500` → `embed` → `load` →
   `python coverage_report.py`
3. 檢查 profile 分布、各 (profile, 維度) 候選池是否接近 §2／§6 的估算。偏離太多就回頭
   調整 §6 配額表——`--quota-scale` 是全域的九層等比縮放，沒有單獨補某一層的旋鈕，要
   補特定分層得直接改 `pipeline/strata.py::STRATA` 裡那一層的 `quota`（見 §6）
4. 確認無誤後跑全量 `python seed_data.py --from structure`（不限 `--max-records`）

用真實 LLM 分類的 500 筆抽樣外推，比任何關鍵字啟發式準，成本僅約 500 次結構化呼叫
（幾分錢量級），比跑錯全量再重來便宜得多。

## 10. `coverage_report.py`

直接 import `pipeline.retrieval.POOL_SQL` 與 `pipeline.facets.FacetCatalog` 計算候選池
大小，**不另外寫一份查詢邏輯**——這樣報告量到的池子大小，定義上就等於 `retrieval.py`
運作時實際看到的池子，兩者不會各自漂移出不一致的結果。

輸出四塊：

1. 每 profile 的 `shared_prompt_histories` 筆數
2. 每 (profile, 維度) 的 preset 候選池大小（等同 `retrieval.py::POOL_SQL` 的查詢條件）
3. 每個 facet id 在 `prompt_knowledge_presets.facet_ids` 裡出現的筆數
4. `prompt_knowledge_presets.category` 分布

門檻：候選池 ≥ 60（`retrieval.py::K_COVERED = 5`，池子要有明顯大於 K 的挑選空間）、
單一 facet ≥ 10。任何一格未達標就以非零 exit code 結束，可直接接進 CI 或手動驗收流程
當 gate 用。這支腳本同時取代 README「查看資料庫內容」段落裡目前要手動貼 SQL 的做法，
但不刪除那些 SQL 範例（唯讀查詢，保留給不想跑腳本的人）。

## 11. 測試

- `test_strata.py`（新增）：配額表 key 唯一、`--quota-scale` 縮放的四捨五入行為
- `test_fetch.py`（擴充）：state v1→v2 遷移、假 client 下依序處理多層（某層 quota
  滿足即停、某層 cursor 耗盡即標記 done 並換下一層）、`existing_keys` 跨層去重不重寫
  已通過的既有測試路徑
- `test_civitai_client.py`（擴充）：`iter_images()` 傳入自訂 `sort`/`period`/
  `base_models` 時，實際送出的 HTTP 查詢參數正確
- `test_coverage_report.py`（新增）：門檻判定與格式化走純函式測試（不需 DB）；SQL 查詢
  本體標記 `integration`，需要 DB 已啟動

## 12. 明確不做（決定不做，不是忘了做）

- **`modelId` 針對性抓取**——實測回傳圖片 100% 無 `meta.prompt`（§4.3）
- **`tags` 查詢參數**——回 400 Bad Request（§4.3）
- Flux.1 D／Krea／Qwen／OpenAI 等自然語言方言分層——與既定的 SD/SDXL tag 方言不一致
  （§4.2、§3 使用者定案）
- SD 3.5——meta 命中率僅 56%，`Year` 週期深度不足（僅 6 筆樣本）
- LLM 合成 preset、人工 curated 補冷門 facet 清單——使用者已定案「只用真實語料，缺就
  承認」（§3）
- 逐頁交錯抓取排程——重新評估後改為逐層循序，見 §7「設計取捨」
- 動 embedding 模型、向量維度、`db/init/001_schema.sql`
- 改動 `clean`／`structure`／`embed`／`load` 四個階段的任何邏輯

## 13. 預期結果

| 指標 | 現在 | 預期 |
| :--- | ---: | ---: |
| presets | 859 | 約 17,300 |
| histories | 258 | 約 5,300 |
| vehicle histories | 6 | 約 150–200 |
| vehicle / pose 候選池 | 2 | 約 30–50 |
| camera.focal 候選池 | 2 | 約 40 |
| pose.terrain 候選池 | 0 | 可能仍是個位數 |

**誠實聲明**：`pose.terrain`（「濺起水花」「揚起塵土」等與地形互動的動作描述）在 Civitai
語料裡是結構性缺口——沒有任何分層測出明顯的命中率，20 倍擴增後很可能仍接近空。
`vehicle / pose` 候選池即使擴增後（約 30–50）仍低於 §10 訂的 60 門檻，是全表最薄的一格，
本計畫的分層抓取策略無法把它拉到門檻之上；若之後要真正解決，需要 §3 已否決的合成或
curated 路徑之一，或接受它是這批 Civitai 語料的已知限制。
