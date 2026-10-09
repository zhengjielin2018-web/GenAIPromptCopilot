# AI Agent 化提案：從提示詞工作流到會自我驗證的 agent

日期：2026-10-08
狀態：**提案，未定案**。各項要不要做、先做哪個，由專案擁有者決定；定案的項目照慣例另開 `superpowers/specs/` 設計文件與 `superpowers/plans/` 計畫，本文件不取代它們。
對象：決定專案下一步方向的人。讀完應該知道：agent 化的重點是什麼（§2）、這個專案跟一般認知的「AI agent 專案」差在哪、每個差距的證據在哪、建議怎麼補、先後順序與代價、哪些事要先拍板。

---

## 1. 摘要

這個專案已經有 agent 的**控管骨架**，而且比多數作品集紮實：用工具清單限權、終止型工具與預算、一輪即交易、先確認再動手、三側安全、稽核與有指標的實驗。缺的是 agent 的**核心迴圈**：照 Anthropic〈Building effective agents〉（2024-12）的分法，它比較接近**工作流**（路由＋人工確認＋RAG）——流程由程式與流程說明決定，模型每一輪拿到 1–6 個工具，以一個終止型工具收尾。

最關鍵的一點：系統的產物是一段提示詞，但它**從來沒看過這段提示詞的效果**。「完成」的定義是「呼叫了 `FinalizePrompt`」，不是「畫面符合需求」。

| 優先 | 提案 | 補的差距（§5） | 規模 | 主要動到 |
| :--- | :--- | :--- | :--- | :--- |
| P0 | 閉環：生成 → 看圖 → 修正 | 1、2 | 大 | `Plugins/`、`Filters/`、`Safety/`、前端定稿卡與儀表板、docker compose |
| P1 | 自動化 eval | 5 | 中 | `manual-tests/replay.py`、`scripts/`、新的 GitHub Actions workflow |
| P1 | 自主程度切換＋流程說明改成目標導向 | 2、3 | 中 | `ToolSetBuilder`、`Prompts/`、`Session`、前端 TopBar |
| P2 | token 用量與 OpenTelemetry | 6 | 小 | `AgenticOrchestrator`、`Program.cs`、docker compose |
| P2 | 記憶（session 持久化、偏好記憶） | 4 | 中 | `Sessions/`、`db/migrations/` |
| P2 | MCP server | 7 | 小 | 新專案或新端點，共用 `KnowledgePlugin` 的實作 |
| P3 | 自評拆成獨立 agent；框架對應 | 8 | 中 | `Orchestration/` |

建議順序：**先量測（P2 token／OTel → P1 eval），再改行為（P0 → P1 自主程度）**，見 §8。

---

## 2. agent 化的重點

後面各節的差距與提案都從這一節的觀念出發。只想看結論的話，讀 2.1 與 2.6 就夠了。

### 2.1 一句話

agent 化的重點是：**讓系統自己檢查做出來的結果，再根據結果決定下一步，一直做到目標真的達成為止。**

重點不在多加工具、多幾個 agent，也不在讓模型更自由。這三件事都不是本質。

### 2.2 用這個專案舉例

使用者說：「銀髮少女站在雨夜的霓虹街頭，穿白色雨衣」。

**現在的流程：**

```text
確認卡 → 按下 → 設題材、標 facet、查知識庫、追問 → 定稿
產出：1girl, silver hair, white raincoat, neon lights, rainy night, ...
（結束）
```

接下來使用者把提示詞貼到 SD 生圖，可能看到頭髮偏灰藍、雨衣像透明塑膠、畫面裡根本沒有雨。他得回來描述問題，系統再改一版，他再去生、再看、再回來。

這個過程裡，**真正在「做 → 看 → 改」的是使用者**，系統只負責「寫」這一步。使用者才是 agent，系統是他手上的工具。

**agent 化之後：**

```text
確認卡 → 按下 → 設題材、查知識庫、寫草稿
  → 生圖 → 看圖：髮色 ✓、雨衣 ✓、霓虹 ✓、下雨 ✗
  → 補 heavy rain, raindrops, wet ground → 再生 → 看圖：全部 ✓
  → 定稿（附上圖和檢查結果）
```

