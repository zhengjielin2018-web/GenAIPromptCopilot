# 檢索時機實驗紀錄（2026-10-06）

設計：[檢索時機設計](../superpowers/specs/2026-10-06-retrieval-timing-design.md)

## 1. 問題

2026-10-05 的 session `ddaf2115…` 有 15 輪、定稿 5 次，`SearchPresets` 只在第 2 輪（第一個動手輪）叫了 1 次。最後定稿標 rag 的 4 個 tag 全是使用者原話直譯，只是剛好跟片段字面相同。原因是流程說明只在動手輪第 1 條要求檢索，回答追問、定稿後修改、隨便與確認輪都沒提。細節見設計 §1。

## 2. 方法

- **重播**：`manual-tests/replay.py`，劇本 Q1–Q3（見 `docs/eval-cases.md`「2026-10-06 檢索時機」），各跑 3 次。確認卡自動按下，有解讀選項時選第一個；追問一律選每個維度的第一個選項。
- **API**：Docker `api` 容器。模型與 embedding 照 `appsettings.json`。
- **報表**：`scripts/adoption_report.py --sessions …` 的「檢索時機」一節；`SearchPresets` 的耗時從 `audit_logs` 的 `Tool_Invoked` 取。
- **指標與通過標準**（設計 §6.3）：

| 指標 | 目標 |
| :--- | :--- |
| 動手輪檢索率（採用除外） | ≥ 90% |
| 交給模型決定／推薦的確認輪檢索率 | ≥ 80% |
| 每次定稿「借來」的 tag | 平均 ≥ 2 |
| rag（借來＋碰巧對上）佔非基礎詞 | ≥ 50% |
| 延遲中位數增加 | 確認輪、動手輪各 ≤ 3 秒 |

## 3. 基準（流程說明未改；分支 `feat/retrieval-timing` 的 commit `1479aa9`，2026-10-06）

只有量測程式，流程說明還是原本的。

session：`ec66be508c1f40b28e0b86c2c6eb5377`、`b7f8d6197a5d4af4b83f9bfe7c98cf21`、`10aae4a126914683a7bf875143a6faea`、`6452b8208aab46cda298c6e206699d8d`、`fe91da9ab903420c9a356cef6bfbb76f`、`ed8776b752784a4589f2b2619b200805`、`a3010e401ca24ebe89a6c69f1c78cfcb`、`656f98704fea4a838ba0e9ba65715449`、`e7a236aeabdf4984a3a870cb96fc160d`

### 3.1 報表

```
- 動手輪檢索率（不含採用）：9/40（22.5%）
- 「隨便」確認輪檢索率：0/6（0.0%）
- 帶參考方向的 Discuss 輪檢索率：0/2（0.0%）
- 選項帶 presetId：2/114（1.8%）
- 每次定稿平均（25 次）：借來 1.3、碰巧對上 3.4、llm 13.9、base 3.0、adopted 0.0
- rag（借來＋碰巧對上）佔非基礎詞：116/463（25.1%）
- 延遲中位數：確認輪 有檢索 —（0 輪）／沒檢索 2887 ms（42 輪）；動手輪 有檢索 7420 ms（9 輪）／沒檢索 5999 ms（31 輪）
```

「隨便」確認輪有 6 輪：Q2 的「其他隨便，你決定」3 輪，加上 Q1 的「衣服你幫我設計」3 輪（輸入分類器也判成隨便）。

### 3.2 每步檢索判定（3 次合計）

