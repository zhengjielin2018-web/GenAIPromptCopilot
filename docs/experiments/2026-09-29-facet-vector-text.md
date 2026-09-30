# 實驗：facet 向量該用什麼文字算

日期：2026-09-29
目的：facet 向量子表案（`preset_facet_embeddings`）動工前，先決定片段端與查詢端各用什麼文字，並留一組改前改後都能重跑的基準。
背景：`docs/known-issues.md` §5 的 R2 觀察——「涼鞋」查 `clothing.footwear`，前 5 名只有 2 筆是 sandals，知識庫有 18 筆。

## 結論

- **片段端用英文 tag，查詢端要帶英文**（中文原話＋模型給的英文 tag）。前 5 名命中數 67 → 81（滿分 85）；只帶中文原話是 73，改善有限。
- **一定要去重**：很多片段在某個 facet 的 tag 完全相同（鞋履只寫 `sandals` 的就有好幾筆），facet 向量一模一樣。不去重時前 5 名常是同一組 tag 重複 5 次，多樣性從 76 掉到 40；相同 facet 文字只留最近一筆後，命中 76、多樣性 85（滿分）。
- **不要在片段端加中文 facet 名稱**：同一個 facet 底下每筆前綴都一樣，拉不開差距，還會把排序帶歪（「俯視」查視角高度，前 5 名全是 `from below`，命中 0）。
- **採用 V4d**（2026-09-29 定案）：片段端英文 tag、查詢端「中文原話（英文 tag）」、相同 facet 文字去重。V3d（查詢只用英文）分數相同；V4d 多帶中文，模型翻譯偏掉時還有原話撐著。
- **分級門檻要重校**：V4d 的正解距離中位數 0.221、非正解 0.278，現行 0.25／0.30 是整套向量校的（正解中位數 0.259）。

## 方法

**查詢集**（17 個 facet 查詢，每個配一條正解規則，對該片段在這個 facet 底下的 `facet_tags` 做正規化後比對）：

| facet | 中文原話 | 正解（regex，小寫、底線換空白後比對） | 候選池 | 有此 facet tag | 正解筆數 |
| :--- | :--- | :--- | ---: | ---: | ---: |
| clothing.footwear | 涼鞋 | `\bsandals?\b` | 396 | 338 | 18 |
| clothing.footwear | 運動鞋 | `\bsneakers?\b` | 396 | 338 | 33 |
| clothing.upper | 連帽外套 | `\bhoodie` | 2402 | 2347 | 67 |
| clothing.upper | 和服 | `\bkimono` | 2402 | 2347 | 102 |
| clothing.lower | 百褶裙 | `\bpleated\b` | 1038 | 898 | 103 |
| clothing.lower | 牛仔短褲 | `\b(denim\|jean) shorts\b` | 1038 | 898 | 10 |
| clothing.head | 草帽 | `\bstraw hat\b` | 700 | 662 | 11 |
| clothing.accessories | 眼鏡 | `(?<!sun)glasses\b\|\beyewear\b` | 1242 | 1062 | 5 |
| appearance.hair | 雙馬尾 | `\btwin ?tails?\b` | 1807 | 1680 | 119 |
| appearance.hair | 銀髮 | `\b(silver\|white\|grey\|gray) hair\b` | 1807 | 1680 | 254 |
| appearance.expression | 微笑 | `\bsmil` | 971 | 885 | 344 |
| scene.location | 海邊 | `\b(beach\|seaside\|shore\|ocean\|sea)\b` | 3968 | 3671 | 166 |
| scene.weather | 下雪 | `\bsnow` | 1435 | 1090 | 127 |
| scene.lighting | 夕陽 | `\b(sunset\|golden hour\|dusk)\b` | 3167 | 2776 | 213 |
| style.genre | 水彩 | `\bwatercolou?r` | 2818 | 2565 | 80 |
| camera.angle | 俯視 | `\b(from above\|high[- ]angle\|bird'?s[- ]eye\|overhead\|top[- ]down\|above)\b` | 739 | 671 | 169 |
| pose.main | 坐著 | `\b(sitting\|seated)\b` | 1790 | 1492 | 309 |

