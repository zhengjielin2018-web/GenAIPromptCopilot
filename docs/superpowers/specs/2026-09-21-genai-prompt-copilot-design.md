# GenAI Prompt Copilot — 設計規格

日期：2026-09-21
狀態：已定案。子專案 1（資料地基）已實作並通過 §14 驗收（2026-09-22）；子專案 2（SK Agent 核心）已實作，以 `manual-tests/chat.py` 手動跑完對話迴圈（2026-09-24），降級檢查點結論見 §4.10；子專案 3（Nuxt 3 前端 + SSE）已實作並通過 §14 驗收（2026-09-24，瀏覽器，結果見 `docs/eval-cases.md`）；子專案 4（收尾與展示）打包驗收通過（2026-09-24：fresh clone 一鍵啟動、種子自 Release 匯入、CI 四個 job 綠，結果見 `docs/eval-cases.md`），瀏覽器展示驗收與截圖待 `docs/known-issues.md` 第 1、2 項修正後補；設計見 [2026-09-24-subproject-4-packaging-design.md](2026-09-24-subproject-4-packaging-design.md)
前身文件：[docs/初步想法.md](../../初步想法.md)（本文件取代其中的架構與流程章節；技術棧與階段藍圖以本文件為準）

---

## 1. 目標與定位

一個協助使用者精煉 AI 生圖提示詞的多輪對話助理。使用者用繁體中文描述需求，系統以六維度 facet 體系分析資訊充足度、主動追問缺失細節、從知識庫推薦可用片段，最後產出 SD/SDXL tag 風格的英文正／負向提示詞，並在使用者同意後沉澱為全域共享知識。

**專案目的：求職作品集。** 設計取捨一律以「每項技術都有可展示的實證」與「有一個能跑給人看的 demo」優先，功能深度其次。

展示重點：

- Semantic Kernel：全 agentic Function Calling 編排、Filters、RAG
- ASP.NET Core：SSE 事件串流、Session 狀態機
- PostgreSQL + pgvector：向量檢索 + GIN 陣列索引
- Python：分階段、可重跑的資料管線
- Nuxt 3：即時儀表板與 tool call 可視化

## 2. 範圍

### 2.1 做

依序四個子專案，每個都能獨立收尾（見 §14）：

1. 資料地基（pgvector + Python 管線）
2. SK Agent 核心（headless，Swagger 可驗證）
3. Nuxt 3 前端 + SSE
4. 收尾與展示（Docker Compose、README、CI 骨架）

### 2.2 明確不做

寫下來是為了讓它們是「決定不做」而不是「忘了做」：

- Azure 部署（保留 GitHub Actions 的 build + test workflow，不部署）
- 使用者帳號與認證
- Session 持久化（重啟即清空）
- i18n（UI 固定繁體中文）
- 實際生成圖片（只產 prompt）
- 多模態圖片反推（`image_url` 欄位保留但不實作）
- 對話歷史列表與管理
- Prompt 版本比較
- 多種生圖模型方言（固定 SD/SDXL tag 風格）
- DeepSeek 等其他 LLM provider（只做 Gemini 主、OpenAI 備）

## 3. 架構總覽

```text
┌──────────────┐  POST /messages (SSE)  ┌─────────────────────────────────────┐
│  Nuxt 3 SPA  │ ◄────────────────────► │  ASP.NET Core Web API               │
│  對話流       │                        │                                     │
│  六維度儀表板 │                        │  SafetyGuard (輸入側，service 層)    │
│  preset 抽屜  │                        │        │                            │
└──────────────┘                        │        ▼                            │
                                        │  AgenticOrchestrator                │
                                        │   ├ 工具清單組裝（依 session 狀態）  │
                                        │   ├ SK Kernel + FunctionChoice.Auto │
                                        │   │   Plugins: Knowledge/Dialog/    │
                                        │   │            Session              │
                                        │   │   Filters: TerminalTool /       │
                                        │   │     ToolBudget / OutputSafety / │
                                        │   │     Audit                       │
                                        │   └ Channel<AgentEvent> → SSE       │
                                        │        │                            │
                                        │  SessionStore (in-memory)           │
                                        └────────┼────────────────────────────┘
                                                 │ EF Core (讀寫)
                                                 ▼
                                   ┌──────────────────────────┐
                                   │ PostgreSQL 16 + pgvector │
                                   │  shared_prompt_histories │
                                   │  prompt_knowledge_presets│
                                   │  audit_logs              │
                                   └──────────────────────────┘
                                                 ▲
                                                 │ 批次寫入
                                   ┌──────────────────────────┐
                                   │ Python 管線（主機端 venv）│
                                   │ Civitai API → clean →    │
                                   │ structure → embed → load │
                                   └──────────────────────────┘
```

LLM：**`gemini-3.5-flash-lite`**（子專案 1 執行時實測定案。注意：`gemini-2.5-flash-lite` 仍會出現在 API 的 `models.list()` 中，但實際呼叫回 404「no longer available to new users」——列表裡有不等於可以用。採釘死版本而非 `gemini-flash-lite-latest` 別名，以維持 eval 結果可重現）。Embedding：**`gemini-embedding-001`，768 維**（實測可用）。

## 4. 核心編排（全 Agentic）

### 4.1 設計原則

流程決策（追問或定稿）交給 LLM 透過 Function Calling 自主決定。**但所有硬性限制由程式碼保證，不靠 prompt 約束**——做法是把「追問」與「定稿」都做成 tool，並依 session 狀態動態決定哪些 tool 存在。LLM 不需要「遵守」規則，因為違規的選項根本不在它的工具清單裡。

### 4.2 Plugins 與 Tools

| Plugin.Function | 參數 | 性質 |
| :--- | :--- | :--- |
| `KnowledgePlugin.SearchSimilarPrompts` | `intent: string, topK: int = 3` | 可重複；RAG 1，查 `shared_prompt_histories`，以 session profile 過濾 |
| `KnowledgePlugin.SearchPresets` | `queries: {dimension, query}[]`（≤ 12） | RAG 2，**分維度檢索** `prompt_knowledge_presets`：一次呼叫帶本輪所有要查的維度，每項一個維度專屬語句，同維度可重複（對比方向），見 §9 |
| `SessionPlugin.SetProfile` | `profile: portrait \| landscape \| object \| vehicle` | 可重複；設定題材 profile，重置 facet 狀態 |
| `SessionPlugin.SetFacetStates` | `updates: FacetStateEntry[]` | 可重複；第一輪先標使用者已描述的 facet 為 covered，再檢索；也用於 `waived` 與委託 note |
| `DialogPlugin.AskUser` | `preamble: string, asks: { dimension, question, missingFacetIds, options }[], facetStates: FacetStateEntry[]` | **終止型**；`asks` 1–3 則，每則 `options` 2–4 個 |
| `DialogPlugin.Discuss` | `message: string, facetStates: FacetStateEntry[], options: { label, tags, presetId? }[]? = null` | **終止型**；`options` 0–4 個參考方向，不帶 `missingFacetIds` |
| `DialogPlugin.FinalizePrompt` | `positivePrompt: string, negativePrompt: string, tips: string, intentSummary: string, facetStates: FacetStateEntry[]` | **終止型**；`AskUser` 還在清單上而仍有缺時擋回（定稿閘門，§4.6） |
| `DialogPlugin.RequestSaveConsent` | 無 | **終止型**；不碰 DB，只觸發前端確認卡片 |

`FacetState = covered | missing | waived | notApplicable`。

`facetStates` 在 `AskUser` / `Discuss` / `FinalizePrompt` 為必填，後端以此更新 session 並發 `dimensions` 事件。

`intentSummary`：繁中一句話的需求描述，存入 `LastFinal`、隨 `finalized` 事件送出，前端用它預填 `save-to-shared` 的 `intent`（子專案 3 設計 §2.2）。空白時回錯誤字串讓模型重試，跟 `positivePrompt` 同一套。

**`facetStates` 是陣列不是 dictionary。** `FacetStateEntry = { facetId: string, state: FacetState, note?: string }`，與 `SetFacetStates.updates` 同一個型別。SK 由 C# 型別產 function declaration 給 Gemini，`Dictionary<string, X>` 會變成「任意鍵的物件」——Gemini 的 schema 不支援開放鍵的 map，描述不出「鍵必須是 facet id」。陣列則能把 `facetId` 寫成具名欄位，順便讓 `note`（「使用者委託此項」）有地方放。

**`SearchPresets` 的每個項目只收 `dimension` 與 `query`，`facetIds` 與 `k` 由伺服器導出。** 維度 → facet 集合是 `facets.yaml` 加 session profile 的函式，LLM 傳進來只是多一個可被捏造的欄位（同 §9 的 `grounded` 原則）；`k` 則取決於該維度 grounded 與否（grounded 5、missing 3），也是伺服器才知道的事。

**`AskUser` 完整簽名**

```text
AskUser(
  preamble:    string,                                   // 一句開場
  asks:        [{                                        // 1–3 則
    dimension:       string,                             // style | scene | camera | appearance | pose | clothing
    question:        string,
    missingFacetIds: string[],
    options:         [{ label: string, tags: string, presetId: int? }]   // 2–4 個，必須不同方向
  }],
  facetStates: FacetStateEntry[]
)
```

**語意：索取。** 我需要你回答才能繼續。

| 旋鈕 | 值 | 理由 |
| :--- | :--- | :--- |
| `asks` 每次上限 | 3 個維度 | 3 × 2 次 = 6，剛好覆蓋六維度全缺的最壞情況 |
| `options` 每維度 | 2–4 個 | 必須不同方向（寫實／動漫），不是同方向的兩種說法 |
| `MaxAskCount` | 2 | 形狀修成多維度之後 2 次就夠 |

LLM 該先問哪三個維度由 system prompt 引導（風格是最大的槓桿，穿著通常最不影響畫面），不由程式碼決定。

**`Discuss` 完整簽名**

```text
Discuss(
  message:     string,                                   // 繁中回覆內容
  facetStates: FacetStateEntry[],                        // 必填
  options:     [{ label: string, tags: string, presetId: int? }]? = null   // 0–4 個參考方向
)
```

`options` 排在最後而且有預設值：SK 只看「有沒有預設值」決定必填與否（可為 null 不算）。沒有預設值時，Gemini 照描述省略它會丟 `KernelException`，白白吃掉一格 tool 預算。

**語意：回應。** 這是我對你問題的回答；你可以無視它繼續講別的。使用者發起的討論與提問走這裡，不消耗 `AskCount`（§4.3、§4.4）。

`Discuss` 不宣告需求，但必須同步事實——這是兩件事：

| | `missingFacetIds` | `facetStates` |
| :--- | :--- | :--- |
| 語意 | **宣告需求**：我需要這幾項才能繼續 | **同步事實**：目前每一項是什麼狀態 |
| `AskUser` | 有 | 有 |
| `Discuss` | **沒有** | **有** |
| 前端反應 | 該維度高亮 | chip 填色更新 |

`Discuss` 沒有前者。後者必須留，否則使用者在討論裡說「那就寫實」時，`Discuss` 是終止型、這一輪就結束了，儀表板會停在舊狀態。

**`options` 的形狀**

