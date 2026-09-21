# GenAI Prompt Copilot — 設計規格

日期：2026-09-21
狀態：待審閱
前身文件：[docs/初步想法.md](../../初步想法.md)（本文件取代其中的架構與流程章節；技術棧與階段藍圖以本文件為準）

---

## 1. 目標與定位

一個協助使用者精煉 AI 生圖提示詞的多輪對話助理。使用者用繁體中文描述需求，系統以六維度 facet 體系分析資訊充足度、主動追問缺失細節、從知識庫推薦可用片段，最後產出 SD/SDXL tag 風格的英文正／負向提示詞，並在使用者同意後沉澱為全域共享知識。

**專案目的：求職作品集。** 設計取捨一律以「每項技術都有可展示的實證」與「有一個能跑給人看的 demo」優先，功能深度其次。

展示重點：

- Semantic Kernel：全 agentic Function Calling 編排、Filters、RAG
- ASP.NET Core：SSE 事件串流、Session 狀態機
- PostgreSQL + pgvector：向量 + GIN 混合檢索
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
| `KnowledgePlugin.SearchPresets` | `query: string, facetIds: string[], topK: int = 5` | 可重複；RAG 2，**分維度檢索** `prompt_knowledge_presets`：一次呼叫一個維度，`query` 是該維度專屬語句，見 §9 |
| `SessionPlugin.SetProfile` | `profile: portrait \| landscape \| object \| vehicle` | 可重複；設定題材 profile，重置 facet 狀態 |
| `SessionPlugin.SetFacetStates` | `updates: { facetId: string, state: FacetState, note?: string }[]` | 可重複；主要用於 `waived` |
| `DialogPlugin.AskUser` | `question: string, missingFacetIds: string[], suggestedOptions: string[], facetStates: Dictionary<string, FacetState>` | **終止型**；`suggestedOptions` 2–4 個 |
| `DialogPlugin.FinalizePrompt` | `positivePrompt: string, negativePrompt: string, tips: string, facetStates: Dictionary<string, FacetState>` | **終止型** |
| `DialogPlugin.RequestSaveConsent` | 無 | **終止型**；不碰 DB，只觸發前端確認卡片 |

`FacetState = covered | missing | waived | notApplicable`。

`facetStates` 在 `AskUser` / `FinalizePrompt` 為必填，後端以此更新 session 並發 `dimensions` 事件。

### 4.3 工具清單組裝規則

每次 agent loop 啟動前，由純函式 `ToolSetBuilder.Build(session, userMessage)` 決定本輪註冊的工具：

```text
永遠註冊： SearchSimilarPrompts, SearchPresets, SetProfile, SetFacetStates, FinalizePrompt

AskUser：             session.Status == Collecting
                  且 session.AskCount < MaxAskCount (預設 2)
                  且 未偵測到捷徑指令

RequestSaveConsent：  session.Status == Finalized
```

捷徑指令（「隨便」「你看著辦」「幫我決定」「直接給我」等）**不用關鍵詞比對**——「鞋子隨便，但背景我要想一下」會被誤判。改由 §6.1 的輸入側分類器順帶回傳 `wantsAutoComplete: bool`（判斷整句是否要求系統直接補齊全部），不多花一次呼叫。命中即移除 `AskUser` 並將 `Session.AutoFill` 設為 true（§5.4），與輪次上限走同一機制。針對單一 facet 的「鞋子隨便」則由 LLM 在對話中處理：該 facet 維持 `missing`，並透過 `SetFacetStates` 的 `note` 記下「使用者委託此項」，定稿時只補這一項。

**此函式是整個防死循環機制的核心，必須有單元測試覆蓋。**

### 4.4 Session 狀態機

```text
Session {
  Id, CreatedAt
  Status:      Collecting | Finalized
  Profile:     portrait | landscape | object | vehicle | null
  AskCount:    int
  AutoFill:    bool                         // 使用者是否已委託系統補齊 missing（§5.4）
  FacetStates: Dictionary<facetId, FacetState>
  ChatHistory: SK ChatHistory
  LastFinal:   { Positive, Negative, Tips }?
  Lock:        SemaphoreSlim(1)
}
```

