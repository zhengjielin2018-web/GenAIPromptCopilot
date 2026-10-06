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

## 4. 改後（分支 `feat/retrieval-timing` 的 commit `df3a7a5`，2026-10-06）

流程說明加上檢索規則、擋只寫維度名稱的查詢、確認輪檢索結果多留一輪（Task 7–9）。

session：`e05df4402b4e4d0e844544cac58e3c3c`、`3e5c09a82a914f20b822fcfa4caffbad`、`af13e1ff320a4bd4b7b1a9e5d9cf49f0`、`6b92af179847421caf68aee1daa7d4d4`、`12ed4d5be01e4668a38b334319849c1f`、`a86040ae305345c6b3211cf3ec209398`、`27e1096d42d34695bca81a832dda5f3e`、`1f5aab86fa5547eca52abbd4391df219`、`d3125cec7e2b4c41ba687aac71e3840c`

### 4.1 報表

```
- 動手輪檢索率（不含採用）：34/39（87.2%）
- 「隨便」確認輪檢索率：4/6（66.7%）
- 帶參考方向的 Discuss 輪檢索率：2/2（100.0%）
- 選項帶 presetId：28/123（22.8%）
- 每次定稿平均（24 次）：借來 6.3、碰巧對上 9.0、llm 3.7、base 3.0、adopted 0.0
- rag（借來＋碰巧對上）佔非基礎詞：368/457（80.5%）
- 延遲中位數：確認輪 有檢索 5724 ms（6 輪）／沒檢索 2720 ms（35 輪）；動手輪 有檢索 7173 ms（34 輪）／沒檢索 4906 ms（5 輪）
```

兩種輪不分有無檢索的延遲中位數：確認輪 2887 → 2773 ms（42 → 41 輪），動手輪 6098 → 7111 ms（40 → 39 輪）。

### 4.2 每步檢索判定（3 次合計）

| 劇本步 | 預期 | OK | MISS | NO-TURN | SKIP | 備註 |
| :--- | :--- | ---: | ---: | ---: | ---: | :--- |
| Q1-1 第一句描述 | act | 3 | 0 | 0 | 0 | |
| Q1-2 追問選第一個 | act | 3 | 0 | 0 | 0 | |
| Q1-3 追問＋「衣服你幫我設計」 | both | 0 | 3 | 0 | 0 | 動手輪 3/3 有查；確認輪 0/3 |
| Q1-4 家居感的衣褲 | act | 2 | 1 | 0 | 0 | |
| Q1-5 推薦一些場景設計 | propose | 3 | 0 | 0 | 0 | 2 次 `Discuss` 帶參考方向；1 次被當成修改（確認卡＋重新定稿） |
| Q1-6 深夜咖啡廳前 | act | 3 | 0 | 0 | 0 | |
| Q1-7 改成黑長直髮的上班族女士 | act | 3 | 0 | 0 | 0 | |
| Q1-8 家居服＋齊劉海＋年輕上班族 | act | 3 | 0 | 0 | 0 | |
| Q2-1 森林裡的狐狸 | act | 3 | 0 | 0 | 0 | |
| Q2-2 其他隨便，你決定 | both | 0 | 3 | 0 | 0 | 確認輪 3/3 有查；動手輪 0/3（卡上內容來自確認輪的檢索，見 §5） |
| Q3-1 穿和服的少女在神社前 | act | 3 | 0 | 0 | 0 | |
| Q3-2 追問選第一個 | act | 2 | 0 | 1 | 0 | 1 次確認輪失敗（§4.5），下一步重送同一張卡 |
| Q3-3 追問選第一個 | act | 3 | 0 | 0 | 0 | |
| Q3-4 讓她更有氣質 | both | 0 | 3 | 0 | 0 | 動手輪 3/3 有查；確認輪 0/3 |

### 4.3 檢索耗時

`SearchPresets` 工具呼叫 40 次，中位數 549 ms，最大 913 ms。

### 4.4 Q1 最後定稿

1. `masterpiece, best quality, highly detailed, photorealistic, cinematic lighting, film grain, young woman, blunt bangs, long hair, thoughtful expression, standing, looking away, loose comfortable top, pajama pants, leather boots, scarf, city street, neon lights, warm ambient lighting, warm glow from nearby cafe, reflections in puddles, rain, close-up, upper body, eye level, bokeh`
2. `masterpiece, best quality, highly detailed, photorealistic, cinematic lighting, realistic textures, upper body, close-up, shallow depth of field, young woman, young office worker, long black hair, straight hair, blunt bangs, standing, looking at viewer, holding umbrella, cozy oversized sweater, pajama pants, scarf, cozy coffee shop storefront at night, warm lights, illuminated sign, neon lights, warm lighting, rain`
3. `masterpiece, best quality, highly detailed, photorealistic, cinematic lighting, film grain, upper body, depth of field, young woman, office lady, long black hair, straight hair, blunt bangs, subtle smile, peaceful expression, trench coat, comfy lounge pants, slippers, cafe storefront, warm lighting, glowing signboard, rain, standing, hands in pockets`