候選池＝`facet_ids && [facet]`（與 `SearchPresets` 現行條件相同）；facet 向量只能算有這個 facet tag 的列。

**比較的組合**（embedding 一律 `gemini-embedding-001`、768 維，片段端 `RETRIEVAL_DOCUMENT`、查詢端 `RETRIEVAL_QUERY`，全部精確排序）：

| 代號 | 片段端 | 查詢端 |
| :--- | :--- | :--- |
| V0（現行） | 整套 `preset_embedding`（`title。description。標籤：全部 tag`） | 中文原話 |
| V1 | 該 facet 的英文 tag，逗號串接 | 中文原話 |
| V2 | `中文 facet 名稱：英文 tag` | 中文原話 |
| V3 | 同 V1 | 英文 tag |
| V4 | 同 V1 | `中文原話（英文 tag）` |
| V1d／V3d／V4d | 同 V1／V3／V4，排序後相同 facet 文字只留最近一筆 | 同左 |

英文 tag 模擬 `SetFacetStates` 的 `tags`：用 `gemini-3.5-flash-lite`、temperature 0，提示「使用者描述圖片的「{facet 名稱}」是：「{原話}」。請給對應的 Stable Diffusion（danbooru 風格）英文 tag，1 到 3 個…」。

**指標**：前 5 名的正解筆數（p5，17 題滿分 85）、前 10 名正解筆數（p10）、前 5 名裡相異 facet 文字的組數（div5，滿分 85；看模型能不能從 5 筆裡借到不同的詞）。

## 結果

每格是 p5／div5：

| 查詢 | 英文 tag | V0 | V1 | V2 | V3 | V4 | V1d | V3d | V4d |
| :--- | :--- | :-: | :-: | :-: | :-: | :-: | :-: | :-: | :-: |
| 涼鞋 | sandals | 2/5 | 5/1 | 5/1 | 5/1 | 5/1 | 4/5 | 4/5 | 5/5 |
| 運動鞋 | sneakers | 4/5 | 5/1 | 5/1 | 5/1 | 5/1 | 4/5 | 5/5 | 5/5 |
| 連帽外套 | hoodie | 4/5 | 5/3 | 4/3 | 5/2 | 5/3 | 3/5 | 5/5 | 5/5 |
| 和服 | kimono | 5/5 | 3/2 | 3/2 | 5/3 | 5/2 | 4/5 | 5/5 | 4/5 |
| 百褶裙 | pleated skirt | 5/4 | 5/1 | 5/2 | 5/1 | 5/2 | 5/5 | 5/5 | 5/5 |
| 牛仔短褲 | denim shorts | 4/5 | 3/2 | 3/2 | 5/3 | 5/3 | 3/5 | 4/5 | 4/5 |
| 草帽 | straw hat | 2/5 | 4/2 | 5/2 | 5/2 | 5/2 | 3/5 | 4/5 | 4/5 |
| 眼鏡 | glasses | 1/2 | 1/2 | 1/2 | 1/3 | 1/2 | 2/5 | 2/5 | 2/5 |
| 雙馬尾 | twin tails | 5/5 | 5/2 | 5/2 | 5/2 | 5/2 | 4/5 | 4/5 | 4/5 |
| 銀髮 | silver hair | 4/5 | 5/2 | 5/4 | 5/3 | 5/3 | 5/5 | 5/5 | 5/5 |
| 微笑 | smile, open mouth, slight smile | 5/5 | 5/1 | 5/1 | 5/2 | 5/1 | 5/5 | 5/5 | 5/5 |
| 海邊 | seashore, beach, ocean | 4/5 | 5/5 | 5/5 | 5/5 | 5/5 | 5/5 | 5/5 | 5/5 |
| 下雪 | snow, snowing, winter | 5/4 | 5/1 | 5/1 | 5/2 | 5/2 | 5/5 | 5/5 | 5/5 |
| 夕陽 | sunset, golden hour, sunbeam | 5/3 | 5/1 | 5/1 | 5/4 | 5/4 | 5/5 | 5/5 | 5/5 |
| 水彩 | traditional media, watercolor, watercolor (medium) | 3/5 | 4/4 | 4/4 | 5/3 | 5/3 | 4/5 | 4/5 | 4/5 |
| 俯視 | from above, high angle, overhead | 5/3 | 4/4 | 0/1 | 5/3 | 5/3 | 4/5 | 5/5 | 5/5 |
| 坐著 | sitting | 4/5 | 4/3 | 4/2 | 5/1 | 5/1 | 4/5 | 4/5 | 4/5 |
| **合計 p5** | | **67** | 73 | 69 | **81** | **81** | 69 | **76** | **76** |
| 合計 p10（滿分 170） | | 129 | 140 | 135 | 153 | 150 | 133 | 144 | 144 |
| **合計 div5** | | 76 | 37 | 36 | 41 | 40 | 85 | **85** | **85** |