狀態轉移：

- 建立 → `Collecting`
- `FinalizePrompt` 呼叫成功 → `Finalized`
- `Finalized` 後使用者要求修改（「把背景改成黃昏」）→ 仍是 `Finalized`，LLM 直接重新 `FinalizePrompt`；`AskUser` 永久不可用
- `SetProfile` 切換 profile → `FacetStates` 重置；**`AskCount` 不重置**（防死循環針對的是系統，不是限制使用者）
- 一個 session = 一個 prompt。「再來一張」由前端開新 session。

儲存：`IMemoryCache`，滑動過期 2 小時。不落 DB。

### 4.5 Filters

| Filter | 介面 | 職責 |
| :--- | :--- | :--- |
| `TerminalToolFilter` | `IAutoFunctionInvocationFilter` | `AskUser` / `FinalizePrompt` / `RequestSaveConsent` 執行後設 `context.Terminate = true` |
| `ToolBudgetFilter` | `IAutoFunctionInvocationFilter` | 計數單輪 tool 呼叫，超過 `MaxToolCallsPerTurn`（預設 8）→ `Terminate`，觸發強制定稿（§4.6） |
| `OutputSafetyFilter` | `IFunctionInvocationFilter` | 只掛在 `FinalizePrompt`；對 `positivePrompt` 跑安全分類（§6），命中則改寫結果為 blocked 並中止 |
| `AuditFilter` | `IAutoFunctionInvocationFilter` | 每次 tool 呼叫、每次攔截、token 與延遲寫入 `audit_logs` |

輸入側安全檢查（`SafetyGuard`）在 service 層、進 kernel 之前執行，不是 SK filter——因為 agentic chat completion 路徑不會觸發 `IPromptRenderFilter`。

### 4.6 失敗模式處理

| 情況 | 處理 |
| :--- | :--- |
| LLM 回純文字、未呼叫終止 tool | 補一則系統提示「你必須呼叫 AskUser 或 FinalizePrompt」重試一次。仍為純文字：若 `AskUser` 本輪可用，將文字包成 `AskUser`（`AskCount++`）；否則發 `error` 事件 |
| `Profile` 為 null 時呼叫 `AskUser` / `FinalizePrompt` | `TerminalToolFilter` 不終止，改回傳結構化錯誤「請先呼叫 SetProfile」給 LLM，讓它補呼叫後再繼續；計入 tool 預算 |
| Tool 預算耗盡 | `Terminate` 後再跑一次強制定稿：只掛 `FinalizePrompt`，附「請立即以現有資訊定稿」提示 |
| 單輪逾時（預設 60s） | `CancellationToken` 取消，發 `error` 事件，session 狀態回滾到本輪開始前 |
| 使用者斷線 | HTTP `RequestAborted` 傳入 agent loop 的 `CancellationToken`，立即停止 |
| 同 session 併發送訊息 | `Lock` 未取得 → 回 `409 Conflict` |
| Tool 執行例外（DB 掛掉等） | filter 捕捉，回傳結構化錯誤訊息給 LLM 讓它決定是否重試或直接定稿；寫 audit |

### 4.7 Chat history 修剪

每輪結束後，將本輪 tool result 訊息內容壓成摘要（`SearchPresets` → `[{id, title}]`；`SearchSimilarPrompts` → `[{id, intent 前 40 字}]`），避免歷史隨輪次膨脹。

### 4.8 Provider 抽象

兩條獨立的軸：

- **對話**：`Llm:Provider = Gemini | OpenAI`、`Llm:Model`、`Llm:ApiKey`、`Llm:Endpoint?`。DI 時依組態註冊對應 SK connector。
- **Embedding**：`Embedding:Provider`、`Embedding:Model`、`Embedding:Dimensions`。**換 embedding 模型 = 整個向量庫必須重算**，README 與組態檔明確標註；管線 `embed.py --reindex` 對應。

**Connector 選擇是子專案 2 的第一項任務**：優先驗證「SK OpenAI connector 對 Gemini 的 OpenAI 相容端點」能否穩定跑 function calling + streaming。若可，Gemini 與 OpenAI 共用同一 connector，切換 provider 為純組態；若不可，改用 `Microsoft.SemanticKernel.Connectors.Google`。