### 4.5 協定違規

改後 9 個 session 有 4 次 `Protocol_Violation`、1 次 `Turn_Failed`；基準 1 次、0 次。都發生在確認輪：

- 回答追問時想直接動手：叫了確認輪沒有的 `Session_SetFacetStates`、`FinalizePrompt`。Q3 第 1 次的第 2 步兩次重試都這樣，整輪失敗；同一句再送一次也先違規一次才成功。
- 「隨便」的確認輪叫 `FinalizePrompt`。
- 「推薦一些場景設計」被輸入分類器判成隨便（這一輪沒有 `Discuss`），模型仍叫 `Dialog_Discuss`。基準也有 1 次同樣情況。

## 5. 對照與結論

| 指標 | 基準 | 改後 | 目標 | 判定 |
| :--- | :--- | :--- | :--- | :--- |
| 動手輪檢索率（採用除外） | 9/40（22.5%） | 34/39（87.2%） | ≥ 90% | 未達（差 1 輪）；排除前一個確認輪已查的則 38/39 |
| 交給模型決定／推薦的確認輪檢索率 | 0/12 | 6/12（50%） | ≥ 80% | 未達 |
| 每次定稿「借來」的 tag | 1.3 | 6.3 | 平均 ≥ 2 | 達成 |
| rag 佔非基礎詞 | 25.1% | 80.5% | ≥ 50% | 達成 |
| 確認輪延遲中位數增加 | — | −0.1 秒 | ≤ 3 秒 | 達成 |
| 動手輪延遲中位數增加 | — | +1.0 秒 | ≤ 3 秒 | 達成 |

「交給模型決定／推薦的確認輪」取重播表裡預期 `propose`／`both` 的 12 輪（Q1-3、Q1-5、Q2-2、Q3-4 各 3 次）；報表的「隨便」與帶參考方向的 `Discuss` 輪都落在這 12 輪裡。延遲增加是改後中位數減基準中位數，兩種輪各自不分有無檢索。

**人工檢查（Q1 定稿的穿著與咖啡廳寫法，逐一查知識庫片段）**：基準三次共 6 個寫法，知識庫 0 筆（`comfortable lounge pants`、`comfortable lounge shirt`、`casual lounge pants`、`loungewear pants`、`cozy cafe storefront at night`、`in front of a late-night coffee shop`）。改後三次共 9 個，4 個在知識庫裡有（`pajama pants` ×2、`cozy oversized sweater`、`warm glow from nearby cafe`），5 個沒有（`loose comfortable top`、`comfy lounge pants`、`cozy coffee shop storefront at night`、`cafe storefront`、`glowing signboard`）。第 3 次「家居感的衣褲」那一輪動手輪沒查，穿著兩個寫法都不在知識庫。

**結論**：

- **定稿裡的知識庫內容大幅增加**：rag 佔非基礎詞 25% → 80%，模型自己寫的（llm）每次定稿 13.9 → 3.7 個。「借來」1.3 → 6.3 用的是同一把尺；§3.5 說過這把尺偏高，但前後差距遠大於偏差。
- **動手輪檢索率差 1 輪未達**：5 個沒查的動手輪裡，4 個的內容來自前一個確認輪已經查過的結果（Q2-2 三次、Q1 第 3 次的推薦），流程說明本來就允許「確認卡的內容是從上一輪檢索結果挑的，就直接用那個片段」。真正漏查的只有 Q1 第 3 次的「家居感的衣褲」。指標的定義沒有排除這種情況，所以照定義判未達。
- **確認輪的檢索只做到一半**：「隨便」3/3、「推薦」3/3；單項委託（追問的回答裡夾「衣服你幫我設計」）0/3、模糊要求（「讓她更有氣質」）0/3。這兩種情況動手輪都有查，所以定稿仍有借用，但確認卡上的內容與解讀是模型自己寫的。
- **協定違規變多**（§4.5）：1 → 4 次，其中 1 輪失敗，使用者會看到錯誤。樣本小，但方向一致：確認輪拿到檢索結果後，模型比較常想直接動手。
- **退路判斷**（設計 §6.4）：
  - 檢索率未達的部分不是「模型不照流程查」，而是指標把合理的跳過算成漏查，加上確認輪兩種情況沒觸發，所以不建議直接上程式把關。
  - 確認輪的兩種情況，可以在流程說明的確認輪段落補寫判斷例子。
  - 協定違規可以在同一段補一句「查完照常用 Confirm」。
  - 這兩項都要再跑一次實驗才知道有沒有效，交給使用者決定。