前 10 名的距離分布（用來重校分級門檻）：

| 代號 | 正解距離中位數［範圍］ | 非正解距離中位數［範圍］ |
| :--- | :--- | :--- |
| V0 | 0.259［0.189, 0.335］ | 0.289［0.204, 0.336］ |
| V1d | 0.234［0.165, 0.315］ | 0.278［0.169, 0.340］ |
| V3d | 0.215［0.127, 0.305］ | 0.273［0.167, 0.338］ |
| V4d | 0.221［0.151, 0.343］ | 0.278［0.193, 0.344］ |

## 觀察

- **R2 的病例解了**：「涼鞋」V0 前 5 名是 `sandals`、`cowboy boots`、`sandals`、`wearing nike sneakers`、`sneakers`；V4d 前 5 名 5 筆都是 sandal 類、5 組 tag 都不同。
- **只有中文原話不夠**：V1（片段端英文、查詢端中文）只比現行多 6；V1d 去重後只剩 69，跟現行差不多。跨語言比對撐不起來，查詢端要帶英文。
- **現行做法在「整套片段本身就以這個 facet 為主」時不差**：和服、雙馬尾、微笑、下雪、俯視 V0 都是 5。改善集中在穿著的小 facet（鞋履、頭部）、以及 V0 會混進沒這個 facet tag 的列的題目（眼鏡的 V0 前 5 名有 4 筆在配件 facet 底下沒有任何 tag）。
- **去重的代價**：去重後少數題的 p5 掉 1（涼鞋 V3d、雙馬尾），因為第 2 名以後換成較遠但不同的 tag 組合。拿 5 分精確換到多樣性從 41 到 85，值得：RAG 的價值在具體、少見的詞，5 筆一樣的 `sandals` 給不出 `strappy sandals`。
- **翻譯不穩定**：同一個提示、temperature 0，三次執行的英文 tag 不同（和服：`kimono` → `kimono, upper body`；雙馬尾：`twintails` → `twin tails`；水彩：`watercolor, traditional media` → `watercolor (medium)` → 三個都有）。表中數字是第三次、翻譯快取住之後的結果。V4 帶著中文原話，翻譯偏掉時比 V3 穩，這是選 V4d 而不是 V3d 的理由。
- **眼鏡是知識庫缺口**：配件池 1,242 筆裡只有 5 筆是眼鏡，任何做法都只找得到 1–2 筆。這不是排序問題。

## 限制

- 17 題，正解用 regex 判；「和服」查到 `japanese clothes` 算錯、「眼鏡」查到 `sunglasses` 算錯，屬於判準偏嚴，對各組一致。
- 正解判斷依賴 `facet_tags`（LLM 回填），回填本身歸錯 facet 的列對各組影響相同。
- 英文 tag 用獨立提示產生，不是真的在對話裡由 `SetFacetStates` 給；實際上模型看得到完整描述，翻譯應該不會更差。
- V0 用精確排序；線上走 HNSW iterative scan，是近似結果，實際的 V0 可能略差。
- 去重以正規化後的 facet 文字完全相同為準；`sandals` 與 `sandals, white socks` 算不同。也沒有忽略順序：「夕陽」的 V4d 同時留下 `sunset, golden hour` 與 `golden hour, sunset`。正式實作的去重鍵要先把 tag 排序。
- 中文原話是手寫的短詞（兩三個字）。實際的 `query` 是模型從使用者句子切出來講這個 facet 的那一段。對照 2026-09-29 開發庫 audit 裡完整記下的 `SearchPresets` 參數（#7 之後才有，111 次呼叫中只有 11 個 facet 項目）：長度 2–10 字、中位數 4（「涼鞋」「睡覺」「窗台」「雨夜街頭」「高山上的一間木製小屋」），跟實驗的短詞相近。另有兩種寫法值得注意：模型自己夾英文（`涼鞋 sandals`），以及把 facet 名稱寫進去（「少女年齡性別」）。樣本很少，驗收時用正式路徑重跑再看。