`AskUser.asks[].options` 與 `Discuss.options` 共用 `{ label: string, tags: string, presetId: int? }`：`label` 是給使用者看的繁中方向名稱，`tags` 是該方向對應的英文 tag 片段，`presetId` 指向 `prompt_knowledge_presets`。`presetId` 可為 null——LLM 可以提出知識庫裡沒有的方向，由 §6.2 的輸出側過濾兜住。前端對 `presetId` 非 null 的選項可點開 preset 抽屜（§11.1）。

**後端清洗（不信 LLM 自述）**

沿用單輪 demo `normalize_queries` / `validate_suggestions` 的精神：程式只修剪與過濾，不改語意，被拒的理由寫 audit。

`AskUser.asks`：

| 情況 | 處理 |
| :--- | :--- |
| `asks` 超過 3 則 | 只留前 3 |
| 某則 `options` 超過 4 個 | 只留前 4 |
| 某則 `options` 少於 2 個 | 該則移除 |
| `missingFacetIds` 含 session 現值非 `missing` 的 facet | 過濾掉 |
| `missingFacetIds` 含不屬於 `dimension` 的 facet | 過濾掉 |
| 過濾後某則 `missingFacetIds` 為空 | 該則移除 |
| 全部移除、`asks` 為空 | 不終止，回結構化錯誤要 LLM 重呼叫；計入 tool 預算（§4.6） |

`AskUser.asks[].options` 與 `Discuss.options`：

| 情況 | 處理 |
| :--- | :--- |
| `presetId` 非 null 但不在 `PresetLedger` | 降級為 null，記 audit |
| `Discuss.options` 超過 4 個 | 只留前 4 |

「不在 ledger 就降級」跟 demo 的 `validate_suggestions`（來源不在檢索結果 → 移除整個選項）不同：這裡降級不移除，因為 `presetId` 本來就可為 null。

`Discuss.facetStates` 在 `Profile == null` 時：忽略，不更新 session、不發 `dimensions` 事件，記 audit。

### 4.3 工具清單組裝規則

每次 agent loop 啟動前，由純函式 `ToolSetBuilder.Build(session, userMessage, guardResult)` 決定本輪註冊的工具：

```text
永遠註冊：  SearchSimilarPrompts, SearchPresets, SetProfile,
           SetFacetStates, FinalizePrompt

AskUser：            Status == Collecting
                 且  AskCount < MaxAskCount (2)
                 且  !guardResult.wantsAutoComplete

Discuss：            !guardResult.wantsAutoComplete
                 且 (Status == Finalized
                     或 DiscussStreak < MaxDiscussStreak (8))

RequestSaveConsent： Status == Finalized
```

兩個目標各由一個獨立機制保證，中間沒有耦合：`AskCount` 上限 2 管的是「LLM 不無限追問」，`Discuss` 管的是「使用者能繼續對話」，`Discuss` 不碰 `AskCount`。

`wantsAutoComplete` 命中的那一輪**同時移除 `AskUser` 與 `Discuss`**：「你決定」的語意就是「直接給我」，工具清單只剩 `FinalizePrompt` 與檢索／設定類，LLM 只能立即定稿。否則 LLM 會回一句「好的，我來幫你決定」就結束回合，使用者得再送一句才拿得到東西。

`Discuss` **不需要 `Profile`**。§4.6 對 `AskUser` / `FinalizePrompt` 在 `Profile == null` 時擋回要求先 `SetProfile`；`Discuss` 不受此限，使用者第一句就問「這個怎麼用？」時 LLM 要能直接回答。

捷徑指令（「隨便」「你看著辦」「幫我決定」「直接給我」等）**不用關鍵詞比對**——「鞋子隨便，但背景我要想一下」會被誤判。改由 §6.1 的輸入側分類器順帶回傳 `wantsAutoComplete: bool`（判斷整句是否要求系統直接補齊全部），不多花一次呼叫。命中即移除 `AskUser` 與 `Discuss`，並將 `Session.AutoFill` 設為 true（§5.4），與輪次上限走同一機制。針對單一 facet 的「鞋子隨便」則由 LLM 在對話中處理：該 facet 維持 `missing`，並透過 `SetFacetStates` 的 `note` 記下「使用者委託此項」，定稿時只補這一項。

**此函式是整個防死循環機制的核心，必須有單元測試覆蓋。**

### 4.4 Session 狀態機

```text
Session {
  Id, CreatedAt
  Status:        Collecting | Finalized
  Profile:       portrait | landscape | object | vehicle | null
  AskCount:      int        // LLM 未受邀請的主動打斷次數。上限 2，永不重設
  DiscussStreak: int        // 未定稿的 Discuss 連續次數。只由 FinalizePrompt 歸零
  AutoFill:      bool       // 使用者是否已委託系統補齊 missing（§5.4）
  FacetStates:   Dictionary<facetId, FacetState>
  ChatHistory:   SK ChatHistory
  PresetLedger:  Dictionary<presetId, LedgerEntry>
  LastFinal:     { Positive, Negative, Tips }?
  Lock:          SemaphoreSlim(1)
}
```

`PresetLedger` 是 §9 跨維度去重所需的 session 帳本，同時兼作對話記憶：

```text
LedgerEntry {
  Title, PromptSnippet, NegativeSnippet, FacetIds, ImageUrl
  Hits:      [(dimension, dist, grounded)]       // 去重歸屬用（§9）
  OfferedAs: [(turnIndex, dimension?, label)]    // 曾攤給使用者看過的選項
}
```

- 每次 `SearchPresets` 回來就寫入（append，不覆蓋既有 `Hits`）。
- `AskUser` / `Discuss` 成功後，其 `options` 中 `presetId` 非 null 者寫入該 preset 的 `OfferedAs`；`OfferedAs` 非空的 preset 會注入 system prompt（§4.9），使用者回頭引用時 LLM 拿得到 snippet，不用重撈或編造。
- 只收 presets。`SearchSimilarPrompts` 撈的是 `shared_prompt_histories`，id 空間不同，而且「相似作品」是參考不是選項，不會被回頭引用；它維持 §4.7 的壓縮規則，不進 ledger。
- 借用來源驗證與跨維度去重歸屬（§9）的範圍是**整個 ledger，不是本輪**。否則使用者聊了五輪之後定稿，前面撈到的東西全部失去借用資格，LLM 只能重撈或硬掰。

狀態轉移：

| 事件 | `AskCount` | `DiscussStreak` | `Status` |
| :--- | :--- | :--- | :--- |
| `AskUser` 成功 | +1 | 不變 | Collecting |
| `Discuss` 成功（Collecting） | 不變 | +1 | Collecting |
| `Discuss` 成功（Finalized） | 不變 | 不變 | Finalized |
| `FinalizePrompt` 成功 | 不變 | **歸零** | Finalized |
| `SetProfile` 切換 | 不變 | 不變 | 不變 |
| 任何 tool 被 `OutputSafetyFilter` 攔截 | 不變 | 不變 | 不變 |

- 建立 → `Collecting`。
- `SetProfile` 切換 profile → `FacetStates` 重置；**`AskCount` 與 `DiscussStreak` 不重置**（防死循環針對的是系統，不是限制使用者）。
- `DiscussStreak` 只由 `FinalizePrompt` 歸零，`AskUser` 不歸零。這讓 `Collecting` 期間的未定稿回合有一個好講的上限：**最多 8 次 `Discuss` + 2 次 `AskUser` = 10 輪**，之後工具清單只剩 `FinalizePrompt`，強制交出一版。跟 §4.6「tool 預算耗盡 → 強制定稿」同一個機制。
- `Finalized` 之後 `Discuss` 不受 streak 限制。護欄擋的是「一直不交東西」，不是「一直講話」；東西交出去了就沒有要保護的對象。
- `Finalized` 後使用者要求修改（「把背景改成黃昏」）→ 仍是 `Finalized`，LLM 直接重新 `FinalizePrompt`；`AskUser` 永久不可用。**純討論（「negative 裡的 `blurry` 是幹嘛的？」）走 `Discuss`，不出新定稿卡**；只有真的動到 prompt 才重新定稿（§4.6 會擋下「`Discuss` 卻改了 facet」）。
- 一個 session = 一個 prompt。「再來一張」由前端開新 session。

儲存：`IMemoryCache`，滑動過期 2 小時。不落 DB。

### 4.5 Filters

| Filter | 介面 | 職責 |
| :--- | :--- | :--- |
| `TerminalToolFilter` | `IAutoFunctionInvocationFilter` | 終止型 tool 成功（plugin 設了 `TurnContext.Outcome`）後設 `context.Terminate = true` |
| `ToolBudgetFilter` | `IAutoFunctionInvocationFilter` | 計數單輪 tool 呼叫，超過 `MaxToolCallsPerTurn`（預設 16）→ `Terminate`，觸發強制定稿（§4.6） |
| `OutputSafetyFilter` | `IAutoFunctionInvocationFilter` | 掛在 `FinalizePrompt` / `AskUser` / `Discuss`；檢查範圍見 §6.2，命中則設 `BlockedOutcome` + `Terminate`，由 orchestrator 回滾本輪（§4.6） |
| `AuditFilter` | `IAutoFunctionInvocationFilter` | 每次 tool 呼叫、每次攔截、token 與延遲寫入 `audit_logs`；也把本次的 `callId` 掛上 `TurnContext` 供 plugin 發 `tool_result`（§10.2） |

**四個都是 `IAutoFunctionInvocationFilter`。** `IFunctionInvocationFilter` 在 auto-invoke 路徑上拿不到這一輪的 `AutoFunctionInvocationContext`（`Terminate`、`RequestSequenceIndex`），而攔截要能停下整個迴圈，不只是讓一次呼叫失敗。

**攔截一律走 `Terminate` + outcome，不丟例外。** SK 的 auto-invoke 迴圈會把 filter 丟出的例外當成連線層失敗往上冒，重試層看不懂；改成在 `TurnContext` 上留下 `BlockedOutcome`、把 `context.Result` 換成一句錯誤字串，再由 orchestrator 在迴圈外判定要回滾還是繼續。

**擋回檢查在 plugin 裡，不在 filter 裡。** `Profile == null`、`asks` 清洗後為空、`Finalized` 下變更 facet、定稿閘門（§4.6）這四件事都是「這個 tool 的參數不合格」，plugin 直接回結構化錯誤字串、不設 outcome，迴圈自然繼續、也自然計入 tool 預算。filter 不需要知道每個 tool 的參數語意。

**`Finalized` 之下 `Discuss` 不得變更 facet 狀態。** 定稿後使用者說「風格改成動漫」，LLM 可能 `Discuss` 回「好的」並帶著改過的 `facetStates`，但沒有 `FinalizePrompt`——儀表板變了、定稿卡沒變。任何 facet 變動都意味著 prompt 該重組。程式碼保證：`Status == Finalized` 且 `Discuss.facetStates` 與**本輪開始時**的狀態不同 → 回結構化錯誤「facet 狀態有變更，請改用 `FinalizePrompt`」，計入 tool 預算。比的是本輪開始時而不是現值：`SetFacetStates` 每輪都在清單裡，先用它改掉再用 `Discuss` 回報同一組值，比現值就永遠相等，這道閘門等於不存在。

輸入側安全檢查（`SafetyGuard`）在 service 層、進 kernel 之前執行，不是 SK filter——因為 agentic chat completion 路徑不會觸發 `IPromptRenderFilter`。它本身也會失敗（上游攔截、分類器回不出 JSON），所以呼叫點在 orchestrator 的交易 `try` 之內，失敗走跟其他階段一樣的 `blocked`／`error` 事件與 audit。