## 6. 第二輪（分支 `feat/retrieval-timing` 的 commit `6952f57`，2026-10-06）

確認輪段落補兩個例子（單項委託夾在追問回答裡、模糊要求）與收尾規則（查完照樣用 Confirm）；報表多「含沿用前一個確認輪的檢索」一行、重播判定多 `CARRY`（設計 §10）。動手輪的流程說明沒動。

session：`398c7539182f4b70abe62fa39093cd7f`、`70700bbd3aa247c3bad4405c13416c03`、`c79b893f739f47ce9552c9602ee51e6f`、`ff876cd973444d02ae02c8958747e0c0`、`de4cb9565ed148bab1858a20714c63ea`、`43c34cfc8ea84c6f99d1b69a6de8d77a`、`9c0f84cf05714052ac30e65313b3a355`、`729b633042d04034a9df0794773feae6`、`a7c07a3b9ad8441c9fe3c480251848fb`

### 6.1 報表

```
- 動手輪檢索率（不含採用）：31/40（77.5%）
- 動手輪檢索率（含沿用前一個確認輪的檢索）：35/40（87.5%）
- 「隨便」確認輪檢索率：4/5（80.0%）
- 帶參考方向的 Discuss 輪檢索率：2/2（100.0%）
- 選項帶 presetId：18/124（14.5%）
- 每次定稿平均（25 次）：借來 4.4、碰巧對上 9.8、llm 5.6、base 3.0、adopted 0.0
- rag（借來＋碰巧對上）佔非基礎詞：355/495（71.7%）
- 延遲中位數：確認輪 有檢索 5689 ms（8 輪）／沒檢索 3088 ms（34 輪）；動手輪 有檢索 8219 ms（31 輪）／沒檢索 6166 ms（9 輪）
```

兩種輪不分有無檢索的延遲中位數：確認輪 3148 ms（42 輪），動手輪 7886 ms（40 輪）。

### 6.2 每步檢索判定（3 次合計）

| 劇本步 | 預期 | OK | CARRY | MISS | 備註 |
| :--- | :--- | ---: | ---: | ---: | :--- |
| Q1-1 | act | 3 | 0 | 0 | |
| Q1-2 | act | 3 | 0 | 0 | |
| Q1-3 追問＋「衣服你幫我設計」 | both | 0 | 0 | 3 | 動手輪 3/3 有查；確認輪仍 0/3 |
| Q1-4 家居感的衣褲 | act | 1 | 0 | 2 | |
| Q1-5 推薦一些場景設計 | propose | 3 | 0 | 0 | 2 次 `Discuss`；1 次被輸入分類器判成隨便，出確認卡並重新定稿 |
| Q1-6 深夜咖啡廳前 | act | 2 | 0 | 1 | |
| Q1-7 改成黑長直髮的上班族女士 | act | 2 | 0 | 1 | |
| Q1-8 家居服＋齊劉海＋年輕上班族 | act | 2 | 0 | 1 | |
| Q2-1 | act | 3 | 0 | 0 | |
| Q2-2 其他隨便，你決定 | both | 0 | 3 | 0 | |
| Q3-1 | act | 3 | 0 | 0 | |
| Q3-2 | act | 3 | 0 | 0 | |
| Q3-3 | act | 3 | 0 | 0 | |
| Q3-4 讓她更有氣質 | both | 2 | 0 | 1 | 確認輪 2/3 有查（第一輪 0/3） |

Q1 的 MISS 集中在第 1 次：第 4、6、7、8 步的動手輪都沒查。另外兩次只有第 2 次的第 4 步漏。

### 6.3 檢索耗時

`SearchPresets` 工具呼叫 39 次，中位數 587 ms，最大 10375 ms（單次；其餘都在 1.5 秒內）。

### 6.4 Q1 最後定稿

1. `masterpiece, best quality, highly detailed, photorealistic, cinematic lighting, realistic urban palette, upper body, eye level, young office lady, professional woman, young adult woman, blunt bangs, straight hair, long hair, calm expression, calms and serene expression, street, raindrops on lens, midnight café storefront, warm glowing lights, glowing storefront sign, wooden exterior, cozy atmosphere, neon lights, rain, standing on street, holding umbrella, looking away, comfortable lounge wear upper, soft knit sweater, comfortable lounge pants, relaxed trousers, soft fabric, cozy homewear style`
2. `masterpiece, best quality, highly detailed, photorealistic, cinematic lighting, full body, eye level, young office worker woman, blunt bangs, long black hair, straight hair, smiling, comfortable loungewear top, loungewear pants, sneakers, weatherproof fabric, wristwatch, standing, holding umbrella, cafe storefront, warm cafe lighting, glowing signboard, warm lighting, rain, rainy night`
3. `masterpiece, best quality, highly detailed, photorealistic, cinematic lighting, cafe exterior, late night cafe, rain, warm lighting, glowing cafe sign, blurred city skyline, glowing neon signs in background, close-up, upper body, young office worker woman, young woman, long hair, blunt bangs, smiling, leaning against wall, comfortable loungewear top, knit wear, loungewear pants, soft sweatpants, soft knit fabric`