## 驗收：正式路徑重跑（2026-09-30）

開發庫跑 `scripts/embed_facet_tags.py` 產生全部 37,011 個 facet 向量後，同一組 17 題走正式的 `SearchFacetSql`（與 `PresetRepository` 逐字相同，`@k` 取 10），查詢句「中文原話（英文 tag）」，英文 tag 用上面結果表那一欄。

| 查詢 | 英文 tag | p5 | div5 | p10 | 前 5 名距離 |
| :--- | :--- | :-: | :-: | :-: | :--- |
| 涼鞋 | sandals | 5 | 5 | 9 | 0.197–0.278 |
| 運動鞋 | sneakers | 5 | 5 | 10 | 0.233–0.264 |
| 連帽外套 | hoodie | 5 | 5 | 9 | 0.219–0.271 |
| 和服 | kimono | 4 | 5 | 9 | 0.219–0.257 |
| 百褶裙 | pleated skirt | 5 | 5 | 10 | 0.191–0.229 |
| 牛仔短褲 | denim shorts | 3 | 5 | 4 | 0.214–0.251 |
| 草帽 | straw hat | 4 | 5 | 5 | 0.172–0.279 |
| 眼鏡 | glasses | 2 | 5 | 2 | 0.221–0.332 |
| 雙馬尾 | twin tails | 4 | 5 | 9 | 0.151–0.216 |
| 銀髮 | silver hair | 5 | 5 | 9 | 0.175–0.228 |
| 微笑 | smile, open mouth, slight smile | 5 | 5 | 9 | 0.179–0.196 |
| 海邊 | seashore, beach, ocean | 5 | 5 | 10 | 0.196–0.217 |
| 下雪 | snow, snowing, winter | 5 | 5 | 10 | 0.182–0.215 |
| 夕陽 | sunset, golden hour, sunbeam | 5 | 5 | 9 | 0.195–0.217 |
| 水彩 | traditional media, watercolor, watercolor (medium) | 4 | 5 | 8 | 0.183–0.236 |
| 俯視 | from above, high angle, overhead | 5 | 5 | 10 | 0.169–0.178 |
| 坐著 | sitting | 4 | 5 | 9 | 0.184–0.219 |
| **合計** | | **75** | **85** | **141** | |

- 跟 V4d（76／85）只差 1。差異來自正式版剝了權重、`tag_key` 不看順序，以及 tie-breaker 改變了同距離時留下哪一筆。
- 沒命中的多半是判準偏嚴：`sit`、`w sitting`、`short jeans`、`twin ponytails`、`afternoon, sun`、`sunglasses`。
- 相異組合 85／85：去重後前 5 名都是不同的 tag 組合。
- 線上（F1）R2 那句的鞋履前 5 名與這裡逐筆相同，距離一致。

近似錨的門檻、查詢速度與執行計畫見設計 §6.4、§7.1；線上驗收見 `docs/eval-cases.md` 的 F1–F5。

## 重跑

實驗腳本沒有進版控（一次性）。要重跑：對上表每一題，從 `prompt_knowledge_presets` 取 `facet_ids && [facet]` 的列與 `facet_tags -> facet`，依上面「比較的組合」算向量、精確排序、取前 5／10，依正解規則計分。實作完成後，同一組 17 題用正式的 `SearchPresets` 路徑再跑一次，當本案的驗收。