差別只有一件事：**驗證這件事從使用者身上，移到了系統身上。**

### 2.3 第一個重點：看得到結果

把「完成」的定義拆開比較：

| | 現在 | agent 化後 |
| :--- | :--- | :--- |
| 什麼時候算完成 | 模型呼叫了 `FinalizePrompt` | 圖上確實有使用者要的東西 |
| 出錯誰發現 | 使用者 | 系統自己 |
| 儀表板的燈亮代表 | 模型說它寫了 | 圖上驗證過了 |

沒有回饋，模型只能「交出去」，沒辦法「做到」。這就像寫程式從不執行，只靠讀程式碼判斷對不對：寫得再仔細，也比不上跑一次看結果。

**回饋必須是真實的東西才算數。**讓模型重讀自己寫的提示詞、問自己「這樣對嗎」，效果很弱，因為它看的還是自己的想像。生出來的圖是外部的事實，所以 P0（§6.1）把生圖放在第一位。

### 2.4 第二個重點：下一步是誰決定的

[檢索時機實驗](experiments/2026-10-06-retrieval-timing.md)剛好說明這一點（數字見 §5.2）：

- 模型手上有檢索工具，但自己決定的時候，動手輪只有 22.5% 會去查。
- 處理方式是在流程說明裡加規則：第幾步要查、什麼情況要查。查詢率拉上來了，但同一份說明兩次實驗之間還是不穩定。

這是**工作流**的做法：步驟由人寫好，模型負責照著走；走錯了，人再補一條規則。

**agent** 的做法不是寫更多規則，而是讓模型有理由去查：用了知識庫沒有的寫法，生出來的圖就不對，自評不會過，模型在這次任務裡碰到失敗，就會自己去查、去換寫法。人只定義「什麼叫做對」，「怎麼做到」交給模型。

用帶新人來比喻：

- **工作流**：給他一份 SOP，第一步做什麼、第二步做什麼，照做就好。
- **agent**：告訴他目標和驗收標準，給他工具和權限，讓他自己安排步驟，做完交出能檢查的成果。

### 2.5 第三個重點：控管不是 agent 的反面

常見的誤解是「agent 化就是放手」。這個專案最好的那些設計，正好是 agent 最需要的：

| 已經有的 | 在 agent 裡的角色 |
| :--- | :--- |
| 工具清單限權 | 他**能**做什麼，由公司規定 |
| 工具預算、強制收尾 | 不會無限做下去 |
| 一輪即交易、回滾 | 做錯可以撤銷 |
| 先確認再動手 | 重要決定要請示 |
| 輸出安全審查 | 不能交出違規的東西 |

agent 化是在這套控管**裡面**，把「做 → 看 → 改」這個圈加上去，不是把控管拆掉換自由。§4 列的是要保留的部分，§7 列的是不建議做的「放手」。

### 2.6 四個判斷問題

看任何系統，問這四個問題，就知道它有多接近 agent：

| 問題 | 這個專案現在 |
| :--- | :--- |
| 下一步是模型決定，還是人預先寫好？ | 大多是人寫好（流程說明的編號步驟） |
| 它看得到自己行動的結果嗎？ | 只看得到知識庫的檢索結果，看不到最終產物 |
| 「完成」可以被檢查嗎？ | 不行，呼叫了定稿就算完成 |
| 出錯時它會自己發現嗎？ | 不會，要靠使用者回報 |

四題都能答「是」，就是 agent。這個專案目前大約在中間：模型確實自己選工具，所以 README 說「agentic」沒有錯；但每一輪只走一兩步，而且最終成果沒有回饋。

### 2.7 對照的尺：七個特徵

上面的四個問題是精簡版。下面是第 3 節逐項評分用的完整版，採業界目前的一般定義；若對照的是某個特定職缺描述，第 3 節的表要依職缺重排權重。