### 4.9 System prompt

- 存於 `src/PromptCopilot.Api/Prompts/system.md`，不寫在 C# 字串裡
- 由 template + `facets.yaml`（依 session profile 篩選）+ session 既定事實（已 waived 的 facet、`AutoFill` 狀態、已定稿內容）組裝
- 組裝後的 prompt 取 SHA-256 前 12 碼寫入每筆 `audit_logs.prompt_version`，讓 eval 紀錄可對應 prompt 版本

### 4.10 降級路徑

定義 `IPromptOrchestrator`：

```csharp
interface IPromptOrchestrator {
    IAsyncEnumerable<AgentEvent> RunTurnAsync(Session s, string userMessage, CancellationToken ct);
}
```

兩個實作：`AgenticOrchestrator`（本設計）與 `StateMachineOrchestrator`（後端決定 ASK/FINALIZE，LLM 只做分析與檢索）。組態 `Orchestrator:Mode` 切換。API 契約與前端不變。

`StateMachineOrchestrator` 在子專案 2 只留介面與 `NotImplementedException` 空殼；**只有當子專案 2 驗收時 agentic loop 無法穩定跑完「追問 → 定稿」才實作**。

## 5. 六維度與 Facet 體系

### 5.1 結構

六個維度，每個維度下掛一組 facet。**充足度判定與追問優先級皆由 LLM 於 runtime 判斷**，不做靜態的 critical/helpful 標註——哪個 facet 重要高度依題材而定，靜態標註反而不準。LLM 挑了哪些 facet 追問會寫入 `audit_logs`（`Ask_Facets_Selected`），使其可觀察。

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

**發明細節的決定權在使用者。** 系統預設不替使用者補上他沒說的東西；只有使用者明說「隨便／你決定／你看著辦」（§6.1 分類器回傳 `wantsAutoComplete`）才把 `Session.AutoFill` 設為 true，此後定稿時 LLM 補齊所有 `missing`。`AutoFill` 一旦為 true 在該 session 內保持（使用者已委託）。「不要指定 X」是 `waived`，永遠不補。System prompt 需明確區分這三種情況。

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

`FinalizePrompt` 的 `positivePrompt` 走同一個分類器（英文輸入）。命中 → 不進 `Finalized` 狀態、發 `blocked` 事件、寫 audit。防止無害輸入配上 preset 組出不當內容。

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

`db/init/001_schema.sql` 為 schema 單一真實來源，由 docker-compose 於首次啟動執行。**不用 EF Core Migration**——Python 與 C# 共用資料庫，且 pgvector 型別與 HNSW 索引用 migration 表達不自然。EF Core 只負責讀寫。

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

`event_type` 值：`Blocked_NSFW`、`Blocked_Celebrity`、`Blocked_Output`、`Tool_Invoked`、`Turn_Budget_Exhausted`、`Tool_Budget_Exhausted`、`Facet_Waived`、`Ask_Facets_Selected`、`Protocol_Violation`、`Turn_Completed`（含 token 與延遲）。

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
| `SearchPresets` | **分維度**：每次呼叫鎖定一個維度，用該維度專屬的查詢語句，GIN 過濾該維度 facet 後向量排序 | `WHERE facet_ids && $facetIdsOfDimension ORDER BY preset_embedding <=> $dimensionQueryVec LIMIT k` |
| `SearchSimilarPrompts` | 向量 Top-K + profile 過濾 | `WHERE subject_profile = $1 ORDER BY intent_embedding <=> $2 LIMIT k` |

**GIN 過濾本身不夠。** 實測（2026-09-22）：同樣過濾到 Style 候選池，用使用者整句描述的向量排序撈回無關片段（dist 0.354），用該維度專屬的查詢語句撈回正確風格（0.229–0.234）。單一整句向量是六維度的模糊平均，只會貼近最泛用的片段，且使用者沒提到的維度永遠撈不到。因此 agent 呼叫 `SearchPresets` 時：