| 劇本步 | 預期 | OK | MISS | NO-TURN | SKIP |
| :--- | :--- | ---: | ---: | ---: | ---: |
| Q1-1 第一句描述 | act | 3 | 0 | 0 | 0 |
| Q1-2 追問選第一個 | act | 0 | 3 | 0 | 0 |
| Q1-3 追問＋「衣服你幫我設計」 | both | 0 | 3 | 0 | 0 |
| Q1-4 家居感的衣褲 | act | 0 | 3 | 0 | 0 |
| Q1-5 推薦一些場景設計 | propose | 0 | 3 | 0 | 0 |
| Q1-6 深夜咖啡廳前 | act | 0 | 3 | 0 | 0 |
| Q1-7 改成黑長直髮的上班族女士 | act | 0 | 3 | 0 | 0 |
| Q1-8 家居服＋齊劉海＋年輕上班族 | act | 0 | 3 | 0 | 0 |
| Q2-1 森林裡的狐狸 | act | 3 | 0 | 0 | 0 |
| Q2-2 其他隨便，你決定 | both | 0 | 3 | 0 | 0 |
| Q3-1 穿和服的少女在神社前 | act | 3 | 0 | 0 | 0 |
| Q3-2 追問選第一個 | act | 0 | 3 | 0 | 0 |
| Q3-3 追問選第一個 | act | 0 | 3 | 0 | 0 |
| Q3-4 讓她更有氣質 | both | 0 | 3 | 0 | 0 |

### 3.3 檢索耗時

`SearchPresets` 工具呼叫 9 次，中位數 691 ms，最大 1418 ms。

### 3.4 Q1 最後定稿

1. `masterpiece, best quality, highly detailed, photorealistic, raw photo, film grain, upper body, eye level, shallow depth of field, young woman, office worker, long black hair, blunt bangs, standing, looking away, contemplative mood, oversized knit sweater, comfortable lounge pants, cozy cafe storefront at night, raindrops on lens, warm lighting from window, glowing cafe sign, blurred traffic in background, warm interior lighting, rain, rainy night`
2. `masterpiece, best quality, highly detailed, young woman, office worker, long black hair, straight hair, hime cut, blunt bangs, upper body, eye level, in front of a late-night coffee shop, warm window light, glowing storefront sign, neon lights, wet asphalt reflecting glowing signs, blurred café interior, rain, photorealistic, film grain, shallow depth of field, bokeh, standing, holding umbrella, looking at viewer, comfortable lounge shirt, casual lounge pants`
3. `masterpiece, best quality, highly detailed, photorealistic, cinematic lighting, vibrant colors, upper body, eye level, 1girl, young woman, long black hair, straight hair, blunt bangs, confident smile, calm expression, standing, looking at viewer, comfortable knit sweater, loungewear pants, slippers, cozy cafe exterior, rain, warm window light, neon signs, rain drops on lens, illuminated storefront, blurred street background`

### 3.5 觀察

- **跟 10/05 那次一致**：9 個 session 全都只在第一個動手輪檢索（9/40 就是這 9 輪），交給模型設計、要求推薦、隨便的確認輪 0 次。
- **追問選項幾乎都是模型自己寫的**：114 個選項只有 2 個帶 presetId。
- **穿著與咖啡廳仍是模型自己編的寫法**：`comfortable lounge pants`、`comfortable lounge shirt`、`casual lounge pants`、`loungewear pants`、`cozy cafe storefront at night`、`in front of a late-night coffee shop`。
- **「借來」平均 1.3 是偏高的估計**：被算成借來的多是通用詞，例如 `standing`、`upper body`、`rainy night`、`neon signs`、`long black hair`、`anime`、`illustration`、`depth of field`。它們剛好出現在第一輪檢索的片段裡，模型之後才寫，照先後規則就算借來，但模型很可能本來就會這樣寫。所以先後規則量到的「借來」是上限，不是確定借用。改後的數字要用同一把尺比，不能當成絕對值讀。
- **Q1 第 2 次的「推薦一些場景設計」**被模型當成修改：出確認卡並重新定稿，沒有用 `Discuss` 列方向。
- **延遲**：確認輪中位數 2.9 秒；動手輪有檢索 7.4 秒、沒檢索 6.0 秒。不過有檢索的都是第一個動手輪，同一輪還有 `SetProfile`，兩者不能直接相減。