### 4.6 失敗模式處理

| 情況 | 處理 |
| :--- | :--- |
| LLM 回純文字、未呼叫終止 tool | 補一則系統提示重試一次。仍為純文字：若 `Discuss` 本輪可用，包成 `Discuss`（`message` = 原文，`options` 留空，`facetStates` 用 session 現值原樣填回，`DiscussStreak++`）；否則發 `error` 事件。LLM 吐散文時想做的九成是講話，不是追問；包成追問會憑空生出追問氣泡與 chip，還燒掉一次 `AskCount`。`Finalized` 之後 `Discuss` 永遠可用，所以定稿後這條路徑不會掉到 `error` 分支 |
| `Profile` 為 null 時呼叫 `AskUser` / `FinalizePrompt` | `TerminalToolFilter` 不終止，改回傳結構化錯誤「請先呼叫 SetProfile」給 LLM，讓它補呼叫後再繼續；計入 tool 預算。`Discuss` 不受此限（§4.3） |
| `Finalized` 下 `Discuss` 帶了與 session 現值不同的 `facetStates` | 不終止，回結構化錯誤「facet 狀態有變更，請改用 `FinalizePrompt`」；計入 tool 預算（§4.5） |
| `AskUser.asks` 經 §4.2 清洗後為空 | 不終止，回結構化錯誤要 LLM 重呼叫；計入 tool 預算 |
| `FinalizePrompt` 時 `AskUser` 仍在清單上，且套用這次的 `facetStates` 後仍有 `missing`、又沒有委託 note 的 facet（定稿閘門） | 不終止、不定稿，回結構化錯誤要求先 `AskUser`（一次最多 3 個維度），列出缺的 facet id；計入 tool 預算。預算耗盡後的強制定稿（下一列）不受此限（`TurnContext.ForcedFinalize`）。`FinalizePrompt` 永遠在清單上，拿不掉，所以在呼叫時擋 |
| Tool 預算耗盡 | `Terminate` 後再跑一次強制定稿：只掛 `FinalizePrompt`，附「請立即以現有資訊定稿」提示 |
| LLM 呼叫失敗（傳輸／空回應／內容攔截） | 依下方三層規則內部重試；重試耗盡才算本輪失敗 |
| 單輪逾時（預設 120s） | `CancellationToken` 取消；退避等待吃同一個 token，不會出現「逾時了還在退避」 |
| **任何失敗**（逾時、取消、例外、上游攔截、輸出側攔截） | **session 回滾至本輪開始前**，發 `error` 或 `blocked` 事件 |
| 使用者斷線 | HTTP `RequestAborted` 傳入 agent loop 的 `CancellationToken`，立即停止 |
| 同 session 併發送訊息 | `Lock` 未取得 → 回 `409 Conflict` |
| Tool 執行例外（DB 掛掉等） | filter 捕捉，回傳結構化錯誤訊息給 LLM 讓它決定是否重試或直接定稿；寫 audit |

**一輪是一個交易。** 多輪對話下一個 session 可能累積十幾輪的狀態，任何一種失敗都不能把它毀掉：

```text
RunTurnAsync(session, text, ct):
  snapshot = session.Snapshot()     // AskCount, DiscussStreak, Status, Profile, AutoFill,
                                    // FacetStates, PresetLedger, ChatHistory.Count
  try:
    agent loop …
    終止型 tool 成功 → 交易成立，snapshot 丟棄
  catch (任何失敗，含 ct 取消):
    session.Restore(snapshot)
    發 error 或 blocked 事件
```

回滾之後 session 跟這一輪沒發生過一樣：使用者訊息不在 history、計數器沒動、ledger 沒多東西。不用逐項講哪個欄位不動，整個 session 都回去了。`PresetLedger` 也回滾——重試會重撈，代價是一次 embed 加幾條 SQL，換來「一輪 = 原子」這個好講的性質。`ChatHistory` 的 snapshot 是訊息數、restore 是截回該長度；其餘欄位淺複製，都很便宜。

**內部重試：三層**（分類邏輯放在一個 `IChatCompletionService` 的 decorator 裡，跟 connector 無關——§4.8 的 connector 還沒選，而 Google connector 與 OpenAI 相容端點回「被擋」的形狀不同）：

| 層 | 觸發 | 次數 |
| :--- | :--- | :--- |
| 傳輸 | 429／5xx／逾時 | **3 次**，退避 1s／2s／4s（Python 管線是 6 次；互動場景每輪有 3–4 次上游呼叫，6 次退避會吃掉整輪預算） |
| 空回應 | 200 但無可用內容、`MAX_TOKENS`、無 `blockReason` | **3 次** |
| 內容攔截 | §6.2 的那組 reason | **1 次** |

| 組態鍵 | 預設 |
| :--- | :--- |
| `Llm:TransportRetries` | 3 |
| `Llm:UnusableRetries` | 3 |
| `Llm:ContentBlockRetries` | 1 |
| `Orchestrator:TurnTimeoutSeconds` | 120 |

逾時由 60s 調為 **120s**：一輪有 1 次 embed 加 2–3 次 LLM 呼叫，每次最多重試 3 次加退避，60s 不夠。

**手動重試：不加端點、不分原因。** 內部重試耗盡或被攔截 → 回滾 → 發事件。session 已經回到本輪開始前，再送一次同一段文字跟第一次送完全等價：不需要 `/retry` 端點，也沒有「重試會不會重複計數」的問題——沒有東西可以被重複計。前端一律保留失敗訊息並附「重試」按鈕（§10.2、§11.1）。這跟 §6.2「內容攔截只重試一次」不衝突：那條管的是系統自動重送的上限，使用者自己決定再送是他的判斷，一次一則，每則都有 audit。

**Audit**：一輪失敗寫**一筆** `Turn_Failed`，`payload` 記 `{ stage, errorClass, attempts }`。每次內部重試不各寫一筆，會淹掉有用的紀錄。上游內容攔截另記 `Blocked_Upstream`（§6.2），不併進來。

### 4.7 Chat history 修剪與截斷

`Finalized` 之後 `Discuss` 不限次，history 沒有上限；而 `options` 改成結構化之後，call args 每輪都留在 history 裡，越積越肥。三條：

1. **tool result 壓縮**：每輪結束後，將本輪 tool result 訊息內容壓成摘要（`SearchPresets` → `{results: [{dimension, poolSize, hits: [{id, title}]}]}`；`SearchSimilarPrompts` → `[{id, intent 前 40 字}]`）。
2. **call args 也壓**：該輪結束後，`AskUser.asks[].options` 與 `Discuss.options` 壓成 `[{label, presetId}]`，去掉 `tags`。完整內容 ledger 有（§4.4）。
3. **整體截斷**：保留 system message + 最近 **10 輪**（一輪 = 一則 user message 起到終止型 tool 止），更早的丟掉。`PresetLedger`、`FacetStates`、`LastFinal` 是 session 事實，不靠 history 記住，所以丟掉是安全的。

### 4.8 Provider 抽象

兩條獨立的軸：

- **對話**：`Llm:Provider = Gemini | OpenAI`、`Llm:Model`、`Llm:ApiKey`、`Llm:Endpoint?`。DI 時依組態註冊對應 SK connector。
- **Embedding**：`Embedding:Provider`、`Embedding:Model`、`Embedding:Dimensions`。**換 embedding 模型 = 整個向量庫必須重算**，README 與組態檔明確標註；管線 `embed.py --reindex` 對應。

**Connector 選擇（子專案 2 已定案）：`Microsoft.SemanticKernel.Connectors.Google` 1.80.1-alpha。** 原本的計畫是先試「SK OpenAI connector 對 Gemini 的 OpenAI 相容端點」，共用一個 connector 就能把切換 provider 變成純組態。否決的理由是可觀測性：相容層不會把 `promptFeedback.blockReason` / `finishReason` 帶進 `ChatMessageContent.Metadata`，而上游內容攔截（§6.2）必須跟一般失敗分開記、分開回話——沒有那兩個欄位就只剩猜。Google connector 會把它們放進 `GeminiMetadata`，`Blocked_Upstream` 才有依據。代價是 OpenAI 要另外接一個 connector，`IChatCompletionService` 這層抽象仍在。

**`GeminiRoleFixHandler`（暫時的 workaround）。** 1.80.1-alpha 把 tool 回覆那一則 content 標成 `"role":"function"`，Gemini API 只收 user／model，直接回 400（`Role 'function' is not supported`）。第一趟往返（只有 user）永遠沒事，第二趟（帶 `functionResponse`）必炸——也就是只要模型呼叫了任何 tool，這一輪就結束不了。做法是插一個 `DelegatingHandler`，送出前把請求 JSON 裡的 `"role":"function"` 改成 `"role":"user"`。**移除條件**：connector 升版後，拿掉 handler 跑 `GeminiContractTests.Auto_invoke_survives_sending_a_tool_result_back` 仍綠，就刪掉整個檔案與 `Program.cs` 裡的注入。

### 4.9 System prompt

- 存於 `src/PromptCopilot.Api/Prompts/system.md`，不寫在 C# 字串裡
- 由 template + `facets.yaml`（依 session profile 篩選）+ session 既定事實（已 waived 的 facet、`AutoFill` 狀態、已定稿內容）組裝
- 組裝後的 prompt 取 SHA-256 前 12 碼寫入每筆 `audit_logs.prompt_version`，讓 eval 紀錄可對應 prompt 版本
- 兩條行為要求只能靠 prompt 約束，程式不擋（列入 §12.3 eval 觀察）：
  - **使用者在描述題材時不得用 `Discuss` 閒聊**，要推進流程（檢索、追問或定稿）。LLM 第一輪就 `Discuss` 是可能的，由 `DiscussStreak` 兜底，代價是多一輪
  - **`Finalized` 之後只要 facet 有變動就必須用 `FinalizePrompt`**，不能用 `Discuss` 帶過（違反時由 §4.5 擋回）

**先前提供過的選項注入**（來源是 `Session.PresetLedger` 的 `OfferedAs`，§4.4）：

```text
## 你先前提供過的選項（使用者可能回頭引用）
[T1] 風格 A. 寫實攝影風格 (preset 412) — photo realism, photorealistic
[T1] 風格 B. 日系動漫風格 (preset 88)  — anime, Anime art
[T3] 鏡頭 A. 低角度仰視   (preset 201) — low angle, from below
```

- 只帶 `OfferedAs` 非空的 preset，不帶全部檢索結果。使用者不會說「回到你第 17 個檢索結果」，只會說「回到你給我的那個厚塗油畫」。
- 按最近 offered 的 `turnIndex` 排序，取前 **24** 個 preset（3 維度 × 4 選項 × 2 次 `AskUser` 的最壞情況）。
- 一筆一行，token 成本可控。

### 4.10 降級路徑

定義 `IPromptOrchestrator`：

```csharp
interface IPromptOrchestrator {
    IAsyncEnumerable<AgentEvent> RunTurnAsync(Session s, string userMessage, CancellationToken ct);
}
```

兩個實作：`AgenticOrchestrator`（本設計）與 `StateMachineOrchestrator`（後端決定 ASK/DISCUSS/FINALIZE，LLM 只做分析、檢索與產文）。組態 `Orchestrator:Mode` 切換。API 契約與前端不變。

