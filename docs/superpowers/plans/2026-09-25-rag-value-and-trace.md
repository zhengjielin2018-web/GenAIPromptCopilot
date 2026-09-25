# RAG 到底有沒有用：量測計畫與前端檢索過程顯示

日期：2026-09-25
性質：**討論用計畫**，不是可直接執行的任務清單。下一個對話從 §5 的問題開始，決定後再走 brainstorm → spec → plan。
前情：子專案 2 修正輪（known-issues #1、#2、#9、追問政策、定稿閘門、tag 來源、facet 層級檢索、圖片署名）都已 merge 到 master（最新 `015e3a6`）。

---

## 1. 問題

使用者 2026-09-25 實測後的疑慮：**「RAG 目前的意義是什麼？會不會直接讓 LLM 生 prompt 就有差不多的效果？」** 附帶需求：前端要能看出 RAG 的經過，可切換是否顯示詳細資訊。

這兩件事要分開想：第一件是產品價值問題，要用量測回答；第二件是可觀測性功能，不管第一件答案如何都值得做，而且它是量測的工具之一。

## 2. 現在 RAG 實際做了什麼

| 環節 | 內容 | 目前對使用者可見嗎 |
| :--- | :--- | :--- |
| `SearchPresets` | 使用者講到的每個 facet 各一句查詢（facetId），沒講的維度各兩個對比方向；HNSW＋facet 過濾；每項回候選池筆數、命中片段（band、dist、可否借入、facet 覆蓋） | 工具卡只有一行摘要「鞋履 池 300 → 5」與縮圖；片段內容、距離、可否借入都看不到 |
| `SearchSimilarPrompts` | 整句需求找相似的既有作品，只供風格參考 | 工具卡一行「相似作品 3」 |
| ledger | session 內看過的片段與命中紀錄 | 不可見 |
| `AskUser` 選項 | 模型從片段挑 `presetId` 當選項 | 選項 chip 可點開抽屜 |
| 定稿 tag 來源 | 伺服器比對 ledger 標 rag / llm / base | chip 三種樣式，rag 可點 |
| audit `tagOrigins` | 定稿時三種來源的計數 | 只在資料庫 |

近幾次定稿的 `tagOrigins`（正向 tag）：rag 10–11、llm 13–22、base 3。也就是每次約三分之一到四成的 tag 能對到片段。**但這個數字不能證明價值**：模型沒看片段也可能寫出同樣的 tag（`1girl`、`long hair`、`shorts`）。要回答疑慮，必須做對照。

## 3. RAG 可能有價值的地方（假設，待驗證）

1. **冷門詞彙**：`virgin killer sweater`、`hakama skirt`、`pelvic curtain`、`stirrup legwear` 這類 danbooru 專有 tag，LLM 可能不知道或寫成錯的同義詞。知識庫 19,354 筆片段的價值主要在這裡。
2. **視覺化選項**：追問的選項帶片段縮圖，使用者可以「看圖選」，而不是讀文字選。這是 RAG 帶來的 UX，不是提示詞品質。
3. **覆蓋可見**：候選池筆數讓知識庫的缺口在使用當下看得見（vehicle 的 pose 只有 2 筆）。這是給開發者的價值。
4. **可歸因**：rag chip 讓使用者知道哪些詞有出處、可以點回去看圖。純 LLM 做不到。
5. **一致性**：同一題材多次生成，借用同一批片段的詞會比較穩定。未驗證。

反面假設：Gemini 對常見題材（少女、街景、寫實攝影）的 danbooru tag 知識已經足夠，RAG 只是把它本來就會寫的詞換成有出處的版本，延遲與 token 卻多了一截。

## 4. 怎麼量測

### 4.1 前提：一個「關掉 RAG」的開關

沒有開關就沒有對照組。建議做法（後端小改，也是前端切換比較的基礎）：

- `POST /api/sessions` 接受 `retrieval: "on" | "off"`（預設 on），存進 `Session`。
- `ToolSetBuilder.Build` 在 off 時不註冊 `SearchPresets` 與 `SearchSimilarPrompts`。
- `SystemPromptBuilder` 在 off 時把「## 流程」第 1 條換成不含檢索的版本（「先 `SetProfile`、`SetFacetStates`，然後 AskUser 或 FinalizePrompt」），其餘規則不變。追問、閘門、定稿格式、tag 來源標示（全 llm）照常。
- audit 的 `Turn_Completed` 記 `retrieval` 欄位，事後可分組。
- 前端 TopBar 加一個「使用知識庫」開關，只影響新開的 session。

### 4.2 離線 A/B 腳本（`scripts/eval_rag.py`，走 API）