| # | 特徵 | 意思 |
| :--- | :--- | :--- |
| A | 目標導向 | 給一個目標，「完成」有可檢查的定義，不是「講完一段話」 |
| B | 自主決定步驟 | 模型自己決定下一步呼叫什麼工具、呼叫幾次；程式只守邊界 |
| C | 環境回饋 | 每一步從環境拿到真實結果（ground truth），據此修正，迴圈到達成目標或預算用完 |
| D | 記憶 | 工作記憶（這次任務）與長期記憶（跨任務的偏好、經驗） |
| E | 可量測 | 有自動化 eval：任務成功率、工具軌跡、成本、延遲，改動前後可比 |
| F | 可觀測、受控 | trace、成本、guardrails、預算、人工介入點 |
| G | 互通 | 工具與能力能被其他 agent 使用（MCP 等標準） |

### 2.8 各項提案跟核心的關係

| 提案 | 跟「做 → 看 → 改」的關係 |
| :--- | :--- |
| P0 生圖＋自評（§6.1） | 補上「看結果」這一圈，是核心本身 |
| P1 自主程度切換（§6.3） | 讓「下一步誰決定」可以調整，不再每一步都等人按按鈕 |
| P1 自動化 eval（§6.2）、P2 token 與 trace（§6.4） | 不是 agent 本身，而是回答兩個一定會被問的問題：「怎麼知道 agent 做對了」「它花了多少錢、哪裡慢」 |
| P2 記憶（§6.5）、MCP（§6.6） | 加分項：讓 agent 記得使用者，也能被別的 agent 使用 |

**如果只能做一件事，就做 P0**：讓系統自己生圖、自己看、自己改。這一步做完，專案就從「會寫提示詞的工作流」變成「會把提示詞做到對的 agent」。§8 的順序把量測排在 P0 前面，是為了做完之後說得出變好多少，不是因為量測比 P0 重要。

---

## 3. 現況對照

✅ 已具備　◐ 部分　✗ 缺

| # | 特徵 | 現況 | 證據 |
| :--- | :--- | :--- | :--- |
| A | 目標導向 | ◐ 目標是「產出提示詞」，完成＝呼叫終止型工具；有定稿閘門（還有缺就擋回），但沒有對「畫面是否符合需求」的檢查 | [主規格](superpowers/specs/2026-09-21-genai-prompt-copilot-design.md) §4.6 |
| B | 自主決定步驟 | ◐ 模型在輪內自己選工具，但流程是編號步驟寫在流程說明裡；輪的種類與可用工具由程式決定 | [`flow-act.md`](../src/PromptCopilot.Api/Prompts/flow-act.md)、[`flow-propose.md`](../src/PromptCopilot.Api/Prompts/flow-propose.md)、[`ToolSetBuilder.cs`](../src/PromptCopilot.Api/Orchestration/ToolSetBuilder.cs) |
| C | 環境回饋 | ✗ 不生成圖片，看不到產物的效果；唯一的「環境」是知識庫檢索 | 主規格 §2.2「實際生成圖片（只產 prompt）」 |
| D | 記憶 | ◐ 工作記憶有（`ChatHistory`、facet 狀態、ledger）；沒有長期記憶，session 重啟即清空 | [`SessionStore.cs`](../src/PromptCopilot.Api/Sessions/SessionStore.cs)、主規格 §2.2 |
| E | 可量測 | ◐ 有指標化的實驗與報表，但 eval 全靠人工跑、人工判讀 | [`eval-cases.md`](eval-cases.md)、[`experiments/`](experiments/) |
| F | 可觀測、受控 | ◐ 控管很完整；可觀測只有 audit 與 log 行，沒有 token、成本、trace | [`Filters/`](../src/PromptCopilot.Api/Filters/)、[`known-issues.md`](known-issues.md) |
| G | 互通 | ✗ 工具只在這個 app 裡用得到；LLM 只接 Gemini | [SK 架構說明](SK架構說明.md) §4 |

---

## 4. 已經是 agent 等級、要保留的