`StateMachineOrchestrator` 在子專案 2 只留介面與 `NotImplementedException` 空殼；**只有當子專案 2 驗收時 agentic loop 無法穩定跑完 §14 第 2 列的「追問 → 討論 → 回答 → 定稿 → 討論 → 修改；上游攔截後 session 可繼續」才實作**。

> **降級檢查點結論（2026-09-24）**：維持全 agentic，`StateMachineOrchestrator` 繼續留空殼不實作。子專案 2 的對話迴圈已能以 `manual-tests/chat.py` 手動跑通；後續若有問題，方向是調整 agentic 流程（工具清單規則、system prompt、filters），不是換編排方式。

## 5. 六維度與 Facet 體系

### 5.1 結構

六個維度，每個維度下掛一組 facet。**充足度判定與追問優先級皆由 LLM 於 runtime 判斷**，不做靜態的 critical/helpful 標註——哪個 facet 重要高度依題材而定，靜態標註反而不準。LLM 挑了哪些 facet 追問仍要可觀察，但不另開事件：寫在該輪 `Turn_Completed` 的 `payload` 裡（`askedFacetIds` = 這輪 `AskUser` 全部的 `missingFacetIds`，沒追問就不寫這個欄位；`waivedFacetIds` = 當下處於 `waived` 的 facet）。獨立事件要有自己的觸發時機與消費者才划算，這兩筆資料的問法都是「那一輪發生了什麼」，跟 `Turn_Completed` 同一個鍵。

### 5.2 `portrait` profile

| 維度 (key) | Facet id | 說明 |
| :--- | :--- | :--- |
| 風格 `style` | `style.genre` | 藝術流派／媒材 |
| | `style.reference` | 參照畫師或作品 |
| | `style.render` | 渲染引擎／技術風格詞（不含基礎畫質詞，見 §5.5） |
| | `style.palette` | 色調傾向 |
| 場景 `scene` | `scene.location` | 地點類型 |
| | `scene.foreground` | 前景元素 |
| | `scene.midground` | 中景／主體周邊 |
| | `scene.background` | 背景與遠景 |
| | `scene.lighting` | 光源與時間 |
| | `scene.weather` | 天氣氛圍 |
| 鏡頭 `camera` | `camera.shot` | 景別（特寫／半身／全身／遠景） |
| | `camera.angle` | 視角高度（俯／平／仰） |
| | `camera.focal` | 焦段 |
| | `camera.dof` | 景深 |
| | `camera.composition` | 構圖 |
| 人物樣貌 `appearance` | `appearance.age_gender` | 年齡與性別 |
| | `appearance.face` | 臉部特徵 |
| | `appearance.hair` | 髮型髮色 |
| | `appearance.body` | 膚色體型 |
| | `appearance.expression` | 表情 |
| 人物動作 `pose` | `pose.main` | 主要動作／姿勢 |
| | `pose.limbs` | 肢體細節（手部、頭部朝向） |
| | `pose.gaze` | 視線方向 |
| | `pose.interaction` | 與環境或道具的互動 |
| | `pose.motion` | 動態感 |
| 人物穿著 `clothing` | `clothing.head` | 頭部配件 |
| | `clothing.upper` | 上半身 |
| | `clothing.lower` | 下半身 |
| | `clothing.footwear` | 鞋履 |
| | `clothing.material` | 材質與磨損 |
| | `clothing.accessories` | 配件飾品 |

### 5.3 其他 profile

> **動物的歸屬（已定案）**：四值分類沒有「動物」這一格。子專案 1 實測 258 筆語料中，
> 一張「睡著的貓」被 LLM 標成 `portrait`（因為沒有更貼切的選項）。
> **決議：動物視為場景中的物件，歸入 `object` profile**，不新增第五個 profile。
> system prompt 需明確寫出這條規則，否則 LLM 會繼續回退到 `portrait`。
>
> 一個需要留意的副作用：`object` profile 目前把 `pose` 整個關掉，但動物**是有姿態的**
> （趴著、蜷曲、奔跑），關掉會損失資訊。建議子專案 2 實作時做一個最小調整——
> `object` profile 在主體是動物時保留 `pose` 維度（`pose.main`／`pose.gaze`／`pose.motion`
> 這三項對動物有意義，`pose.limbs`／`pose.interaction` 可留可不留）。
> 這不影響已入庫的語料，只影響 runtime 的追問行為。

| Profile | 差異 |
| :--- | :--- |
| `landscape` | `appearance` / `pose` / `clothing` 全部 `notApplicable`；`scene` 增加 `scene.season`（季節） |
| `object` | `appearance` 換成 `appearance.material`（材質與工藝）、`appearance.wear`（磨損與使用痕跡）、`appearance.scale`（尺度參照）；`pose` / `clothing` 為 `notApplicable`；顯示名稱「主體外觀」 |
| `vehicle` | 同 `object`，但 `pose` 換成 `pose.motion_state`（靜止／行進／漂移）、`pose.terrain`（與地形的互動）；顯示名稱「運動狀態」 |

### 5.4 Facet 四態

| 狀態 | 意義 | 追問 | 定稿時 |
| :--- | :--- | :--- | :--- |
| `covered` | 使用者已提供 | 否 | 寫入 prompt |
| `missing` | 尚未提供 | 可能 | **預設不寫入**，交給生圖模型；僅當 `Session.AutoFill = true` 時由 LLM 補齊 |
| `waived` | 使用者明示「不要指定」 | 否 | **不寫入**，即使 `AutoFill` 為 true 也不補 |
| `notApplicable` | profile 判定不適用 | 否 | 忽略 |

**發明細節的決定權在使用者。** 系統預設不替使用者補上他沒說的東西；只有使用者明說「隨便／你決定／你看著辦」（§6.1 分類器回傳 `wantsAutoComplete`）才把 `Session.AutoFill` 設為 true，此後定稿時 LLM 補齊所有 `missing`。`AutoFill` 一旦為 true 在該 session 內保持（使用者已委託）。**但使用者透過討論（`Discuss`，§4.2）把某一項變成 `covered` 或 `waived`，`AutoFill` 即管不到它**——`AutoFill` 補的是 `missing`，不在那個集合裡就不在補齊範圍內，`waived` 本來就是為「即使委託也不補」存在的。委託之後想回頭細談某一項，走討論即可，不需要讓 `AutoFill` 可逆。「不要指定 X」是 `waived`，永遠不補。System prompt 需明確區分這三種情況。

定稿卡片與儀表板會顯示哪些 facet 仍為 `missing`（「未指定，交由生圖模型」），讓使用者知道自己留了什麼空白。

### 5.5 Boilerplate 不屬於任何 facet

基礎畫質詞（如 `masterpiece, best quality, highly detailed`）與基礎負向詞（如 `lowres, bad anatomy, worst quality`）是 SD 風格 prompt 的固定配備，不是創作選擇，**永遠由 `FinalizePrompt` 生成，不受 facet 狀態影響**。

`style.render` 這個 facet 的語意因此收窄為「渲染引擎／技術風格詞」（`octane render`、`cel shading`、`film grain`），那才是使用者的選擇。

### 5.6 組態檔 `Configuration/facets.yaml`

```yaml
dimensions:
  - key: style
    label: 風格
    facets:
      - id: style.genre
        label: 藝術流派／媒材
        hint: 例如 anime, oil painting, photorealistic
      # ...
profiles:
  portrait:
    dimensions: [style, scene, camera, appearance, pose, clothing]
  landscape:
    dimensions: [style, scene, camera]
    overrides:
      scene:
        add: [scene.season]
  object:
    dimensions: [style, scene, camera, appearance]
    overrides:
      appearance:
        label: 主體外觀
        replace: [appearance.material, appearance.wear, appearance.scale]
  vehicle:
    # ...
```

前端由 `GET /api/config/facets` 取得同一份定義渲染儀表板，不在前端重複定義。

## 6. 安全合規

### 6.1 輸入側 `SafetyGuard`（service 層）

1. 快速路徑：可組態 denylist（NSFW 關鍵詞 + 少量高頻名人），命中直接擋。
2. 分類路徑：一次 Gemini Flash 結構化呼叫，回傳 `{ nsfw: bool, realPerson: bool, personName?: string, wantsAutoComplete: bool, reason: string }`。`nsfw` 或 `realPerson` 為 true 即擋；`wantsAutoComplete` 交給 §4.3 的工具清單組裝。
3. 命中 → 回 `blocked` 事件、寫 `audit_logs`（`Blocked_NSFW` / `Blocked_Celebrity`）、本輪不進 kernel、不計 `AskCount`。

### 6.2 輸出側 `OutputSafetyFilter`

**檢查範圍是全部對外輸出面**，不只定稿：

| Tool | 檢查欄位 |
| :--- | :--- |
| `FinalizePrompt` | `positivePrompt`、`tips`、`intentSummary` |
| `Discuss` | `message`、`options[].label`、`options[].tags` |
| `AskUser` | `preamble`、`asks[].question`、`asks[].options[].label`、`asks[].options[].tags` |

**是這張表，不是「把全部參數串起來」。** `negativePrompt` 與 `facetStates` 不在表內，而且不能在：SD 的負向詞常態就是 `nsfw, nude, naked`——那是排除清單，把它餵給分類器等於要它攔我們自己的排除詞，每一次定稿都會被自己擋下來。`facetStates` 則是機器狀態，沒有人會看到。

**純文字補救那條路要自己檢一次。** §4.6 的「兩次都回純文字就包成 `Discuss`」是在 kernel 外面直接呼叫 plugin 的，filter 不會跑；但包出來的 `message` 一樣會送到使用者眼前，所以包之前先跑同一個分類器，命中就走一樣的 `Blocked_Output` 回滾。

理由是「防止無害輸入配上 preset 組出不當內容」對 `options` 一字不差地成立：`options` 直接來自 `prompt_knowledge_presets`，走的是跟定稿一模一樣的來源；輸入側擋不到（輸入無害），出口不一致難講。代價是每個討論回合多一次 Gemini Flash 分類呼叫；討論回合本來就不跑六次 `SearchPresets`，延遲在這種輪次裡幾乎看不出來。

**命中時**：不進 `Finalized` 狀態、發 `blocked` 事件、`Terminate`、session 回滾至本輪開始前（§4.6，涵蓋「任何計數器都不動」）、寫 audit（`Blocked_Output`）。跟輸入側「不計 `AskCount`」對稱。

**上游內容攔截（`Blocked_Upstream`）是另一回事。** Gemini 有可能對某些輸入**完全不回應**：HTTP 200，但 `candidates` 是 `null`、`promptFeedback.blockReason` 有值、`safetyRatings` 與 `blockReasonMessage` 都是 `null`。這跟 `OutputSafetyFilter` 是兩回事——那是我們檢查 LLM 的輸出，這是 LLM 根本拒絕產出。

實測（2026-09-22，用子專案 1 的 `demo.py`，語料庫 19,354 筆 presets，每格 3 次）：

| 查詢 | 被擋 |
| :--- | ---: |
| 少女＋熱褲 | 3/6 |
| 成年女性＋熱褲 | 2/6 |
| 少女＋泳裝 | 2/3 |
| 成年女性＋泳裝 | 0/3 |
| 少女／男性／風景／載具，一般服裝 | 0/18 |