- 一次呼叫一個維度，`facetIds` 給該維度在當前 profile 下的 facet 集合。
- `query` 是該維度的專屬語句：使用者已描述的維度用其原話；未描述的維度由 agent 依整體畫面推想，且應給對比方向（例：寫實攝影 vs 動漫插畫）分兩次呼叫。
- 使用者未描述的維度撈到的片段只可用於追問與建議，不得直接寫入提示詞。
- 不設距離門檻；tool result 帶相似度分級（`<0.25` 高、`<0.30` 中、其餘低），由 agent 判斷。
- `tags && $2` 決定不做：OR 上 tags 會把候選池撐到維度外，與分維度前提衝突。

查詢向量於 runtime 以同一 embedding 模型計算；多個維度的查詢語句合併為單次 `embed_batch` 呼叫。

完整推導與 Python 參考實作見 [2026-09-22-dimension-scoped-retrieval-design.md](2026-09-22-dimension-scoped-retrieval-design.md)。

## 10. API 與 SSE 協定

### 10.1 端點

| 方法 | 路徑 | 說明 |
| :--- | :--- | :--- |
| `POST` | `/api/sessions` | 建立 session → `{ sessionId }` |
| `POST` | `/api/sessions/{id}/messages` | body `{ text }`；回 `text/event-stream` |
| `POST` | `/api/sessions/{id}/save-to-shared` | 需 `Finalized`；寫 `shared_prompt_histories` 並向量化；**唯一的寫入路徑** |
| `GET` | `/api/config/facets` | 回 `facets.yaml` 內容供前端渲染 |
| `GET` | `/api/presets/{id}` | preset 詳情（抽屜用） |
| `GET` | `/health` | |

前端以 `fetch` + `ReadableStream` 消費 SSE（`EventSource` 不支援 POST）。

### 10.2 SSE 事件

| `event:` | `data:` | 前端反應 |
| :--- | :--- | :--- |
| `session` | `{ sessionId, turnIndex, status }` | 初始化 |
| `tool_call` | `{ callId, name, argsSummary }` | 對話流插入行內卡片 |
| `tool_result` | `{ callId, name, summary, presets?: [{id, title, imageUrl}] }` | 展開卡片；餵抽屜 |
| `dimensions` | `{ profile, facetStates: {facetId: state} }` | 儀表板更新 |
| `token` | `{ text }` | 打字機 |
| `final` | `{ kind: "ask", question, suggestedOptions, missingFacetIds }` 或 `{ kind: "finalized", positive, negative, tips }` 或 `{ kind: "save_consent_requested" }` | 追問氣泡／定稿卡片／高亮入庫按鈕 |
| `blocked` | `{ reason, message }` | 攔截提示 |
| `error` | `{ code, message }` | 錯誤 |

### 10.3 串流實作

`IAutoFunctionInvocationFilter` 內無法存取 HTTP response。做法：每個請求建立 scoped `Channel<AgentEvent>`，filters 與 orchestrator 往 channel 推事件，端點以 `IAsyncEnumerable<AgentEvent>` 讀出並寫 SSE。**這是前後端串接最容易卡住的點，實作計畫中列為獨立任務。**

## 11. 前端（Nuxt 3，`ssr: false`）

關閉 SSR：單頁、無 SEO 需求，SSE + 客戶端狀態在 SSR 下的 hydration 問題不值得處理。

### 11.1 版面

- 左側主區：對話流。tool call 以輕量行內卡片呈現（「🔍 查詢知識庫：鏡頭 · 景別 → 找到 5 筆」），完成後自動摺疊，可點開。
- 右側 sticky 側欄：六維度儀表板。
- 定稿卡片出現在對話流內（不用 modal），含正／負向 prompt、複製按鈕、生成建議、「儲存至共享知識庫」按鈕。
- preset 預覽抽屜從右側滑出，覆蓋儀表板；顯示 `image_url`、snippet、tags。
- 追問氣泡下方渲染 `suggestedOptions` 為快速回覆 chip。
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

## 12. 測試策略

### 12.1 單元測試（xUnit / vitest）— 只測程式碼保證的部分，不呼叫 LLM