改造時這些都不要拆，它們正好是「受控自主」論述的基礎。

| 能力 | 在哪裡 | 為什麼重要 |
| :--- | :--- | :--- |
| 用工具清單限權，不靠 prompt 叫模型別做 | [`ToolSetBuilder.Build`](../src/PromptCopilot.Api/Orchestration/ToolSetBuilder.cs) | 違規的選項根本不在模型眼前；這是 agent 安全最實在的做法 |
| 終止型工具、每輪工具預算、強制收尾 | [`TerminalToolFilter`](../src/PromptCopilot.Api/Filters/TerminalToolFilter.cs)、[`ToolBudgetFilter`](../src/PromptCopilot.Api/Filters/ToolBudgetFilter.cs) | agent 迴圈一定會停 |
| 一輪即交易，失敗整輪回滾 | [`AgenticOrchestrator`](../src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs)、`Session.Snapshot()`／`Restore` | 工具副作用可撤銷；P1 的「復原」直接用得上 |
| 先確認再動手 | [先確認再動手設計](superpowers/specs/2026-10-05-confirm-before-act-design.md) | 人工介入點由程式保證 |
| 輸入、輸出、資料三側安全 | [`Safety/`](../src/PromptCopilot.Api/Safety/)、[`OutputSafetyFilter`](../src/PromptCopilot.Api/Filters/OutputSafetyFilter.cs)、`scripts/pipeline/nsfw_filter.py` | guardrails |
| connector 補丁與失敗分類重試 | [`Llm/`](../src/PromptCopilot.Api/Llm/)、[SK 架構說明](SK架構說明.md) §5 | 實戰經驗的證據，比換框架更有說服力 |
| 稽核紀錄＋有指標、有前後對照的實驗 | `audit_logs`、[`experiments/`](experiments/) | 已經有「量測後再改」的習慣，P1 eval 是把它自動化 |

---

## 5. 差距細節

### 5.1 沒有環境回饋（最大的差距）

主規格 §2.2 明確不做實際生成圖片。agent 沒有「行動 → 觀察 → 修正」，無法自我驗證；儀表板的 facet 燈號只代表「模型說有」，不代表「圖上有」。tag 寫錯、互相衝突（例如晴天與雨夜）、SD 不認得的寫法（known-issues #16 的 `comfortable loungewear top`），系統都不會發現。

### 5.2 流程寫死在流程說明裡，決策品質靠 prompt 撐

`flow-act.md` 與 `flow-propose.md` 是編號步驟。[檢索時機實驗](experiments/2026-10-06-retrieval-timing.md)是最直接的證據：

| | 動手輪檢索率（不含採用） | 交給模型決定／推薦的確認輪檢索率 |
| :--- | :--- | :--- |
| 基準（流程說明只在第 1 條要求檢索） | 9/40（22.5%） | 0/12 |
| 第一輪修改流程說明 | 34/39（87.2%） | 6/12 |
| 第二輪修改流程說明 | 31/40（77.5%）；含沿用前一個確認輪的檢索 35/40（87.5%） | 8/12 |
| 目標 | ≥ 90% | ≥ 80% |

模型自主決定時很少查知識庫；靠加流程規則才拉上來，同一份說明兩輪之間還有變異，仍未達目標（known-issues #15）。這是工作流的特徵：步驟是人寫的，模型負責照做。

### 5.3 自主度固定在最低

每一個會改畫面的要求都是「確認輪 → 按按鈕 → 動手輪」，連「鞋子換成白色」這種沒有歧義的小改也一樣。延遲中位數確認輪約 2.8–3.1 秒、動手輪約 6.1–7.9 秒（檢索時機實驗 §4、§6），一次小改要兩段等待加一次點擊。

這是 2026-10-05 使用者選的「盡量謹慎」（[先確認再動手設計](superpowers/specs/2026-10-05-confirm-before-act-design.md) §1、§2），不是疏忽。差距在於**沒有其他選項**：agent 專案通常展示「依風險決定要不要問」。

### 5.4 沒有長期記憶