三個結論：觸發因子是**暴露性服裝**不是年齡用詞（加「成年」有幫助但不消除）；攔截發生在**組裝**階段（送進去的 prompt 含檢索到的候選片段，分析階段從沒被擋過）；是**機率性**的，同一份輸入重送有時會過。

判定與處置（C# client 層的 decorator，§4.6）：

| 情況 | 判定 | 處置 |
| :--- | :--- | :--- |
| `blockReason` 或 `finishReason` 屬 `PROHIBITED_CONTENT`／`SAFETY`／`BLOCKLIST`／`JAILBREAK`／`IMAGE_SAFETY`／`MODEL_ARMOR` | 內容攔截 | **重試 1 次**，仍被擋才交給使用者 |
| `finishReason = MAX_TOKENS`；或空 `candidates` 且無 `blockReason` | 與內容無關 | 重試，預設 3 次 |
| 429／5xx／逾時 | 傳輸 | 既有的指數退避重試，預設 3 次 |

**內容攔截重試一次，不多。** 攔截是機率性的，實測同一份 SFW 內容也會被誤擋，重送一次常會過；送到 Gemini 的內容已經先過了 §6.1 的 `SafetyGuard`——是我們自己判定可接受的東西，上游攔截是第二個分類器的第二次意見，容忍它一次誤判是合理的。但只能一次：對同一份輸入重送到通過為止，等於利用分類器的不確定性規避安全判定，而實測顯示這裡的判定牽涉未成年與暴露服裝的組合——這條界線兩次仍被擋就交給使用者，由人決定要不要改寫。`safetySettings` 也不是出口：`PROHIBITED_CONTENT` 不在可調的 `HarmCategory` 之列。

**攔截時的對話行為**：比照上面的命中處理——發 `blocked` 事件、`Terminate`、session 回滾至本輪開始前（§4.6）。回滾多做一件事：被擋的那句話不留在 history，否則下一輪 LLM 會看到它、可能再觸發一次。使用者不該因為上游攔截損失輪次。訊息要說清楚三件事：這是上游模型的判定**不是程式錯誤**、**不是知識庫的問題**、**下一步在使用者手上**。

**Audit**：`event_type = 'Blocked_Upstream'`，`payload` 記 `blockReason` 與發生階段。必須與 `Blocked_NSFW`（我們自己擋的）**分開**——兩者混在一起會讓「合規」指標失真：一個是我們的防線生效，一個是我們把上游不接受的東西送出去了。

參考實作：`scripts/pipeline/gemini_client.py` 的 `UnusableResponse.is_content_block` 與 `generate_structured` 的重試條件（`CONTENT_BLOCK_ATTEMPTS = 1`）；使用者訊息見 `scripts/demo.py::unusable_message`。

### 6.3 資料側

Python 管線在 `clean.py` 階段過濾 NSFW（Civitai API `nsfw=None` 參數 + `nsfwLevel` 檢查 + 自建關鍵詞清單，三層過濾）。知識庫本身乾淨是整個「合規」賣點的前提。

> **子專案 1 實測結果（重要，會影響本節的可信度）**：抓下來的 20 筆原始資料**全部**是
> `nsfwLevel: "None"` 且 `nsfw: false`，包含一筆內容為
> `cleavage, extremely sexy, seductive` 的 prompt。也就是說**前兩層實際上什麼都沒擋掉**，
> 第三層的關鍵詞清單是唯一真正在運作的過濾。清單已擴充性暗示形容詞並改為標點正規化比對，
> 實測 859 筆 preset 殘留 0 筆，但**固定的英文關鍵詞清單本質上擋不住換句話說、其他語言或新詞**。
> 因此 §6.1／§6.2 的 runtime LLM 分類器才是真正的防線；管線這層只是語料衛生，不是保證。
>
> **決議：管線這層維持現狀，不再加強。** 已知它擋不住換句話說與其他語言，這是明知並接受的
> 取捨，不是疏漏。合規賣點的實質內容在 runtime 那兩層，demo 時應該這樣講。

## 7. 資料模型

`db/init/001_schema.sql` 為 schema 單一真實來源，由 docker-compose 於首次啟動執行。**不用 EF Core Migration**——Python 與 C# 共用資料庫，且 pgvector 型別與 HNSW 索引用 migration 表達不自然。

**讀寫也不用 EF Core：直接 Npgsql + Pgvector 寫原生 SQL**（`NpgsqlDataSourceBuilder.UseVector()`）。全部的查詢就是三張表各一到兩句、都帶向量運算子（`<=>`）與陣列運算子（`&&`），EF 對這兩者都要走 raw SQL 或第三方擴充，等於為了一層映射多一層翻譯。`Data/` 底下因此是 `PresetRepository`／`HistoryRepository`／`AuditRepository` 三個 repository，沒有 `DbContext`、沒有 entity 類別。

向量維度 `<DIM>` = **768**，模型 `gemini-embedding-001`（stable、支援逐筆批次與 `task_type`；`gemini-embedding-2` 仍為 preview 且多輸入會合併成單一向量，不採用）。與 `.env` 的 `EMBEDDING_DIMENSIONS` 及 `appsettings` 的 `Embedding:Dimensions` 保持一致。

```sql
CREATE EXTENSION IF NOT EXISTS vector;

-- 使用者沉澱 + 管線匯入的完整 prompt；RAG 1 來源
CREATE TABLE shared_prompt_histories (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_ref          VARCHAR(64) UNIQUE,       -- 'civitai:<imageId>'，供管線 upsert；使用者紀錄為 NULL
    user_intent         TEXT NOT NULL,            -- 繁中原始需求（匯入資料由 LLM 生成）
    positive_prompt     TEXT NOT NULL,
    negative_prompt     TEXT NOT NULL,
    subject_profile     VARCHAR(20) NOT NULL,     -- portrait | landscape | object | vehicle
    source              VARCHAR(20) NOT NULL,     -- user | civitai
    image_url           TEXT,
    completeness_scores JSONB,                    -- 六維度 + facet 四態快照
    intent_embedding    VECTOR(768),
    created_at          TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX idx_shared_intent_embedding ON shared_prompt_histories
    USING hnsw (intent_embedding vector_cosine_ops);
CREATE INDEX idx_shared_profile ON shared_prompt_histories (subject_profile);

-- 知識包片段；RAG 2 來源
CREATE TABLE prompt_knowledge_presets (
    id               BIGSERIAL PRIMARY KEY,
    source_ref       VARCHAR(64) UNIQUE,          -- 'civitai:<imageId>:<idx>'，供管線 upsert
    title            VARCHAR(100) NOT NULL,
    category         VARCHAR(50) NOT NULL,        -- Style | Scene | Camera | Appearance | Pose | Clothing | Combined
    description      TEXT NOT NULL,               -- 繁中模糊敘述（LLM 生成，供語意檢索）
    tags             TEXT[] NOT NULL,
    facet_ids        TEXT[] NOT NULL,             -- 對應 facets.yaml 的 id
    prompt_snippet   TEXT NOT NULL,               -- 英文正向片段
    negative_snippet TEXT,                        -- 英文負向片段（可空）
    image_url        TEXT,
    preset_embedding VECTOR(768),
    created_at       TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX idx_presets_tags      ON prompt_knowledge_presets USING gin (tags);
CREATE INDEX idx_presets_facet_ids ON prompt_knowledge_presets USING gin (facet_ids);
CREATE INDEX idx_presets_embedding ON prompt_knowledge_presets
    USING hnsw (preset_embedding vector_cosine_ops);

-- 稽核
CREATE TABLE audit_logs (
    id                BIGSERIAL PRIMARY KEY,
    session_id        VARCHAR(64),
    turn_index        INT,
    event_type        VARCHAR(50) NOT NULL,
    prompt_version    VARCHAR(12),                -- system prompt hash
    raw_input         TEXT,
    payload           JSONB,                      -- tool 名稱、參數摘要、挑選的 facet 等
    prompt_tokens     INT,
    completion_tokens INT,
    latency_ms        INT,
    created_at        TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX idx_audit_session ON audit_logs (session_id, created_at);
```

`event_type` 值：`Blocked_NSFW`（denylist 命中時 `payload` 記 `{ term }`——命中的詞只進這裡，不回給使用者）、`Blocked_Celebrity`、`Blocked_Output`、`Blocked_Upstream`（上游模型拒絕產出，§6.2；與 `Blocked_NSFW` 分開記，`payload` 記 `{ reason, stage, attempts? }`）、`Tool_Invoked`、`Tool_Budget_Exhausted`、`Protocol_Violation`、`Turn_Failed`（一輪失敗一筆，`payload` 記 `{ stage, errorClass, message, attempts? }`，§4.6）、`Saved_To_Shared`（`/save-to-shared` 寫入成功）、`Turn_Completed`（含 token 與延遲，`payload` 另記 `outcome`、`toolCalls`、`rejections`、`askedFacetIds?`、`waivedFacetIds`，§5.1）。

`attempts` 只有在例外是由重試層（§4.6）包出來、知道自己實際打了幾次時才寫；不知道就不寫，不硬塞 0。

原草稿還列了 `Turn_Budget_Exhausted`、`Facet_Waived`、`Ask_Facets_Selected` 三個，**不實作**：`AskCount` 用盡的表現是工具從清單裡消失（§4.3），不是一個事件；後兩者的資料都在 `Turn_Completed` 的 `payload` 裡（§5.1）。

## 8. Python 管線

主機端 venv 執行（Python 3.12+），金鑰由 `.env` 讀取。分階段落地，每階段可獨立重跑：

```text
scripts/
  pipeline/
    fetch_civitai.py   # 分頁拉取 Civitai /api/v1/images (nsfw=false)；限流；cursor 斷點續傳
                       #   → data/raw/*.jsonl
    clean.py           # prompt 正規化 + hash 去重；NSFW 關鍵詞二次過濾；長度與語言過濾
                       #   → data/clean/*.jsonl
    structure.py       # Gemini 批次結構化：每筆原始 prompt →
                       #   (a) 一筆完整紀錄 {user_intent(繁中), positive, negative, subject_profile}
                       #   (b) N 筆片段 {title, category, description(繁中), tags, facet_ids,
                       #                 prompt_snippet, negative_snippet}
                       #   → data/structured/{histories,presets}.jsonl
    embed.py           # 批次向量化；記錄已處理 id；--reindex 全部重算
    load.py            # upsert 進兩張表
  seed_data.py         # 串起全部；各階段可 --from <stage> 起跑
  data/{raw,clean,structured}/   # git ignore
```

設計要點：

- **每階段 jsonl 落地**，可單獨檢視與重跑。
- **斷點續傳與限流**：Civitai 與 Gemini 免費層皆有 rate limit；token bucket + 指數退避；fetch 記 cursor、embed 記已處理 id。
- **`structure.py` 同時產兩種輸出**：完整紀錄餵 RAG 1（讓全新資料庫的 `SearchSimilarPrompts` 也有東西可撈），片段餵 RAG 2。
- **NSFW 在此過濾**（§6.3）。
- 目標資料量：拉取數千筆，清洗後預期數百筆完整紀錄、千筆左右片段。

## 9. 檢索策略

| Tool | 策略 | SQL 概念 |
| :--- | :--- | :--- |
| `SearchPresets` | **分維度**：一次呼叫帶多個維度的查詢，每項用該維度專屬的查詢語句，`facetIds` 依維度過濾後向量排序 | `WHERE facet_ids && $facetIdsOfDimension ORDER BY preset_embedding <=> $dimensionQueryVec LIMIT k` |
| `SearchSimilarPrompts` | 向量 Top-K + profile 過濾 | `WHERE subject_profile = $1 ORDER BY intent_embedding <=> $2 LIMIT k` |