- 題目集：`docs/eval-cases.md` 的輸入加上新寫的，約 30 句，涵蓋四種 profile、常見與冷門穿著／風格（例：巫女服、袴、水手服、賽博龐克、油畫、水彩）。每句附「使用者講到的 facet 清單」作為覆蓋率的標準答案。
- 每句各跑 on / off 兩個 session，用「直接給我」讓它一輪定稿（避免追問分岔），取 `final.positive`、`tagOrigins`、耗時、token（audit 有 `prompt_tokens`／`completion_tokens`）。
- 指標：
  1. **tag 有效率**：tag 是否在 danbooru 標籤詞表裡。詞表只當量測用的 oracle，**不灌進 presets**（先前決定：danbooru 詞典暫緩，還沒做的正是「標註準確率量測與詞表正規化」，這一步就是它）。
  2. **RAG 獨有貢獻**：只出現在 on 版、且被標 rag 的 tag。看它們是冷門詞（詞表頻率低）還是通用詞。這是回答疑慮的核心數字。
  3. **覆蓋率**：使用者講到的 facet 有沒有反映在提示詞裡。先用 LLM 當評審給二值判定，抽 10 句人工核對評審。
  4. **成本**：每輪耗時、token、Gemini 呼叫次數（on 多一次 embedding batch 與更長的 context）。
  5. **人眼比較（可選但最有說服力）**：挑 10 句，把 on / off 的提示詞丟同一個 SD 模型、同 seed 各生 2 張，盲測選比較符合描述的那張。
- 輸出一份 Markdown 報表放 `docs/eval-rag-YYYY-MM-DD.md`。

### 4.3 判讀

- RAG 獨有貢獻少、且多為通用詞 → RAG 的價值主要是 UX（看圖選、可歸因、覆蓋可見）。產品敘事要改成這樣講，不要再說「提升提示詞品質」；可以考慮把檢索改成「按需」（只查冷門維度）以省成本。
- RAG 獨有貢獻裡冷門詞佔多數、且有效率高 → 保留並加強：facet 層級檢索已經做了，下一步是擴知識庫冷門維度、調 k。
- 兩種都不明顯 → 先修量測，不下結論。

## 5. 下一個對話要先決定的事

1. 先做 §4.1 的開關（後端半天、前端一個 toggle），還是先做 §6 的檢索過程顯示？開關是量測的前提，我建議先做。
2. §4.2 的題目集誰寫：從 eval-cases 擴充，還是使用者提供實際用過的句子（audit 裡有 20 多句真實輸入可以直接拿）。
3. 要不要做 §4.2 第 5 點的人眼比較（需要本機 SD 環境）。
4. danbooru 詞表當量測 oracle 是否可接受（先前只是暫緩灌進 presets，沒說不能拿來量測）。
5. **§8 的「整套建議」定位要不要優先於對照組**：若採用，量測題目從「tag 品質」變成「建議採用率」，§4 的 A/B 往後排。

## 6. 前端「檢索過程」顯示（可切換詳細）

目標：使用者能看到每一輪 RAG 做了什麼、拿到什麼、最後用了什麼。預設收起，一個全域開關「顯示檢索細節」（存 localStorage）。

### 6.1 資料從哪來

- 現在 `tool_result` 事件對 `SearchPresets` 只有摘要字串與 preset 清單。**audit 不能當來源**：`Tool_Invoked` 的 args／result 截到 200 字（known-issues #7 第 4 點）。
- 所以要擴 `ToolResultEvent`：加一個 `detail` 欄位，`SearchPresets` 時放結構化結果：每個項目的 `facetId`／`dimension`、查詢句、`grounded`、`poolSize`、k、命中清單（id、title、band、dist、usable、每個 facet 的 covered/missing），**不放 snippet 本文**（抽屜已有）。`SearchSimilarPrompts` 放 intent 與命中的 intent 前 40 字。
- `FinalEvent` 已有 `positiveSources`／`negativeSources`，可反推每個片段貢獻了哪些 tag。
- `GET /api/sessions/{id}` 要不要回 detail：重新整理後對話流從 sessionStorage 來，事件本來就存在前端，不需要後端補。

### 6.2 畫面

- **工具卡展開**（開關開時）：`SearchPresets` 卡從一行摘要變成每個項目一列：facet 名｜查詢句｜池｜命中數，點開列出命中：標題、相似度分級、距離、可借入／僅供建議，命中的縮圖照舊可點。
- **定稿卡加「檢索貢獻」區塊**（開關開時）：這次定稿 rag/llm/base 各幾個；哪些片段貢獻了哪些 tag（片段標題 → tag 清單）；「僅供建議」的片段若仍有 tag 被借用，標警告（主規格 §5.3 要擋的事）。
- **session 層級摘要**（可放在儀表板下方）：本 session 查過幾次、各維度候選池、看過幾筆片段、借用幾筆。
- 開關關閉時畫面與現在一樣。