`SessionStore` 用 `IMemoryCache`（滑動過期 120 分鐘），重啟即清空；`ChatHistory` 只留 20 輪（`HistoryTurns`）。沒有跨 session 的使用者偏好。`shared_prompt_histories` 是經同意寫入的共享知識庫，不是 agent 對這個使用者的記憶。

### 5.5 eval 靠人工

[`eval-cases.md`](eval-cases.md) 主表 27 條：7 條通過、1 條部分不符（#18）、3 條沒跑成（#21、#22 需要故障注入，#23 沒觸發）、16 條空白；而且多數紀錄早於 2026-10-05 的先確認再動手。功能驗收（S1–S7、C1–C10、Q1–Q3）紀錄很詳細，但同樣是人工跑、人工判讀。[`replay.py`](../manual-tests/replay.py) 已經能照劇本重播，缺的是自動判定與定期執行。agent 專案最常被問的就是「你怎麼知道它變好了」。

### 5.6 看不到成本與 trace

`audit_logs` 有 `prompt_tokens`／`completion_tokens` 欄位，但從來沒寫過（known-issues 歷史壓縮那一項的實測紀錄：「省了多少 token 看不到」）。一輪裡可能有好幾趟模型呼叫與工具呼叫（SK 架構說明 §3），現在只能從 audit 與 `Turn …`、`Gemini …` log 行拼湊，沒有一條 trace 串起來，也算不出每個 session 的成本。

### 5.7 工具關在 app 裡

沒有 MCP，知識庫（分維度檢索、facet 層級向量）無法被其他 agent 使用。`Llm:Provider` 只接了 Gemini，主規格 §4.8 預留的 OpenAI 路線沒有實作。

### 5.8 框架層級

[SK 架構說明](SK架構說明.md) §2 寫明刻意沒用 SK 的 Agent Framework。在 C# 職缺裡，「agent」關鍵字多半指 Microsoft Agent Framework（SK 與 AutoGen 的後繼；版本狀態以動工當時為準）。這是履歷關鍵字問題，不是能力問題，所以排最後。

---

## 6. 提案

### 6.1 P0 閉環：生成 → 看圖 → 修正

**目標**：讓「完成」變成可檢查的事——covered 的 facet 有出現在圖上。

**新工具**（`Plugins/` 新增一個 `RenderPlugin`）：

| 工具 | 參數 | 回傳 | 性質 |
| :--- | :--- | :--- | :--- |
| `RenderPreview` | `positive: string, negative: string` | `{ imageId, url }` | 可重複；只在動手輪；算進新的「每輪生圖次數」預算 |
| `CritiqueImage` | `imageId: string` | `{ facetId, pass, evidence, suggestedTags }[]`，只檢查 covered 的 facet | 可重複；只在 `RenderPreview` 之後 |

`CritiqueImage` 的「該檢查哪些 facet」由伺服器從 session 狀態導出，不讓模型自己列（同主規格 §4.2「`facetIds` 由伺服器導出」的原則）。

**動手輪流程**：草稿 tag → `RenderPreview` → `CritiqueImage` → 有沒過的 facet 就改 tag 再生 → `FinalizePrompt` 附上最後一張圖與自評結果。

**程式把關**（延續 §4.1「硬性限制由程式保證」）：

- `FinalizePrompt` 的定稿閘門加一條：生圖開著、還有 facet 沒過、而且生圖預算還有剩時，回錯誤字串讓模型繼續修（跟現在「`AskUser` 還在清單上而仍有缺時擋回」同一套手法）。
- `ToolBudgetFilter` 旁邊加生圖預算（例如每輪最多 3 張），用完就強制定稿，`tips` 列出沒過的 facet。
- 生圖是開關，跟知識庫開關一樣放在 TopBar：關掉時工具清單裡沒有這兩個工具，流程退回現在的樣子。這樣也自然得到一組對照實驗。

**安全**：生成的圖也要過輸出審查。`SafetyClassifier` 加一個看圖的分類（Gemini 多模態），沒過就整輪走 `OutputBlockedException` 的回滾路徑。