**GIN 過濾本身不夠。** 實測（2026-09-22）：同樣過濾到 Style 候選池，用使用者整句描述的向量排序撈回無關片段（dist 0.354），用該維度專屬的查詢語句撈回正確風格（0.229–0.234）。單一整句向量是六維度的模糊平均，只會貼近最泛用的片段，且使用者沒提到的維度永遠撈不到。因此 agent 呼叫 `SearchPresets` 時：

- 一次呼叫帶本輪所有要查的維度（`queries[]`，每項一個維度），`facetIds` 由伺服器依 profile 導出。
- `query` 是該維度的專屬語句：使用者已描述的維度用其原話；未描述的維度由 agent 依整體畫面推想，且應給對比方向（例：寫實攝影 vs 動漫插畫）在同一次呼叫裡放兩個項目。
- 使用者未描述的維度撈到的片段只可用於追問與建議，不得直接寫入提示詞。
- 不設距離門檻；tool result 帶相似度分級（`<0.25` 高、`<0.30` 中、其餘低），由 agent 判斷。
- `tags && $2` 決定不做：OR 上 tags 會把候選池撐到維度外，與分維度前提衝突。
- **跨維度去重：歸屬規則為「grounded 優先、距離次之」，且在定稿時才算，不在檢索時算。** 一筆 preset 的 `facet_ids` 可能橫跨數維（實測 859 筆裡 134 筆、15.6%），會在多個候選池出現，agent 可能從兩個維度分別拿到兩份結果、兩個不同的 `grounded` 值。規則：若有任何 grounded 維度撈到它，歸給這些維度中距離最小的那個；完全沒有才退回全體最小距離。**不能單純比距離**——歸屬決定借用資格，而 `grounded` 是「這個維度使用者講了沒」的屬性，不是片段的屬性；單純比距離會讓一個 grounded 維度正當撈到的片段，因為某個 missing 維度**推想出來**的查詢剛好更近，就被降級成「僅供建議」而失去借用資格。
- **落地方式：session ledger。** `SearchPresets` 每個項目照實回傳自己維度的結果，不做跨維度去重。session 內維護 `presetId → [(dimension, dist, grounded)]`，每次呼叫累加；歸屬與借用資格到定稿驗證時才套上面的規則。這本 ledger 本來就非有不可——借用來源的驗證（借的 tag 必須真的在片段裡、且真的在提示詞裡，不信 LLM 自述）查的是同一本帳。附帶好處：agent 只呼叫部分維度時自然成立，而且能回報「這片段你在某維度看過了」。
- **`grounded` 由伺服器算，不是 LLM 傳進來的參數。** agent 傳 `facetIds`，伺服器映射到維度後查 `Session.FacetStates`（§4.4）自行判定。與 Python `grounded_dimensions()` 不問 LLM 同一個原則：少一個可被捏造的欄位。
- **候選池大小要跟著 tool result 一起回。** `池 2 → 2` 這種「這維度過濾後有多少候選、其中命中幾筆」的資訊，是分維度檢索最有價值的副產品：它讓知識庫覆蓋缺口（例如 vehicle 的 pose 只有 2 筆）在使用當下就看得見，不必事後查資料庫才發現。tool result 除了相似度分級，也要帶上 GIN 過濾後的候選池筆數，否則子專案 2 會失去這個可見度。

查詢向量於 runtime 以同一 embedding 模型計算；多個維度的查詢語句合併為單次 `embed_batch` 呼叫（2026-09-24 起 C# 端也是：批次簽名的緣由見 [批次 SearchPresets 設計](2026-09-24-batch-search-presets-design.md)）。

**embedding 不走 SK 抽象，直接打 REST `:batchEmbedContents`。** 要保證的是三件事跟 Python 管線逐字一致：`taskType`（查詢用 `RETRIEVAL_QUERY`、入庫用 `RETRIEVAL_DOCUMENT`）、`outputDimensionality: 768`、回來之後自己做 L2 正規化（`gemini-embedding-001` 在非預設維度下不保證單位長度，而 `vector_cosine_ops` 的距離只有在單位向量上才跟管線算出來的值可比）。SK 的 embedding 抽象當時蓋不到 `taskType` 與 `outputDimensionality`，包一層反而要繞過它。`Llm/GeminiEmbeddingClient` 因此是一個 `HttpClient` 的薄封裝，介面 `IEmbeddingClient` 只有 `EmbedAsync(texts, taskType, ct)`。

完整推導與 Python 參考實作見 [2026-09-22-dimension-scoped-retrieval-design.md](2026-09-22-dimension-scoped-retrieval-design.md)。

## 10. API 與 SSE 協定

### 10.1 端點

| 方法 | 路徑 | 說明 |
| :--- | :--- | :--- |
| `POST` | `/api/sessions` | 建立 session → `{ sessionId }` |
| `GET` | `/api/sessions/{id}` | session 目前的權威狀態（status、profile、facetStates、askCount／askLimit、lastFinal）；前端重載重建用；不拿 session 鎖；`404` 表示不存在或已過期 |
| `POST` | `/api/sessions/{id}/messages` | body `{ text }`；回 `text/event-stream`；同一 session 已有一輪在跑 → `409` |
| `POST` | `/api/sessions/{id}/save-to-shared` | body `{ intent }`；需 `Finalized`，否則 `409`；寫 `shared_prompt_histories` 並向量化；**唯一的寫入路徑** |
| `GET` | `/api/config/facets` | 回 `facets.yaml` 內容供前端渲染 |
| `GET` | `/api/presets/{id}` | preset 詳情（抽屜用）；含 `sourceRef` 與伺服器算的 `sourceUrl`（出處連結，子專案 4） |
| `GET` | `/health` | |

`save-to-shared` 的 `intent` 是使用者這次需求的整句繁中原話（會被向量化成 `intent_embedding`，即 RAG 1 的檢索鍵），不是定稿的英文提示詞；定稿內容從 session 的 `LastFinal` 取，不由客戶端送。

`save-to-shared` 與 `/messages` **共用同一把 session 鎖**：它要讀 `FacetStates` 與 `LastFinal`，那一輪還在跑（而且隨時可能被回滾）時讀到的是半途的狀態，所以拿不到鎖一樣回 `409`。資料列寫進去之後的稽核寫入失敗不影響回應——否則使用者一重試就多一筆重複的共享紀錄。

前端以 `fetch` + `ReadableStream` 消費 SSE（`EventSource` 不支援 POST）。

### 10.2 SSE 事件

| `event:` | `data:` | 前端反應 |
| :--- | :--- | :--- |
| `session` | `{ sessionId, turnIndex, status }` | 初始化 |
| `tool_call` | `{ callId, name, argsSummary }` | 對話流插入行內卡片 |
| `tool_result` | `{ callId, name, summary, presets?: [{id, title, imageUrl}] }` | 展開卡片；餵抽屜。`callId` **等於**對應 `tool_call` 的 `callId`（同一次呼叫的兩個事件），前端據此配對 |
| `dimensions` | `{ profile, facetStates: {facetId: state} }` | 儀表板更新 |
| `token` | `{ text }` | 接到最近一則討論訊息後面。**後端現況不發**（回覆內容都是終止型 tool 的參數，一次到位）；前端 reducer 保留處理，但不對一次到位的文字做假的逐字動畫（子專案 3 設計 §1.2） |
| `final` | 四種 `kind`，見下 | 追問卡／對話氣泡／定稿卡片／高亮入庫按鈕 |
| `blocked` | `{ reason, message }` | 標記原因，**保留失敗的訊息並附「重試」按鈕；按下把原文填回輸入框**，使用者可改可直接送 |
| `error` | `{ code, message }` | 同上（不加 `retryable` 欄位——session 已回滾，重送等價首次送出，§4.6） |

`final` 的四種 `kind`：

```text
{ kind: "ask",       preamble, asks: [{ dimension, question, missingFacetIds, options }] }
{ kind: "message",   message, options? }
{ kind: "finalized", positive, negative, tips, intentSummary }
{ kind: "save_consent_requested" }
```

`options` 的每筆是 `{ label, tags, presetId? }`（§4.2）。`dimensions` 事件不變，`Discuss` 一樣會發（帶 `facetStates`）。

`Discuss.message` 不會有打字機效果：它是 tool call 的參數，一次到位。這跟 `AskUser` / `FinalizePrompt` 現況一致，不是新問題；前端不要對 `message` 期待 `token` 事件。

### 10.3 串流實作

`IAutoFunctionInvocationFilter` 內無法存取 HTTP response。做法：每個請求建立 scoped `Channel<AgentEvent>`，filters 與 orchestrator 往 channel 推事件，端點以 `IAsyncEnumerable<AgentEvent>` 讀出並寫 SSE。**這是前後端串接最容易卡住的點，實作計畫中列為獨立任務。**

## 11. 前端（Nuxt 3，`ssr: false`）

關閉 SSR：單頁、無 SEO 需求，SSE + 客戶端狀態在 SSR 下的 hydration 問題不值得處理。

### 11.1 版面

- 左側主區：對話流。tool call 以輕量行內卡片呈現（「🔍 查詢知識庫：鏡頭 · 景別 → 找到 5 筆」），完成後自動摺疊，可點開。
- 右側 sticky 側欄：六維度儀表板。
- 定稿卡片出現在對話流內（不用 modal），含正／負向 prompt、複製按鈕、生成建議、「儲存至共享知識庫」按鈕。
- preset 預覽抽屜從右側滑出，覆蓋儀表板；顯示 `image_url`、snippet、tags。
- `kind: "ask"` 渲染成一張**多維度追問卡**：`preamble` 在最上，每則 ask 一區（維度標題 + `question` + 一排 `options` chip），該維度在儀表板高亮。
- `kind: "message"` 渲染成一般對話氣泡；`options` 若有，渲染成比追問 chip 更輕的「參考方向」列表，儀表板**不**高亮。
- chip 點選是**填入輸入框可累積**，不是點了就送；否則三個維度要送三次。
- chip 送出時帶維度前綴：`[風格] 寫實攝影`，讓 LLM 能對回 `asks` 的哪一則。
- `options[].presetId` 非 null 的選項可點開 preset 抽屜。
- 任何 `error` / `blocked`：失敗的訊息保留顯示並標記原因，附「重試」按鈕；按下把原文填回輸入框，使用者可改可直接送（§4.6）。
- 頂部「新對話」按鈕。

### 11.2 儀表板

每個維度一列：維度名稱 + 一排 facet chip。四態以填色與視覺重量區分（不只靠顏色）：

- `covered`：實心、飽和
- `missing`：空心描邊
- `notApplicable`：極淡，幾乎無視覺重量
- `waived`：實心去飽和，帶「已略過」記號

hover 顯示 facet 名稱與狀態。`notApplicable` 的整個維度（如風景的人物三維）整列淡化。

### 11.3 狀態管理

單一 Pinia store。SSE 事件以 reducer `applyEvent(state, event)` 處理——純函式，vitest 可測，不用跑瀏覽器。