穿著與咖啡廳的 16 個寫法，知識庫 0 筆。第 2、3 次的動手輪其實有查。以第 3 次為例，「家居服上衣」「家居褲」的命中多是「低」分級，而且內容混雜：`cozy oversized sweater, holding a steaming cup`、`flower print pajama pants, cotton pajama long sleeve top`、`black tank top, gray sweatpants, clothes`、`wearing loose off-shoulder top, pajama pants`。模型照「只借相符的詞」沒有借，自己寫了 `comfortable loungewear top`、`loungewear pants`、`soft sweatpants`。這是檢索品質（家居服這個概念在片段裡排序靠後、混在其他衣物裡），不是檢索時機。

### 6.5 協定違規

1 次 `Protocol_Violation`（「推薦一些場景設計」被判成隨便、這一輪沒有 `Discuss`，模型仍叫 `Dialog_Discuss`，重試後成功），0 次失敗。第一輪是 4 次、1 次失敗；基準 1 次。

### 6.6 對照與結論

| 指標 | 基準 | 第一輪 | 第二輪 | 目標 | 第二輪判定 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| 動手輪檢索率（含沿用確認輪的檢索） | 9/40 | 38/39（97%） | 35/40（87.5%） | ≥ 90% | 未達 |
| 動手輪檢索率（不含沿用，原定義） | 9/40（22.5%） | 34/39（87.2%） | 31/40（77.5%） | — | — |
| 交給模型決定／推薦的確認輪檢索率 | 0/12 | 6/12 | 8/12（67%） | ≥ 80% | 未達 |
| 每次定稿「借來」的 tag | 1.3 | 6.3 | 4.4 | ≥ 2 | 達成 |
| rag 佔非基礎詞 | 25.1% | 80.5% | 71.7% | ≥ 50% | 達成 |
| 每次定稿模型自己寫的 tag | 13.9 | 3.7 | 5.6 | — | — |
| 確認輪延遲中位數增加 | — | −0.1 秒 | +0.3 秒 | ≤ 3 秒 | 達成 |
| 動手輪延遲中位數增加 | — | +1.0 秒 | +1.8 秒 | ≤ 3 秒 | 達成 |
| 確認輪協定違規／失敗 | 1／0 | 4／1 | 1／0 | — | 回到基準 |

「交給模型決定／推薦的確認輪」：Q1-3 0/3、Q1-5 3/3、Q2-2 3/3、Q3-4 2/3。

**結論**：

- **協定違規修好了**：4 → 1 次，沒有失敗輪。
- **模糊要求有改善**（0/3 → 2/3）。**單項委託夾在追問回答裡仍是 0/3**，兩輪共 0/6。這種情況動手輪 6/6 都有查，所以定稿仍有機會借用，只是確認卡上的寫法是模型自己想的。
- **動手輪檢索率兩輪差很多**（97% → 87.5%）。這一輪沒改動手輪的流程說明，差距幾乎都來自 Q1 第 1 次連續 4 個動手輪沒查，看起來是同一份流程說明下的變異。也就是說，只靠流程說明，動手輪大約 85–95% 會查，有變異。
- **結果指標兩輪都遠高於基準**：rag 佔非基礎詞 25% → 72–81%，模型自己寫的 tag 13.9 → 3.7–5.6。
- **有查卻沒借的情況出現了**：「家居服」這個概念在知識庫裡排序靠後、混在其他衣物裡，模型依規則不借。這屬於檢索品質，要另外處理。
- **退路判斷**（設計 §6.4）：
  - 動手輪檢索率未達 → 對應做法 2（程式把關）。只靠流程說明已經改了兩輪，動手輪卻出現變異，再改文字的效益有限。
  - 確認輪的單項委託只能靠流程說明或接受現狀，程式無法判斷一句話裡有沒有委託。
  - 檢索品質（家居服）另開一案。

## 7. 決定（2026-10-06）

使用者看完第二輪結果後決定**照現狀收**：結果指標（rag 佔非基礎詞、模型自己寫的 tag）兩輪都遠高於基準；沒達標的動手輪檢索率變異與單項委託記在 known-issues #15（可能的方向是動手輪的程式把關），家居服這類概念的檢索品質記在 #16，另案處理。