**前端**：儀表板 facet 多一種「圖上已驗證／沒出現」標記；定稿卡顯示預覽圖與自評結果；SSE 照現在的方式把 `RenderPreview`、`CritiqueImage` 推成工具卡。

**生圖後端**（要先拍板，§9）：

| 選項 | 方言相符（SD tag） | 需要 GPU | 成本 | 部署 |
| :--- | :--- | :--- | :--- | :--- |
| 本機 ComfyUI＋SDXL（docker compose 加一個服務） | ✅ | 要 | 只有電費 | 最重；`docker compose up` 一鍵跑起來會變難 |
| 託管的 SDXL API | ✅ | 不用 | 按張計費 | 中；多一把 API key |
| Gemini／Imagen 生圖 | ✗ 不吃 tag 語法，驗證的是替代品 | 不用 | 按張計費 | 最輕；沿用現有 key |

建議：託管的 SDXL API 為主，本機 ComfyUI 為選配（`compose` profile）。線上 ComfyUI API（Comfy API v2）的可行性評估見 [ComfyUI 整合可行性](ComfyUI整合可行性.md)：兩個選項可以共用同一個 client。

**資料**：預覽圖是暫存，不寫進知識庫、不進種子，session 過期一起清掉；跟 [資料來源](資料來源.md)「上游圖片一律不轉存」是兩回事，但 README 要寫清楚。

**驗收指標**：facet 通過率（最後一張圖）、平均修正次數、每輪生圖數、動手輪延遲中位數、每次定稿成本。生圖開／關各跑一組（P1 eval 的劇本）。

### 6.2 P1 自動化 eval

**目標**：每次改流程說明、工具或模型，都能在同一組劇本上比前後。

- 以 [`replay.py`](../manual-tests/replay.py)＋`replay_scenarios.json` 為基礎，把 `eval-cases.md` 主表與 C、Q 系列改寫成劇本。
- 每個劇本寫可程式判定的斷言：`final.kind`、工具軌跡（有沒有檢索、有沒有 `Protocol_Violation`、`Tool_Budget_Exhausted`）、facet 狀態、`tagOrigins` 比例。資料來源是 SSE 事件與 `audit_logs`，現有的 [`adoption_report.py`](../scripts/adoption_report.py) 已經會讀。
- 主觀品質（定稿是否完整反映描述、確認卡是否複述正確）用 LLM-as-judge 打分，評分準則寫進劇本；P0 完成後加上 `CritiqueImage` 的 facet 通過率。
- 報表：任務成功率、平均幾輪定稿、工具呼叫正確率、協定違規率、延遲、token／成本（要先做 §6.4）。
- GitHub Actions 新增一個 `schedule`＋`workflow_dispatch` 的 eval workflow，Gemini key 放 repository secret；**不跑在每個 PR 上**（要錢、而且結果有變異）。結果寫進 `docs/experiments/`，README 放最近一次的摘要表。
- #21、#22 的故障注入改用 fake connector 自動化（主規格 §12.3 本來就這樣描述），不必改 `appsettings`。

### 6.3 P1 自主程度切換＋流程說明改成目標導向

**不推翻 2026-10-05 的決定**：預設維持「每步確認」，新增兩個可切換的模式，放在 TopBar（跟知識庫開關同一排）。

| 模式 | 行為 |
| :--- | :--- |
| 每步確認（預設，現況） | 照 [先確認再動手設計](superpowers/specs/2026-10-05-confirm-before-act-design.md) |
| 只確認有歧義的 | 沒有衝突、沒有歧義、只增改的要求直接動手並定稿，畫面上給「復原」；下列情況仍先確認 |
| 全自動 | 除了下列情況，全部直接動手 |

**一律先確認**（程式判定，不交給模型）：換題材（會重置 facet）、說「隨便／你決定」（補齊內容要給使用者看）、模型在確認輪給了 `choices`（模型自己判斷有歧義）。

**做法**：