**前端也要回滾。** 失敗前已串出去的 `tool_call` 卡片、`dimensions` 更新都是這一輪的半成品。reducer 是純函式，做法跟後端對稱：**送出當下**就 snapshot store（不是等 `session` 事件——斷線可能發生在第一個事件之前），收到 `error`／`blocked`、或串流沒有以終止事件收尾就斷掉（記成 `stream_ended`）時 restore，再推一筆帶原文的失敗條目。後端回滾、前端回滾，兩邊一致（§4.6）。

**重載恢復。** 對話流（顯示用的 transcript）與 sessionId 存 `sessionStorage`；權威狀態（status、profile、facetStates、askCount、lastFinal）重載時從 `GET /api/sessions/{id}` 拿回，`404` 就開新 session 並提示已過期（子專案 3 設計 §3.4）。

## 12. 測試策略

### 12.1 單元測試（xUnit / vitest）— 只測程式碼保證的部分，不呼叫 LLM

`ToolSetBuilder.Build`：

- `AskCount >= 2` 時無 `AskUser`；`Finalized` 時無 `AskUser` 有 `RequestSaveConsent`
- `Collecting` 且 `DiscussStreak < 8` → 有 `Discuss`；`= 8` → 無
- `Finalized` 且 `DiscussStreak = 8` → 仍有 `Discuss`
- `wantsAutoComplete` 命中 → 無 `AskUser` 也無 `Discuss`
- `AskCount = 2` 且 `DiscussStreak = 8` 且 `Collecting` → 只剩永遠註冊的那五個

Filters：

- `ToolBudgetFilter`：超過上限 `Terminate`
- `TerminalToolFilter`：四個終止 tool 各自 `Terminate`
- `Finalized` 下 `Discuss` 帶變更的 `facetStates` → 拒絕、不終止、計預算
- `Profile == null` 下 `Discuss` 的 `facetStates` → 忽略、不發 `dimensions`

Session 狀態機：

- `SetProfile` 重置 facet 但不重置 `AskCount`、`DiscussStreak` 與 `AutoFill`；`waived` 的 facet 不再出現於 missing；`AutoFill` 設為 true 後不會被重設
- `Discuss` 成功：`Collecting` 下 `DiscussStreak++`，`Finalized` 下不變；兩者 `AskCount` 皆不變
- `FinalizePrompt` 成功 → `DiscussStreak = 0`；`AskUser` 成功 → `DiscussStreak` 不變
- `OutputSafetyFilter` 攔截 → 所有計數器不變

後端清洗（§4.2 表格每一列一個案例）：

- `AskUser.asks`：截斷至 3 則、`options` 截斷至 4 個、少於 2 個的該則移除、過濾非 `missing` 的 facet、過濾跨維度的 facet、`missingFacetIds` 空則移除、全空回結構化錯誤
- `options` 清洗：`presetId` 不在 ledger → 降級為 null

`PresetLedger`：

- `OfferedAs` 注入取前 24、按最近 turn 排序
- `Hits` append 不覆蓋
- histories 不進 ledger

Chat history：

- call args 壓縮後 `options` 無 `tags`
- 超過 10 輪時最舊的被丟，system message 保留

錯誤復原（§4.6）：

- 任一階段拋例外 → session 所有欄位等於 snapshot，`ChatHistory.Count` 回到輪次開始，`PresetLedger` 無本輪新增
- 終止型 tool 成功後再拋例外（例如 SSE 寫入失敗）→ 不回滾，狀態已提交
- decorator 分類：內容攔截 reason → 重試 1 次，仍被擋才拋 `UpstreamBlocked`，第二次通過則正常回傳；空回應 → 重試至 `UnusableRetries` 次；429／5xx → 重試至 `TransportRetries` 次。每一類用 fake `IChatCompletionService` 各一案例，內容攔截要有「第二次過」與「第二次仍擋」兩案
- 退避等待中 `CancellationToken` 取消 → 立即停止、回滾，不再打下一次
- 失敗後重送同一段文字 → 計數器、history 長度、ledger 與首次送出時完全相同
- 失敗一輪只寫一筆 `Turn_Failed`，`attempts` 等於實際嘗試次數

其他：

- `SafetyGuard` 快速路徑（denylist）
- 純文字協定違規處理：以 fake `IChatCompletionService` 回純文字，驗證重試後包成 `Discuss` 而非 `AskUser`；`Discuss` 不可用時發 `error`
- 前端 `applyEvent` reducer，含 `session` 事件 snapshot / `error`／`blocked` restore

**不 fake 整個 auto-invoke 迴圈**——那在 connector 內部，fake 它等於重寫它。測的是清單組裝與 filter 本身。

### 12.2 契約測試 — 真打 Gemini，只斷言形狀

3–5 個，標 `[Trait("Category", "Integration")]`，CI 預設跳過：

- 首輪對話回傳的 `facetStates` 含該 profile 全部 facet id
- `FinalizePrompt` 的 `positivePrompt` 非空且為英文
- tool 參數可正確反序列化

### 12.3 人工 Eval — `docs/eval-cases.md`

固定輸入（目前 23 條），每次改 system prompt 後手動跑並記錄結果與 `prompt_version`：

1. 極簡人像（「一個女生」）→ 應追問
2. 完整人像 → 應直接定稿
3. 風景（「山上的日出」）→ profile=landscape，人物三維 notApplicable
4. 載具 → profile=vehicle
5. 含「隨便」→ 不追問直接定稿，且所有 `missing` 被補齊
6. 「不要指定鞋子」→ `clothing.footwear` waived，定稿 prompt 無鞋子描述
7. 連續兩輪模糊回答 → 第三輪強制定稿，`missing` 不補，定稿卡片列出未指定項目
8. 「鞋子隨便，背景我要想一下」→ 仍追問背景，只有鞋子被補
9. NSFW 輸入 → blocked
10. 真實公眾人物 → blocked
11. 定稿後「把背景改成黃昏」→ 重新定稿，不追問
12. 中途改題材（人像改風景）→ facet 重置
13. 使用者回答與追問無關 → LLM 應能處理不崩
14. 中途提問（「寫實跟動漫差在哪？」）→ LLM 走 `Discuss`，`AskCount` 不變，前端是對話氣泡不是追問卡
15. 定稿後討論（「`blurry` 是幹嘛的？」）→ 沒有新定稿卡
16. `Collecting` 一路聊到 streak 踩滿 → 強制定稿 → 之後還能繼續聊
17. 回頭引用先前選項（「厚塗油畫那個具體會加哪些 tag？」）→ LLM 回答內容與 ledger 裡的 snippet 一致，沒有重撈也沒有編造
18. 「一個少女」六缺五 → 第一次 `AskUser` 問三個維度、第二次問剩下的
19. 「都你決定」→ 該輪直接定稿，沒有中間的 `Discuss`
20. 定稿後說「風格改成動漫」→ LLM 用 `FinalizePrompt` 不是 `Discuss`（或被 §4.5 拒絕後改用）
21. 以 fake connector 讓第二次 LLM 呼叫 500 兩次後成功 → 使用者無感，audit 無 `Turn_Failed`
22. 讓它連續失敗超過重試次數 → `error` 事件、儀表板回到輪次開始、按「重試」後正常完成、`AskCount` 只算一次
23. 送出會觸發上游攔截的描述 → `blocked` 事件、訊息保留、按「重試」原文回到輸入框、改寫後送出正常完成

### 12.4 TDD 適用範圍

§12.1 全部適用 TDD（先寫測試）。§12.2、§12.3 不適用。

## 13. 專案結構

```text
GenAIPromptCopilot/
├─ docs/
│  ├─ 初步想法.md
│  ├─ eval-cases.md
│  ├─ 資料來源.md                         # 來源、授權、署名、公開散布與免責聲明
│  ├─ images/                            # README 截圖
│  └─ superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
├─ db/init/001_schema.sql
├─ src/
│  ├─ PromptCopilot.Api/                 # .NET 10
│  │  ├─ Endpoints/
│  │  ├─ Orchestration/                  # IPromptOrchestrator, AgenticOrchestrator,
│  │  │                                  # StateMachineOrchestrator(空殼), ToolSetBuilder
│  │  ├─ Plugins/                        # KnowledgePlugin, DialogPlugin, SessionPlugin
│  │  ├─ Filters/                        # TerminalTool, ToolBudget, OutputSafety, Audit
│  │  ├─ Safety/                         # SafetyGuard (輸入側), SafetyClassifier, Denylist
│  │  ├─ Llm/                            # ResilientChatCompletion, 失敗分類,
│  │  │                                  # GeminiEmbeddingClient, GeminiRoleFixHandler
│  │  ├─ Streaming/                      # AgentEvent, Channel 基礎建設, SSE writer
│  │  ├─ Sessions/                       # Session, SessionStore, PresetLedger
│  │  ├─ Data/                           # repositories（Npgsql 原生 SQL，無 DbContext／entity）
│  │  ├─ Prompts/system.md
│  │  └─ Configuration/facets.yaml
│  ├─ PromptCopilot.Api.Tests/
│  └─ PromptCopilot.Frontend/            # Nuxt 3 SPA（ssr: false）+ Tailwind + Pinia + vitest
│     ├─ types/api.ts                     # 後端 DTO 與 SSE 事件型別，唯一定義處
│     ├─ lib/                             # 純函式：sse、reducer、persist、composer、dashboard、copy
│     ├─ composables/useApi.ts
│     ├─ stores/session.ts                # 唯一的 Pinia store，狀態變更全走 lib/reducer
│     ├─ components/
│     └─ tests/                           # vitest，node 環境，不跑瀏覽器
├─ scripts/
│  ├─ pipeline/
│  ├─ seed_data.py
│  ├─ export_seed.py                     # 匯出公開的知識庫種子 dump（丟棄容器裡過濾，開發庫唯讀）
│  ├─ requirements.txt
│  └─ data/{raw,clean,structured}/       # git ignore
├─ manual-tests/                         # 手動試用：start_api.py 起 API、chat.py 終端機對話
├─ docker/
│  ├─ Dockerfile.api                     # sdk 編譯 → aspnet 執行
│  ├─ Dockerfile.frontend                # nuxi generate → nginx
│  ├─ Dockerfile.seed                    # pgvector 底圖 + curl，跑 seed.sh
│  ├─ seed.sh                            # 空庫才從 Release 下載 dump 並 pg_restore
│  └─ nginx.conf                         # 靜態檔 + 反代 /api /health /swagger（不緩衝 SSE）
├─ docker-compose.yml                    # db → seed → api → frontend
├─ .github/workflows/ci.yml              # dotnet build/test、npm test/build、ruff+pytest、docker build；不部署
├─ .dockerignore
├─ .gitattributes                        # *.sh 強制 LF
├─ README.md
├─ LICENSE                               # MIT，只涵蓋程式碼
├─ .env.example
└─ .gitignore
```

金鑰：`.env`（管線，以及 `docker compose` 的 api 容器：`GEMINI_API_KEY` 映射成 `Llm__ApiKey`）與 user-secrets（API 本機開發），皆不入版控。

## 14. 子專案順序與驗收