### 6.3 與 §4 的關係

有了 detail 事件，`scripts/eval_rag.py` 也能直接從 SSE 取結構化結果，不必再解 audit。前端開關 on/off 對比也能讓使用者自己感受差異，不用等離線報表。

## 7. 建議順序

1. §4.1 開關（後端＋TopBar toggle）。
2. §6 detail 事件與工具卡展開（後端事件＋前端）。
3. §4.2 離線 A/B 腳本與報表。
4. 依 §4.3 判讀決定 RAG 的定位，回頭改 README 與主規格的敘事。

已知風險：off 模式下模型仍可能「自稱」借用；tag 來源會全部標 llm，那是正確的。§4.2 的 LLM 評審與人眼比較都有主觀成分，樣本要夠。

## 8. 另一個定位假設：RAG 提供「整套組合」的建議，不是借 tag（使用者 2026-09-25 提出）

**想法**：如果借 tag 的品質跟純 LLM 差不多，RAG 的價值可能在於「整套圖片的建議」。使用者說了「涼鞋」，就找出幾套含涼鞋的完整穿搭（有圖），讓使用者參考要不要改成那樣。理由：使用者自己想不到那麼仔細的設定。

**為什麼這比借 tag 站得住腳**：它用到知識庫裡 LLM 沒有的東西——真實存在、有圖可看、彼此搭配過的完整組合。LLM 寫得出 `sandals`，寫不出「這套搭起來長什麼樣」，也沒辦法讓使用者看圖決定。

### 8.1 對應到現有系統

| 需要 | 現況 | 缺口 |
| :--- | :--- | :--- |
| 「含這個元素的完整組合」檢索 | 只有「跟這句最像的片段」 | 新的檢索模式：先以 tag 包含（沿用 `TagAttribution` 的字尾規則）篩出片段含 `sandals` 的，再依與使用者整段服裝描述的向量距離排序，取 2–3 套。知識庫 `sandals` 有 19 筆、`short shorts` 18 筆，夠當建議。風格、場景同理（「油畫」→ 整組 genre＋palette＋render） |
| 把整套攤給使用者看 | `Discuss`／`AskUser` 的選項可帶 `presetId`，抽屜看得到整套片段與圖 | 選項旁直接顯示縮圖；「採用這套」一鍵把整組 tag 帶進定稿（現在點選項只是把文字填進輸入框） |
| 提示詞政策 | 追問給 2–4 個文字方向 | 追問改成「要不要參考這幾套」給圖；使用者講到某個單品時主動附「含這個單品的組合」 |
| 事件與前端 | `tool_result` 只有摘要與縮圖 | 與 §6 的 detail 事件共用；建議卡是新的元件 |

### 8.2 對量測的影響

- 題目從「RAG 借的 tag 比 LLM 好嗎」變成「使用者有沒有採用建議的組合；採用後定稿與原始描述差多少；採用的組合是否讓使用者補上原本沒想到的 facet」。這是**採用率與擴充率**，不是 tag 品質。
- off 模式做不出這種建議，對照組的意義變成「有沒有這個功能」，不再是品質高低。§4 的 A/B 不衝突，但優先順序倒過來：先做出整套建議讓使用者實際用，再決定要不要花力氣量 tag 品質。
- audit 要記：建議了哪些 preset、使用者採用了哪一個、採用前後的 facet 覆蓋數。

### 8.3 風險

- 片段品質不一：Civitai 來的有些是整句自然語言（`wearing Nike sneakers, wearing sunglasses`），不是乾淨的 tag 組；建議卡要能容忍，或以 band／來源篩。
- 建議會把使用者沒要求的元素帶進來（整套穿搭含 `white socks`），採用時要讓使用者能勾選保留哪些，或至少在定稿卡上看得出哪些是「採用組合帶進來的」（tag 來源可加第四種 `adopted`）。
- 「含這個元素」的篩選靠 tag 字尾比對，同義詞（`slippers` vs `sandals`）抓不到；先接受，之後再看要不要用向量補。

### 8.4 若採用，建議順序

1. 檢索模式：`SearchPresetsContaining(facetId, tag, contextQuery, k)` 或在 `SearchPresets` 項目上加 `mustContain` 欄位；SQL 先過濾再排序；單元＋整合測試。
2. 提示詞：追問與 Discuss 在有命中時附「參考組合」選項（presetId）。
3. 前端：選項帶縮圖；「採用這套」動作（後端一個 `adopt` 訊息或直接送「採用〈標題〉這套」讓模型 FinalizePrompt）。
4. audit 與 §6 的檢索過程顯示一起做。
5. 量採用率，回頭決定 §4 要不要做。