- 確認輪在非預設模式下多一個「直接套用」的出口。最簡單的形狀是讓 `Confirm` 回傳後，伺服器依模式與上面的規則決定是出卡片，還是立刻接一個動手輪（使用者看到的是一段連續的事件）。不做「預先算好、按對立即套用」，那一案的反對理由（多解讀無法預先算）仍然成立。
- 「復原」：`Session.Snapshot()` 已經有，把動手輪開始前的快照留一份給使用者，新增 `POST /undo`。
- 流程說明從編號步驟改成「目標＋完成條件＋限制」。硬性規則繼續放在 `ToolSetBuilder` 與工具閘門；known-issues #15 提的「動手輪沒查就退回」閘門（[檢索時機設計](superpowers/specs/2026-10-06-retrieval-timing-design.md) §6.4 做法 2）一起做，把檢索從「拜託模型查」變成「程式保證查過」。
- 改完用 §6.2 的 eval 確認沒有退步，三種模式各跑一組。

### 6.4 P2 token 用量與 OpenTelemetry

- **token**：Google connector 回應的 metadata 是 `GeminiMetadata`，有 `PromptTokenCount`、`CandidatesTokenCount`、`ThoughtsTokenCount`、`TotalTokenCount`。在 orchestrator 每次呼叫後累加到 `TurnContext`，寫進 `Turn_Completed` 那筆 audit 的 `prompt_tokens`／`completion_tokens`，修掉「欄位從沒寫過」；成本用組態的單價算，進每輪 log 行與 eval 報表。分類器呼叫也走同一個 chat client，一起算。
- **trace**：SK 以 AppContext 開關 `Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics`（或環境變數 `SEMANTICKERNEL_EXPERIMENTAL_GENAI_ENABLE_OTEL_DIAGNOSTICS`）發出 `gen_ai.*` 語意慣例的 span；Google connector 的 `GeminiChatCompletionClient` 有呼叫 `ModelDiagnostics.StartCompletionActivity`（SK main 分支，升級或動工時在 1.80.1 上確認）。加上 ASP.NET Core 與 Npgsql 的 instrumentation，一輪的 HTTP 請求、模型呼叫、工具呼叫、SQL 就在同一條 trace 上。
- docker compose 加 Aspire Dashboard（或 Jaeger）當 OTLP 接收端；`Sensitive` 開關（會記 prompt 內容）預設關。
- 文件：[SK 架構說明](SK架構說明.md) 照它的維護規則同一個 commit 更新。

### 6.5 P2 記憶

- **session 持久化**：`Session` 存進 PostgreSQL（`ChatHistory` 序列化成 JSON），`SessionStore` 改成快取＋資料庫。要處理 Gemini 訊息的特殊形狀（`GeminiChatMessageContent`、`thoughtSignature`，見 SK 架構說明 §5），序列化要有往返測試。
- **偏好記憶**：定稿時經同意（沿用 `RequestSaveConsent` 的確認卡形式）抽出偏好，例如「常用寫實攝影」「偏好 85mm」，存成這個使用者的記憶；下次放進 system prompt 的 Session 事實，並加一個 `RecallPreferences` 檢索工具。
- 沒有帳號時用匿名裝置 id（cookie），使用者可以清除。
- **這一項會碰到主規格 §2.2 的兩條「明確不做」**（使用者帳號與認證、Session 持久化），要先改規格再動工。

### 6.6 P2 MCP server

- 用 MCP 官方 C# SDK（NuGet `ModelContextProtocol`）把 `SearchPresets`、`SearchSimilarPrompts`（P0 完成後加 `RenderPreview`）包成 MCP server，跟現有 plugin 共用 repository 與檢索實作，不複製邏輯。
- 示範：在 Claude Desktop 或 Claude Code 裡直接查知識庫、拿片段。README 加一段設定方式。
- 只開唯讀工具；寫入共享庫不開放。

### 6.7 P3 視需要