| # | 子專案 | 驗收條件 |
| :--- | :--- | :--- |
| 1 | 資料地基 | docker-compose 起 db 並自動建 schema；管線跑完兩張表皆有資料；一支查詢腳本用「昏暗雨夜的科幻城市」能從 presets 檢索到合理結果 |
| 2 | SK Agent 核心 | Swagger 打完整一輪：追問 → 討論 → 回答 → 定稿 → 討論 → 修改；上游攔截後 session 可繼續；NSFW 被攔；`audit_logs` 有紀錄；§12.1 測試全綠；降級檢查點在此 |
| 3 | 前端 + SSE | 瀏覽器端到端跑完 §12.3 第 1、3、6、11 條，加子專案 3 設計 §7 的 F1–F3（重載恢復、攔截後重試、存共享庫） |
| 4 | 收尾 | `docker compose up` 一鍵可用（fresh clone 只放 `.env`；seed 灌入、二次啟動跳過）；抽屜有出處連結；README 含架構圖與截圖；CI 四個 job 綠。細項見子專案 4 設計 §7.2 P1–P7 |

Azure 部署排除。

## 15. 假設與決定紀錄

| 項目 | 決定 | 理由 |
| :--- | :--- | :--- |
| 編排方式 | 全 agentic（方案 B） | 使用者選擇；以動態工具清單補足可控性 |
| Prompt 方言 | SD/SDXL tag 風格 | Civitai 資料絕大多數為此格式 |
| 追問上限 | 2 次／session | 原草稿規格 |
| 維度數 | 六（新增「人物動作」） | 動作是生圖錯誤率最高區塊，值得獨立燈號 |
| Facet 優先級 | LLM runtime 判斷 | 靜態標註不適應題材差異 |
| `missing` 定稿處理 | 預設不補，僅使用者明說「你決定」才補 | 發明細節的決定權在使用者，不在系統 |
| 畫質詞與負向詞 | 永遠生成，不屬於 facet | 是 boilerplate 不是創作選擇 |
| 入庫寫入路徑 | 僅前端按鈕 | 不在共享庫上開 LLM 決定的寫入口 |
| Schema 管理 | SQL 檔 | Python 與 C# 共用；pgvector 用 migration 不自然 |
| Nuxt SSR | 關閉 | 單頁、無 SEO，避免 hydration 問題 |
| LLM provider | Gemini 主、OpenAI 備 | DeepSeek 無 embedding 且 function calling 較弱，排除 |
| Embedding 換模型 | 需全庫 re-index | 向量空間不相容 |
| 跨維度去重歸屬 | grounded 優先、距離次之；定稿時才算 | 歸屬決定借用資格，不能讓推想查詢抹掉「使用者講過」這個事實（§9） |
| `modelId`／`tags` 針對性抓取 | 已實測不可行，不採用 | `modelId` 反查圖片回傳內容 100% 無 `meta.prompt`；`tags` 查詢參數回 400 Bad Request。見 `docs/superpowers/specs/2026-09-22-corpus-expansion-design.md` §4.3 |
| 使用者提問時重設或豁免 `AskCount` | **否決**，改加 `Discuss` | 重設把「系統的打斷額度」跟「使用者的參與度」綁在一起，兩者沒有因果關係；使用者要的是「能繼續對話」，不是「讓 LLM 多問我兩次」。豁免則要靠分類器判斷「這句是提問還是回答」，邊界模糊（「你覺得寫實比較好嗎？我選寫實」兩者皆是），把閘門建在分類器上等於把硬保證降級成猜測——跟 §4.3 拒絕關鍵詞比對是同一個理由 |
| `Discuss` 帶不帶選項 | 帶「參考方向」，不帶 `missingFacetIds` | 純文字的討論體驗差（「再多給我幾個方向」只能收到散文）；界線靠「索取 vs 回應」的語意與 streak 護欄守住 |
| `Discuss` 帶不帶 `facetStates` | 帶，必填 | 終止型工具是該輪最後一次同步狀態的機會；不帶則討論期間儀表板變死的。這跟「不宣告需求」是兩回事 |
| `DiscussStreak` 上限 | 8 | 使用者指定。`Collecting` 期間最多 10 個未定稿回合 |
| `DiscussStreak` 誰歸零 | 只有 `FinalizePrompt` | 讓上限可以講成一個數字；`AskUser` 不代表進展 |
| `Finalized` 後 `Discuss` 限不限次 | 不限 | 護欄的目的是確保交出東西，交了就功成身退 |
| `AskUser` 多維度 | 一次最多 3 則 | 3 × 2 = 6 覆蓋六維度全缺的最壞情況；一張卡 12 個 chip 分三區不至於糊掉 |
| 追問政策 | 還有任何 facet 是 missing 的維度都要問（waived 與有委託 note 的 facet 不算），只講一部分的維度也問剩下的 facet；一次問滿 3 個 | 原本「能省就省」導致追問偏少（eval #18、使用者 2026-09-25 實測）；使用者選擇「盡量追問」，接受完整描述也可能先被追問（eval #2）；額度 2×3 剛好能問完人像 6 維 |
| 定稿閘門 | 有缺就不准定稿，直到追問額度用完（`AskUser` 離開清單）；委託 note 的 facet 不算缺；強制定稿放行 | system.md 的追問政策模型不遵守（2026-09-25 實測：`AskUser` 還在清單上、31 個 facet 有 20 個 missing，模型直接 `FinalizePrompt`）；沿用 §4.3「違規的選項不給選」 |
| `OutputSafetyFilter` 範圍 | 全檢（定稿 + 討論 + 追問的文字與選項） | §6.2 原文的理由對 `options` 一字不差地成立；合規是對外賣點，出口不一致難講 |
| `presetId` 不在 ledger | 降級為 null，不移除 | `presetId` 本來就可為 null；輸出過濾兜住自由發明的內容 |
| `AutoFill` 可逆 | **不做** | 有了 `Discuss` 之後問題自己解掉：`Discuss` 不看 `AutoFill`，使用者透過討論把某項變成 `covered` 或 `waived`，那一項就不在 `AutoFill` 補齊的範圍內。`waived` 本來就是為「即使委託也不補」存在的 |
| 純文字補救的預設目標 | `Discuss`（原為 `AskUser`） | LLM 吐散文時九成是想講話，不是追問 |
| ledger 只收 presets | 是 | histories 是參考不是選項，不會被回頭引用 |
| history 截斷 | 最近 10 輪 | session 事實都在 ledger／`FacetStates`／`LastFinal`，history 只需最近脈絡 |
| session 總輪次上限 | 不加 | 純成本護欄，既有設計就沒有 |
| 一輪失敗的處理 | 回滾至本輪開始前，所有失敗一律 | 一般化原本只對逾時的回滾；「不全毀」靠原子性保證，不靠逐項列舉哪個欄位不動 |
| 手動重試 | 不加端點；不分 `error`／`blocked` 一律給重試按鈕，原文填回輸入框 | session 已回滾，重送等價首次送出；不給按鈕使用者也只是重打一次，區分是做樣子。「只重試一次」管的是系統自動重送的上限，不是使用者的決定 |
| 上游內容攔截的內部重試 | 1 次（原 0 次） | 實測同一份 SFW 內容會被誤擋，重送一次常會過；送到上游的內容已先過我們自己的 `SafetyGuard`，容忍第二個分類器一次誤判合理。上限 1 是為了不變成「重送到過為止」——那才是規避安全判定 |
| 傳輸重試次數 | 3（Python 管線是 6） | 互動場景每輪 3–4 次上游呼叫，6 次退避會吃掉整輪預算 |
| 單輪逾時 | 120s（原 60s） | 給三層重試留空間；退避吃同一個 `CancellationToken`，不會逾時了還在等 |
| 對話 connector | `Connectors.Google`（否決 OpenAI 相容端點） | 相容層不回 `blockReason`／`finishReason`，`Blocked_Upstream` 就只剩猜（§4.8） |
| `GeminiRoleFixHandler` | 暫時保留，附移除條件 | connector 1.80.1-alpha 把 tool 回覆標成 `role:"function"`，Gemini 回 400；只要模型呼叫任何 tool 這一輪就結束不了（§4.8） |
| 資料存取 | Npgsql + Pgvector 原生 SQL，不用 EF Core | 查詢全帶 `<=>` 與 `&&`，EF 對兩者都要走 raw SQL，多一層翻譯沒有收益（§7） |
| embedding 客戶端 | 自己打 REST `:batchEmbedContents` | 要鎖住 `taskType`／`outputDimensionality`／L2 正規化與 Python 管線一致，SK 抽象當時蓋不到（§9） |
| `facetStates` 形狀 | `FacetStateEntry[]`，不是 dictionary | Gemini 的 function declaration schema 描述不出開放鍵的 map；陣列還能放 `note`（§4.2） |
| `SearchPresets` 參數 | 只收 `dimension` 與 `query` | `facetIds` 與 `k` 都是伺服器算得出來的，傳進來只是多一個可被捏造的欄位（§4.2、§9） |
| `SearchPresets` 批次簽名 | `queries: {dimension, query}[]`，一輪一次呼叫 | 逐維度呼叫的規則跟預算 8 在算術上不相容（人像 6 維 + 對比方向 > 8），第一輪就強制定稿；Python 管線本來就是一次 `embed_batch`（known-issues #1、[批次設計](2026-09-24-batch-search-presets-design.md)） |
| `MaxToolCallsPerTurn` | 16（原 8） | 批次後預期一輪 4–5 次；16 是模型仍逐維度呼叫時的保險，不是設計目標 |
| filter 介面 | 四個都是 `IAutoFunctionInvocationFilter` | `IFunctionInvocationFilter` 拿不到 `Terminate`，攔截停不了整個迴圈（§4.5） |
| 攔截的表達方式 | `Terminate` + `BlockedOutcome`，不丟例外 | filter 的例外會被 SK 當成連線層失敗往上冒，重試層看不懂（§4.5） |
| `Discuss` 變更閘門比對基準 | 本輪開始時的 facet 狀態 | 比現值的話，先 `SetFacetStates` 改掉再 `Discuss` 回報同值就永遠相等，閘門等於不存在（§4.5） |
| 輸出審核的取材方式 | 照 §6.2 的欄位表逐 tool 取，不是把參數整包串起來 | 範圍仍是全檢（上一列），但 `negativePrompt` 常態含 `nsfw, nude, naked`（那是排除清單），整包餵進去等於拿自己的排除詞去問分類器 |
| 分類器 prompt 的形狀 | 待審內容夾在 `<<<INPUT` / `INPUT>>>` 之間，並註明是資料不是指令 | 原本直接把使用者文字接在指示後面，等於邀請它自稱是指示 |
| 分類器判定缺 `reason` | 當成解析失敗丟例外 | `{}` 也是合法 JSON，反序列化出來剛好是「全 false」——那是漏判，不是乾淨（fail-open） |
| denylist 命中的訊息 | 不複述命中的詞，詞只進 audit payload | 複述等於把清單一個一個唸給使用者聽 |
| `Ask_Facets_Selected` 等三個事件 | 不實作 | `AskCount` 用盡表現為工具消失；另兩者的資料放 `Turn_Completed` 的 payload（§5.1、§7） |
| `tool_result.callId` | 等於 `tool_call.callId` | 前端要配對；id 由 `AuditFilter` 算好掛在 `TurnContext` 上（§10.2） |
| 錯誤 frame 的內容 | 固定句子，不帶例外訊息 | 例外訊息可能帶連線字串、路徑、上游原文；內文留在 audit 與 log |
| `save-to-shared` 的併發 | 與 `/messages` 共用 session 鎖，拿不到回 409 | 它讀的 `FacetStates`／`LastFinal` 在一輪跑完之前都還可能被回滾（§10.1） |