- `ToolSetBuilder.Build`：`AskCount >= 2` 時無 `AskUser`；`Finalized` 時無 `AskUser` 有 `RequestSaveConsent`；捷徑指令命中時無 `AskUser`
- `ToolBudgetFilter`：超過上限 `Terminate`
- `TerminalToolFilter`：三個終止 tool 各自 `Terminate`
- Session 狀態機：`SetProfile` 重置 facet 但不重置 `AskCount` 與 `AutoFill`；`waived` 的 facet 不再出現於 missing；`AutoFill` 設為 true 後不會被重設
- `SafetyGuard` 快速路徑（denylist）
- 純文字協定違規處理（以 fake `IChatCompletionService` 回傳純文字，驗證重試與包裝）
- 前端 `applyEvent` reducer

**不 fake 整個 auto-invoke 迴圈**——那在 connector 內部，fake 它等於重寫它。測的是清單組裝與 filter 本身。

### 12.2 契約測試 — 真打 Gemini，只斷言形狀

3–5 個，標 `[Trait("Category", "Integration")]`，CI 預設跳過：

- 首輪對話回傳的 `facetStates` 含該 profile 全部 facet id
- `FinalizePrompt` 的 `positivePrompt` 非空且為英文
- tool 參數可正確反序列化

### 12.3 人工 Eval — `docs/eval-cases.md`

10–15 條固定輸入，每次改 system prompt 後手動跑並記錄結果與 `prompt_version`：

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

### 12.4 TDD 適用範圍

§12.1 全部適用 TDD（先寫測試）。§12.2、§12.3 不適用。

## 13. 專案結構

```text
GenAIPromptCopilot/
├─ docs/
│  ├─ 初步想法.md
│  ├─ eval-cases.md
│  └─ superpowers/specs/2026-09-21-genai-prompt-copilot-design.md
├─ db/init/001_schema.sql
├─ src/
│  ├─ PromptCopilot.Api/                 # .NET 10
│  │  ├─ Endpoints/
│  │  ├─ Orchestration/                  # IPromptOrchestrator, AgenticOrchestrator,
│  │  │                                  # StateMachineOrchestrator(空殼), ToolSetBuilder
│  │  ├─ Plugins/                        # KnowledgePlugin, DialogPlugin, SessionPlugin
│  │  ├─ Filters/                        # TerminalTool, ToolBudget, OutputSafety, Audit
│  │  ├─ Safety/                         # SafetyGuard (輸入側), SafetyClassifier
│  │  ├─ Streaming/                      # AgentEvent, Channel 基礎建設, SSE writer
│  │  ├─ Sessions/                       # Session, SessionStore
│  │  ├─ Data/                           # DbContext, entities, repositories
│  │  ├─ Prompts/system.md
│  │  └─ Configuration/facets.yaml
│  ├─ PromptCopilot.Api.Tests/
│  └─ PromptCopilot.Frontend/            # Nuxt 3 SPA + Tailwind + Pinia
├─ scripts/
│  ├─ pipeline/
│  ├─ seed_data.py
│  ├─ requirements.txt
│  └─ data/{raw,clean,structured}/       # git ignore
├─ docker/
│  ├─ Dockerfile.api
│  └─ Dockerfile.frontend
├─ docker-compose.yml                    # db + api + frontend
├─ .github/workflows/ci.yml              # dotnet build/test, npm build, ruff；不部署
├─ .env.example
└─ .gitignore
```

金鑰：`.env`（管線）與 user-secrets（API 本機開發），皆不入版控。

## 14. 子專案順序與驗收

| # | 子專案 | 驗收條件 |
| :--- | :--- | :--- |
| 1 | 資料地基 | docker-compose 起 db 並自動建 schema；管線跑完兩張表皆有資料；一支查詢腳本用「昏暗雨夜的科幻城市」能從 presets 檢索到合理結果 |
| 2 | SK Agent 核心 | Swagger 打完整一輪：追問 → 回答 → 定稿；NSFW 被攔；`audit_logs` 有紀錄；§12.1 測試全綠；**降級檢查點在此** |
| 3 | 前端 + SSE | 瀏覽器端到端跑完 §12.3 第 1、3、6、11 條 |
| 4 | 收尾 | `docker compose up` 一鍵可用；README 含架構圖與截圖；CI 綠 |

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