- **自評拆成獨立 agent**：P0 的 `CritiqueImage` 改成一個有自己 system prompt、用多模態模型的 critic agent，跟寫提示詞的 agent 形成 evaluator–optimizer 兩個 agent。這是唯一值得拆的地方。
- **框架對應**：`IPromptOrchestrator` 已經是可替換的介面（`Orchestrator:Mode`）。若目標職缺明確要求 Microsoft Agent Framework，再加一個實作；**不建議重寫現有 orchestrator**。

---

## 7. 不建議做的

| 做法 | 為什麼不 |
| :--- | :--- |
| 拿掉工具清單限權或整輪回滾，換「更自由」的 agent | 這是專案最好的設計；自主程度用 §6.3 的模式調，不是拆控管 |
| 為了「多 agent」把檢索、撰寫拆成群組對話 | 動手輪中位數已經 6–8 秒，拆了只會更慢；沒有對應的品質收益 |
| 為打字機效果開 token 串流 | 終止型工具的參數要一次到位（SK 架構說明 §2 已寫明理由） |
| 把 eval 跑在每個 PR 上 | 要錢、結果有變異，會變成不穩定的紅燈；用排程與手動觸發 |

---

## 8. 建議順序與里程碑

先量測，再改行為：沒有 token 與 eval 的基準，P0、P1 改完說不出變好多少。

| 階段 | 內容 | 完成時拿得出來的證據 | 依賴 |
| :--- | :--- | :--- | :--- |
| 1 | §6.4 token 與 OTel | 一輪的完整 trace 截圖；每輪 log 有 token 與成本 | — |
| 2 | §6.2 自動化 eval | 第一份自動 eval 報表，當作基準 | 1（成本欄位） |
| 3 | §6.1 閉環 | 生圖開／關的 eval 對照：facet 通過率、成本、延遲 | 2、生圖後端拍板 |
| 4 | §6.3 自主程度＋目標導向流程說明＋檢索閘門 | 三種模式的 eval 對照；檢索率達標 | 2 |
| 5 | §6.6 MCP、§6.5 記憶 | Claude Desktop 查知識庫的示範；跨 session 偏好帶入 | §6.5 要先改主規格 §2.2 |
| 6 | README 改定位 | 見下 | 3 |

**README 改定位**：從「提示詞協作助理」改成「會自我驗證的生圖提示詞 agent：規劃 → 檢索 → 撰寫 → 生成 → 自評 → 修正，人只在需要時介入」。「技術對照」表旁邊加一張「agent 能力 → 實作 → 證據」對照表（§2.7 的 A–G 各一列，證據指向 eval 報表與 trace），並放最近一次 eval 的摘要。

---

## 9. 需要專案擁有者決定的事

1. **對照的定位**：一般的 AI agent 職缺，還是某個特定職缺描述？後者的話，§3 與優先序要依描述重排。
2. **生圖後端與預算**：§6.1 的三個選項選哪個；每月可接受的生圖與 eval 費用上限。
3. **主規格 §2.2 的「明確不做」要不要改**：「實際生成圖片」（P0 必須改）、「Session 持久化」與「使用者帳號」（§6.5 必須改）。
4. **自主程度的預設值**：本提案維持「每步確認」，要不要改。
5. **eval 的執行頻率**：每天、每週，或只手動觸發。

---

## 10. 對既有文件的影響（定案後才改）

| 文件 | 要改的地方 |
| :--- | :--- |
| [主規格](superpowers/specs/2026-09-21-genai-prompt-copilot-design.md) | §2.2 不做清單；§4.2 工具表加 P0 的工具；§4.3 工具清單規則加生圖與自主程度；§12.3 人工 eval 改指向自動 eval |
| [SK 架構說明](SK架構說明.md) | OTel、token 讀取、MCP 加進四層圖與第 4 節 |
| [README](../README.md) | 定位句、技術對照表、agent 能力對照表、生圖後端的設定與費用說明 |
| [known-issues](known-issues.md) | token 欄位修掉後移到「已修正」；#15 做了檢索閘門後一併處理 |
| [eval-cases](eval-cases.md) | 能自動化的案例標註對應劇本，人工只留需要看畫面的項目 |
