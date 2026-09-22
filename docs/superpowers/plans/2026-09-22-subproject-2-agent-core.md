# 子專案 2：SK Agent 核心（含多輪對話）— Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建一個 ASP.NET Core Web API，讓 LLM 透過 Semantic Kernel function calling 自主決定「追問／討論／定稿」，所有硬限制（追問上限、討論 streak、tool 預算、一輪原子性、輸出過濾）由程式碼保證；Swagger 能打完整一輪「追問 → 討論 → 回答 → 定稿 → 討論 → 修改」。

**Architecture:** 每個 HTTP 請求是一輪（turn）。輪次開始先 snapshot session，`ToolSetBuilder` 依 session 狀態決定本輪註冊哪些 tool，SK 的 auto-invoke 迴圈（`FunctionChoiceBehavior.Auto()`）跑到終止型 tool 為止，四個 `IAutoFunctionInvocationFilter` 管預算、輸出安全、稽核與 `Terminate`；任何失敗一律 restore snapshot。LLM 的自述一律不信：`grounded` 由伺服器從 facet 狀態推導、`asks` 由後端清洗、借用來源由 ledger 驗證。Gemini 的失敗分三層各自重試（傳輸 3／空回應 3／內容攔截 1）。

**Tech Stack:** .NET 10（SDK 10.0.301 已安裝）、ASP.NET Core minimal API、Semantic Kernel + `Microsoft.SemanticKernel.Connectors.Google`（Gemini chat）、Npgsql + Pgvector（不用 EF Core，見偏離 2）、YamlDotNet、xUnit、Swashbuckle；PostgreSQL 16 + pgvector（既有 docker-compose）；Gemini `gemini-3.5-flash-lite`（對話與分類）、`gemini-embedding-001` 768 維（查詢向量）。

**Spec:** [docs/superpowers/specs/2026-09-22-multi-turn-dialogue-design.md](../specs/2026-09-22-multi-turn-dialogue-design.md)（多輪對話：§3–§6、§8）與主規格 [docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md](../specs/2026-09-21-genai-prompt-copilot-design.md)（§4、§5、§6、§7、§9、§10、§12.1、§14 第 2 列）。Task 1 會把多輪設計併回主規格，之後主規格是單一事實來源。Python 參考實作：[scripts/pipeline/retrieval.py](../../../scripts/pipeline/retrieval.py)、[scripts/pipeline/gemini_client.py](../../../scripts/pipeline/gemini_client.py)、[scripts/demo.py](../../../scripts/demo.py)。

---

## Global Constraints

- .NET 10；`src/PromptCopilot.sln` 含 `PromptCopilot.Api` 與 `PromptCopilot.Api.Tests`；所有 `dotnet` 指令從 `src/` 執行
- LLM：`gemini-3.5-flash-lite`，釘死版本不用 `-latest`；embedding：`gemini-embedding-001`，**768 維**，查詢用 `task_type = RETRIEVAL_QUERY`，**L2 正規化**（與 Python `_l2_normalize` 一致，否則距離不可比）
- 數字（全部進組態，`appsettings.json` 為預設值）：`MaxAskCount = 2`、`MaxDiscussStreak = 8`、`MaxToolCallsPerTurn = 8`、`TurnTimeoutSeconds = 120`、`HistoryTurns = 10`、`OfferedOptionsLimit = 24`、`TransportRetries = 3`、`UnusableRetries = 3`、`ContentBlockRetries = 1`、`AskUser.asks ≤ 3`、`options` 2–4（`AskUser`）／0–4（`Discuss`）
- 相似度分級：`dist < 0.25` 高、`< 0.30` 中、其餘低；不設距離門檻。`k`：grounded 維度 5、missing 維度每句 3
- Schema 只在 `db/init/001_schema.sql`；C# 不建表、不 migration
- 金鑰：本機 `dotnet user-secrets`（`Llm:ApiKey`）；CI 不打 Gemini。標 `[Trait("Category","Integration")]` 的測試需要 DB 或 Gemini，預設跳過（見 Task 5 的 `IntegrationFact`）
- 繁中給使用者，英文給生圖模型：`Discuss.message`、`AskUser.question`、`options[].label` 繁中；`positivePrompt`、`negativePrompt`、`options[].tags` 英文
- 不信 LLM 自述：`grounded` 伺服器算、`asks` 後端清洗、`facetStates` 以 catalog 過濾、`presetId` 以 ledger 驗證
- 每個任務結束都 commit；commit 訊息結尾附 `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`
- `docs/單輪流程說明.md` 是 Python demo 的文件，**本計畫不動它**；本計畫動到主規格時在同一 commit 更新

### 與 spec 的偏離（已決定，執行前可否決）

| # | spec 說 | 本計畫做 | 理由 |
| :--- | :--- | :--- | :--- |
| 1 | §4.8：優先驗證 SK OpenAI connector 打 Gemini 的 OpenAI 相容端點 | 直接用 `Microsoft.SemanticKernel.Connectors.Google` | 多輪設計 §5.5／§5.6 要分類「內容攔截 vs 空回應」，靠的是 Gemini 原生的 `promptFeedback.blockReason` / `finishReason`；OpenAI 相容端點把它壓成 `finish_reason: content_filter`，分不出 `PROHIBITED_CONTENT` 與 `SAFETY`。Provider 抽象（`IChatCompletionService`）仍保留，OpenAI 之後可加 |
| 2 | §3／§7：EF Core 讀寫 | Npgsql + Pgvector 直接下 SQL | 三張表、五條查詢，全部要 `<=>` 與 `&&`，EF 只是包一層 `FromSqlRaw`。Python 端也是原生 SQL，兩邊對得上 |
| 3 | §4.5：`OutputSafetyFilter` 是 `IFunctionInvocationFilter` | 四個 filter 都是 `IAutoFunctionInvocationFilter`；OutputSafety 命中用 `Terminate` + `BlockedOutcome`，不丟例外 | 攔截後要停迴圈並記錄結果，只有 auto-invocation filter 有 `Terminate`；filter 裡丟例外會不會穿出 connector 是版本相依的，outcome 沒有這個問題。`ResilientChatCompletion` 包在整個 auto-invoke 迴圈外：SK 邊跑邊把 tool call 與結果寫進 history，重試是從斷點接下去，tool 不會重跑 |
| 4 | 多輪 §4.4：`Finalized` 下 facet 變更的檢查「放在 `TerminalToolFilter`」 | 檢查放在 `DialogPlugin.Discuss` 本身，回結構化錯誤字串、不設 outcome；`TerminalToolFilter` 只看有沒有 outcome | 效果相同，plugin 能直接回訊息給 LLM，filter 不必知道每個 tool 的規則 |
| 5 | Embedding 走 SK `ITextEmbeddingGenerationService` | 自寫 `GeminiEmbeddingClient` 打 REST `batchEmbedContents` | 必須指定 `taskType` 與 `outputDimensionality` 並自行 L2 正規化才與 Python 端同一向量空間；SK 抽象不保證這三件事 |

### 明確不在本計畫

- **借用驗證（Python `validate_borrowed` 的 C# 版）**：主規格 §4.2 的 `FinalizePrompt` 沒有 `borrowed` 欄位，多輪 §6.4「範圍 = 整個 ledger」因此沒有落點。ledger 的 `Hits` 已按主規格 §9 累積，之後改 `FinalizePrompt` 契約加 `borrowed` 即可接上，不必動 ledger。
- **前端**（多輪 §5.4 的預設行為、reducer snapshot/restore）：子專案 3。
- **`GET /api/sessions/{id}` 重建畫面**：多輪 §11 已列為子專案 3 的事。
- **`Turn_Budget_Exhausted`／`Facet_Waived`／`Ask_Facets_Selected` 三個 audit 事件**：主規格 §7 列了但沒有行為需要它們（AskCount 用盡是工具消失、不是事件；waived 與 ask 的選擇都在 `Turn_Completed` 的 payload 裡）。

---

## File Structure

```text
src/
├─ PromptCopilot.sln
├─ README.md                              # 怎麼跑、怎麼打一輪（Task 18）
├─ PromptCopilot.Api/
│  ├─ PromptCopilot.Api.csproj
│  ├─ Program.cs                          # DI、endpoints 掛載、Swagger
│  ├─ appsettings.json                    # 所有數字的預設值
│  ├─ Configuration/
│  │  ├─ facets.yaml                      # 已存在，不動
│  │  ├─ FacetCatalog.cs                  # 載入 yaml；DimensionOf / FacetsOf / IdsForProfile / Listing
│  │  └─ Options.cs                       # LlmOptions / EmbeddingOptions / OrchestratorOptions / DatabaseOptions
│  ├─ Sessions/
│  │  ├─ Session.cs                       # 狀態 + 轉移 + Snapshot/Restore
│  │  ├─ PresetLedger.cs                  # LedgerEntry, Hits, OfferedAs
│  │  └─ SessionStore.cs                  # IMemoryCache，滑動 2h
│  ├─ Data/
│  │  ├─ PresetRepository.cs              # SearchAsync / PoolSizeAsync / GetAsync
│  │  ├─ HistoryRepository.cs             # SearchAsync / InsertAsync
│  │  └─ AuditRepository.cs               # WriteAsync
│  ├─ Llm/
│  │  ├─ GeminiEmbeddingClient.cs         # IEmbeddingClient + REST 實作
│  │  ├─ LlmFailures.cs                   # UpstreamBlockedException / UnusableResponseException / 分類
│  │  └─ ResilientChatCompletion.cs       # IChatCompletionService decorator：三層重試
│  ├─ Safety/
│  │  ├─ Denylist.cs
│  │  ├─ SafetyClassifier.cs              # 結構化分類呼叫
│  │  └─ SafetyGuard.cs                   # 輸入側；回 GuardResult
│  ├─ Plugins/
│  │  ├─ Contracts.cs                     # OptionItem / AskItem / TurnOutcome 家族
│  │  ├─ AskCleaner.cs                    # 多輪 §4.3 清洗
│  │  ├─ KnowledgePlugin.cs               # SearchPresets / SearchSimilarPrompts
│  │  ├─ SessionPlugin.cs                 # SetProfile / SetFacetStates
│  │  └─ DialogPlugin.cs                  # AskUser / Discuss / FinalizePrompt / RequestSaveConsent
│  ├─ Filters/                            # 四個 IAutoFunctionInvocationFilter，掛在每輪的 kernel 上
│  │  ├─ TurnContextExtensions.cs         # kernel.Data["turn"] → TurnContext；ArgsText / Summary
│  │  ├─ TerminalToolFilter.cs            # plugin 設了 Outcome → Terminate
│  │  ├─ ToolBudgetFilter.cs              # 超過上限 → Outcome = BudgetExhausted，Terminate
│  │  ├─ OutputSafetyFilter.cs            # 檢 Discuss/AskUser/FinalizePrompt 的輸出面；命中 Outcome = Blocked，Terminate
│  │  └─ AuditFilter.cs                   # 每次 tool 呼叫一筆 Tool_Invoked；寫不進去不影響呼叫
│  ├─ Orchestration/
│  │  ├─ IPromptOrchestrator.cs           # + ProtocolViolationException
│  │  ├─ TurnContext.cs                   # 一輪的可變狀態：outcome、tool 計數、事件 writer；FacetStateParser
│  │  ├─ AgentKernelFactory.cs            # 每輪建 kernel，函式清單依 ToolSetBuilder 過濾
│  │  ├─ ToolSetBuilder.cs                # + ToolNames
│  │  ├─ SystemPromptBuilder.cs           # template + facets + session 事實 + ledger 注入；SHA-256
│  │  ├─ HistoryTrimmer.cs                # call args 壓縮 + 輪次截斷
│  │  ├─ AgenticOrchestrator.cs
│  │  └─ StateMachineOrchestrator.cs      # NotImplementedException 空殼
│  ├─ Streaming/
│  │  ├─ AgentEvent.cs                    # 事件型別
│  │  └─ SseWriter.cs
│  ├─ Endpoints/
│  │  ├─ SessionEndpoints.cs              # POST /api/sessions, POST .../messages, POST .../save-to-shared
│  │  └─ ReferenceEndpoints.cs            # GET /api/config/facets, GET /api/presets/{id}, GET /health
│  └─ Prompts/system.md
└─ PromptCopilot.Api.Tests/
   ├─ PromptCopilot.Api.Tests.csproj
   ├─ IntegrationFact.cs                  # 沒設 PC_INTEGRATION=1 就 Skip
   ├─ Fakes/FakeChatCompletion.cs         # 腳本化的 IChatCompletionService
   ├─ Configuration/FacetCatalogTests.cs
   ├─ Sessions/SessionTests.cs
   ├─ Sessions/PresetLedgerTests.cs
   ├─ Data/RepositoryIntegrationTests.cs
   ├─ Llm/GeminiEmbeddingClientTests.cs
   ├─ Llm/ResilientChatCompletionTests.cs
   ├─ Safety/SafetyGuardTests.cs
   ├─ Plugins/AskCleanerTests.cs
   ├─ Plugins/DialogPluginTests.cs
   ├─ Orchestration/ToolSetBuilderTests.cs
   ├─ Orchestration/HistoryTrimmerTests.cs
   ├─ Orchestration/SystemPromptBuilderTests.cs
   ├─ Orchestration/AgenticOrchestratorTests.cs
   ├─ Filters/FiltersTests.cs
   ├─ Streaming/SseWriterTests.cs
   ├─ Endpoints/EndpointTests.cs
   └─ Llm/GeminiContractTests.cs           # [IntegrationFact]，真打 Gemini（主規格 §12.2）
docs/
├─ eval-cases.md                          # 主規格 §12.3 + 多輪 §8.3
└─ superpowers/specs/2026-09-21-genai-prompt-copilot-design.md   # Task 1 改寫
```

一輪的資料流（實作時對照）：

```text
POST /api/sessions/{id}/messages { text }
  → SessionStore.TryGet → session.Lock（拿不到 409）
  → SafetyGuard.CheckAsync(text)            blocked → 發 blocked 事件，結束（不進 kernel）
  → snapshot = session.Snapshot()
  → tools = ToolSetBuilder.Build(session, guard.WantsAutoComplete)
  → kernel = AgentKernelFactory.Create(turn, tools)：plugins 依 tools 過濾、Data["turn"] = TurnContext、
       filters 由外到內 Audit → Budget → OutputSafety → Terminal
  → history += user(text); systemPrompt = SystemPromptBuilder.Build(session)
  → chat.GetChatMessageContentsAsync(history, FunctionChoiceBehavior.Auto(), kernel)   ← ResilientChatCompletion 包在外面重試
       SK 自己迴圈：LLM 叫 tool → filter 鏈 → invoke → 結果進 history → 再叫 LLM …
       Budget 超限：Outcome=BudgetExhausted、Terminate；OutputSafety 命中：Outcome=Blocked、Terminate
       Terminal：plugin 設了 Outcome 就 Terminate
  → outcome == null → 純文字補救（多輪 §5.3）
  → outcome is BudgetExhausted → 強制定稿：只掛 FinalizePrompt 再跑一次
  → 成功：session 套用 outcome、HistoryTrimmer、發 final 事件
  → 任何例外／取消：session.Restore(snapshot)、發 error 事件
```

---

## Task 1: 把多輪設計併回主規格

**Files:**
- Modify: `docs/superpowers/specs/2026-09-21-genai-prompt-copilot-design.md`（§4.2、§4.3、§4.4、§4.5、§4.6、§4.7、§4.9、§5.4、§6.2、§7、§10.2、§11.1、§11.3、§12.1、§12.3、§14、§15）
- Modify: `docs/superpowers/specs/2026-09-22-multi-turn-dialogue-design.md`（狀態列改「已併入主規格」）

多輪設計 §9 有逐段的改寫清單，這個任務就是照表執行。沒有程式碼，但它決定後面每個任務讀的是哪一份規格，所以放第一。

- [ ] **Step 1: 逐段改寫主規格**

照多輪設計 §9 的表，每一列做一次。改寫時**把多輪設計對應段落的內容搬進去**，不是加一句「見多輪設計」。具體：

| 主規格 | 搬入的內容 |
| :--- | :--- |
| §4.2 | `AskUser` 換成多輪 §4.2 簽名（`preamble` + `asks[1–3]` + `facetStates`）；新增 `DialogPlugin.Discuss` 列（多輪 §4.1）；`options` 形狀 `{label, tags, presetId?}` 說明 |
| §4.3 | 換成多輪 §3.3 的規則塊（含 `Discuss` 條件、`wantsAutoComplete` 同時移除 `AskUser` 與 `Discuss`、`Discuss` 不需 `Profile`） |
| §4.4 | `Session` 加 `DiscussStreak`、`PresetLedger`；狀態轉移換成多輪 §3.4 表；「`Finalized` 後使用者要求修改」補「純討論走 `Discuss`，不出新定稿卡」 |
| §4.5 | `TerminalToolFilter` 加 `Discuss`；`OutputSafetyFilter` 範圍換成多輪 §5.2（含命中時回滾） |
| §4.6 | 第一列換成多輪 §5.3；加「`Finalized` 下 `Discuss` 變更 facet → 結構化錯誤」「`asks` 清洗後為空 → 結構化錯誤」；逾時列改成「任何失敗 → 回滾至本輪開始前（多輪 §5.6）」；加 LLM 呼叫三層重試列；逾時 60s → 120s |
| §4.7 | 換成多輪 §6.3（call args 壓縮 + 最近 10 輪截斷） |
| §4.9 | 加多輪 §6.2 的注入段；加兩條 system prompt 要求 |
| §5.4 | `AutoFill` 段末補一句：使用者透過討論把某項變成 `covered` 或 `waived`，`AutoFill` 即管不到它 |
| §6.2 | 範圍換成多輪 §5.2；加 §5.5 上游攔截（重試 1 次）與 `Blocked_Upstream` |
| §7 | `event_type` 清單加 `Turn_Failed`、`Blocked_Upstream` |
| §10.2 | `final` 換成多輪 §5.1；`error`／`blocked` 的前端反應改「保留訊息 + 重試按鈕，原文填回輸入框」 |
| §11.1 | 追問卡多維度版；`message` 氣泡與參考方向；chip 累積與前綴；重試按鈕 |
| §11.3 | reducer 加 turn snapshot / restore |
| §12.1 | 補多輪 §8.1 全部 |
| §12.3 | 補多輪 §8.3 的 1–10 條（編號接在既有 13 條之後：14–23） |
| §14 | 第 2 列驗收改「Swagger 打完整一輪：追問 → 討論 → 回答 → 定稿 → 討論 → 修改；上游攔截後 session 可繼續」 |
| §15 | 加多輪 §10 決定紀錄的每一列 |

- [ ] **Step 2: 多輪設計狀態列改寫**

第 4 行改成：

```markdown
狀態：已併入主規格（2026-09-22）；本文件保留為推導紀錄，以主規格為準
```

- [ ] **Step 3: 自查**

在主規格裡搜尋 `suggestedOptions`——應該只剩 §15 決定紀錄或歷史敘述，§4.2 與 §10.2 不該再有。搜尋 `不重試`——§6.2 不該有（現在是重試 1 次）。

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/
git commit -m "docs(spec): merge multi-turn dialogue design into main spec

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 2: 專案骨架與組態

**Files:**
- Create: `src/PromptCopilot.sln`
- Create: `src/PromptCopilot.Api/PromptCopilot.Api.csproj`
- Create: `src/PromptCopilot.Api/Program.cs`（最小可跑）
- Create: `src/PromptCopilot.Api/appsettings.json`
- Create: `src/PromptCopilot.Api/Configuration/Options.cs`
- Create: `src/PromptCopilot.Api.Tests/PromptCopilot.Api.Tests.csproj`
- Create: `src/PromptCopilot.Api.Tests/Configuration/OptionsTests.cs`
- Modify: `.gitignore`（已含 `bin/` `obj/`，確認即可）

**Interfaces:**
- Produces: `LlmOptions`、`EmbeddingOptions`、`OrchestratorOptions`、`DatabaseOptions`（record，section 名稱同類別名去掉 `Options`）；後面每個任務用 `IOptions<T>` 取

- [ ] **Step 1: 建 solution 與兩個專案**

```bash
cd src
dotnet new sln -n PromptCopilot
dotnet new web -n PromptCopilot.Api -f net10.0
dotnet new xunit -n PromptCopilot.Api.Tests -f net10.0
dotnet sln add PromptCopilot.Api PromptCopilot.Api.Tests
dotnet add PromptCopilot.Api.Tests reference PromptCopilot.Api
```

- [ ] **Step 2: 加套件**

```bash
cd src/PromptCopilot.Api
dotnet add package Microsoft.SemanticKernel
dotnet add package Microsoft.SemanticKernel.Connectors.Google --prerelease
dotnet add package Npgsql
dotnet add package Pgvector
dotnet add package YamlDotNet
dotnet add package Swashbuckle.AspNetCore
cd ../PromptCopilot.Api.Tests
dotnet add package Microsoft.Extensions.Configuration.Json
```

`Connectors.Google` 目前只有 alpha 版，`--prerelease` 是必要的。若 `dotnet add` 抱怨版本衝突，先 `dotnet add package Microsoft.SemanticKernel` 再加 Google，讓核心版本被 Google 套件的相依帶上來。

- [ ] **Step 3: 寫失敗的測試——組態能綁定**

`src/PromptCopilot.Api.Tests/Configuration/OptionsTests.cs`：

```csharp
using Microsoft.Extensions.Configuration;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Tests.Configuration;

public class OptionsTests
{
    private static IConfiguration Config(params (string key, string value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.key, p => (string?)p.value)).Build();

    [Fact]
    public void Orchestrator_defaults_match_spec()
    {
        var o = new OrchestratorOptions();
        Assert.Equal(2, o.MaxAskCount);
        Assert.Equal(8, o.MaxDiscussStreak);
        Assert.Equal(8, o.MaxToolCallsPerTurn);
        Assert.Equal(120, o.TurnTimeoutSeconds);
        Assert.Equal(10, o.HistoryTurns);
        Assert.Equal(24, o.OfferedOptionsLimit);
    }

    [Fact]
    public void Llm_retry_defaults_match_spec()
    {
        var o = new LlmOptions();
        Assert.Equal(3, o.TransportRetries);
        Assert.Equal(3, o.UnusableRetries);
        Assert.Equal(1, o.ContentBlockRetries);
        Assert.Equal("gemini-3.5-flash-lite", o.Model);
    }

    [Fact]
    public void Options_bind_from_configuration_sections()
    {
        var cfg = Config(("Llm:ApiKey", "k"), ("Llm:TransportRetries", "5"), ("Embedding:Dimensions", "768"),
                         ("Orchestrator:MaxDiscussStreak", "3"), ("Database:ConnectionString", "Host=x"));
        var llm = cfg.GetSection(LlmOptions.Section).Get<LlmOptions>()!;
        var emb = cfg.GetSection(EmbeddingOptions.Section).Get<EmbeddingOptions>()!;
        var orch = cfg.GetSection(OrchestratorOptions.Section).Get<OrchestratorOptions>()!;
        var db = cfg.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>()!;
        Assert.Equal("k", llm.ApiKey);
        Assert.Equal(5, llm.TransportRetries);
        Assert.Equal(768, emb.Dimensions);
        Assert.Equal(3, orch.MaxDiscussStreak);
        Assert.Equal("Host=x", db.ConnectionString);
    }
}
```

- [ ] **Step 4: 跑測試確認失敗**

```bash
cd src && dotnet test --no-restore 2>&1 | tail -5
```

Expected: 編譯錯誤 `The type or namespace name 'OrchestratorOptions' could not be found`。

- [ ] **Step 5: 寫 Options**

`src/PromptCopilot.Api/Configuration/Options.cs`：

```csharp
namespace PromptCopilot.Api.Configuration;

public sealed class LlmOptions
{
    public const string Section = "Llm";
    public string Provider { get; set; } = "Gemini";
    public string Model { get; set; } = "gemini-3.5-flash-lite";
    public string ApiKey { get; set; } = "";
    public int TransportRetries { get; set; } = 3;
    public int UnusableRetries { get; set; } = 3;
    public int ContentBlockRetries { get; set; } = 1;
    /// <summary>傳輸重試的第一次退避；之後 ×2。</summary>
    public int TransportBackoffMs { get; set; } = 1000;
}

public sealed class EmbeddingOptions
{
    public const string Section = "Embedding";
    public string Model { get; set; } = "gemini-embedding-001";
    public int Dimensions { get; set; } = 768;
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
}

public sealed class OrchestratorOptions
{
    public const string Section = "Orchestrator";
    public string Mode { get; set; } = "Agentic";
    public int MaxAskCount { get; set; } = 2;
    public int MaxDiscussStreak { get; set; } = 8;
    public int MaxToolCallsPerTurn { get; set; } = 8;
    public int TurnTimeoutSeconds { get; set; } = 120;
    public int HistoryTurns { get; set; } = 10;
    public int OfferedOptionsLimit { get; set; } = 24;
    public int MaxAsksPerCall { get; set; } = 3;
    public int SessionSlidingExpirationMinutes { get; set; } = 120;
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public string ConnectionString { get; set; } = "Host=localhost;Port=5432;Database=prompt_copilot;Username=postgres;Password=postgres";
}
```

`src/PromptCopilot.Api/appsettings.json`：

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "Llm": { "Provider": "Gemini", "Model": "gemini-3.5-flash-lite", "TransportRetries": 3, "UnusableRetries": 3, "ContentBlockRetries": 1, "TransportBackoffMs": 1000 },
  "Embedding": { "Model": "gemini-embedding-001", "Dimensions": 768 },
  "Orchestrator": { "Mode": "Agentic", "MaxAskCount": 2, "MaxDiscussStreak": 8, "MaxToolCallsPerTurn": 8, "TurnTimeoutSeconds": 120, "HistoryTurns": 10, "OfferedOptionsLimit": 24, "MaxAsksPerCall": 3, "SessionSlidingExpirationMinutes": 120 },
  "Database": { "ConnectionString": "Host=localhost;Port=5432;Database=prompt_copilot;Username=postgres;Password=postgres" },
  "Safety": { "Denylist": [] }
}
```

`Program.cs` 先只註冊 options，之後任務再加：

```csharp
using PromptCopilot.Api.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.Section));
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.Section));
builder.Services.Configure<OrchestratorOptions>(builder.Configuration.GetSection(OrchestratorOptions.Section));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

public partial class Program { }
```

- [ ] **Step 6: 跑測試確認通過**

```bash
cd src && dotnet test 2>&1 | tail -3
```

Expected: `Passed! - Failed: 0, Passed: 3`。

- [ ] **Step 7: 設 user-secrets（不入版控）**

```bash
cd src/PromptCopilot.Api
dotnet user-secrets init
dotnet user-secrets set "Llm:ApiKey" "<你的 GEMINI_API_KEY>"
```

- [ ] **Step 8: Commit**

```bash
cd src && git add . ../.gitignore
git commit -m "feat(api): scaffold solution, options, swagger

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 3: FacetCatalog

**Files:**
- Create: `src/PromptCopilot.Api/Configuration/FacetCatalog.cs`
- Test: `src/PromptCopilot.Api.Tests/Configuration/FacetCatalogTests.cs`
- Modify: `src/PromptCopilot.Api/PromptCopilot.Api.csproj`（把 `Configuration/facets.yaml` 與 `Prompts/system.md` 複製到輸出目錄）

**Interfaces:**
- Produces: `FacetCatalog.Load(string path)`；`Facet(string Id, string Label, string Hint, string Dimension)`；`Dimensions: IReadOnlyList<string>`（yaml 順序）；`DimensionLabels: IReadOnlyDictionary<string,string>`；`Facets: IReadOnlyDictionary<string,Facet>`；`Profiles`；`string DimensionOf(string facetId)`；`IReadOnlySet<string> IdsForProfile(string profile)`；`IReadOnlyList<string> FacetsOf(string profile, string dimension)`（不適用回空）；`string DimensionLabel(string dimension, string profile)`；`string PromptListing()`；`string ProfileListing(string profile)`；`bool IsProfile(string)`

這是 Python `facets.py` 的直譯。測試資料直接用真的 `facets.yaml`。

- [ ] **Step 1: csproj 複製 yaml 與 system.md**

在 `PromptCopilot.Api.csproj` 的 `<Project>` 內加：

```xml
<ItemGroup>
  <None Update="Configuration\facets.yaml" CopyToOutputDirectory="PreserveNewest" />
  <None Update="Prompts\system.md" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

`Prompts/system.md` 在 Task 14 才建；先建一個空檔讓 build 不抱怨：`mkdir -p Prompts && echo "" > Prompts/system.md`。

- [ ] **Step 2: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Configuration/FacetCatalogTests.cs`：

```csharp
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Tests.Configuration;

public class FacetCatalogTests
{
    public static FacetCatalog Real() =>
        FacetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Configuration", "facets.yaml"));

    [Fact]
    public void Loads_six_dimensions_in_yaml_order()
    {
        var c = Real();
        Assert.Equal(new[] { "style", "scene", "camera", "appearance", "pose", "clothing" }, c.Dimensions);
        Assert.Equal("風格", c.DimensionLabels["style"]);
    }

    [Fact]
    public void DimensionOf_maps_facet_to_its_dimension() =>
        Assert.Equal("pose", Real().DimensionOf("pose.gaze"));

    [Fact]
    public void FacetsOf_returns_empty_for_inapplicable_dimension()
    {
        var c = Real();
        Assert.Empty(c.FacetsOf("landscape", "clothing"));
        Assert.Equal(new[] { "pose.motion_state", "pose.terrain" }, c.FacetsOf("vehicle", "pose"));
    }

    [Fact]
    public void IdsForProfile_portrait_has_31_ids_and_no_vehicle_facets()
    {
        var ids = Real().IdsForProfile("portrait");
        Assert.Equal(31, ids.Count);
        Assert.DoesNotContain("pose.motion_state", ids);
    }

    [Fact]
    public void DimensionLabel_prefers_profile_override()
    {
        var c = Real();
        Assert.Equal("主體外觀", c.DimensionLabel("appearance", "object"));
        Assert.Equal("人物樣貌", c.DimensionLabel("appearance", "portrait"));
    }

    [Fact]
    public void ProfileListing_only_includes_applicable_facets()
    {
        var text = Real().ProfileListing("landscape");
        Assert.Contains("scene.season", text);
        Assert.DoesNotContain("clothing.", text);
    }

    [Fact]
    public void Load_rejects_profile_referencing_unknown_facet()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            dimensions:
              - key: style
                label: 風格
                facets:
                  - { id: style.genre, label: g, hint: h }
            profiles:
              portrait:
                dimensions:
                  style: [style.genre, style.nope]
            """);
        Assert.Throws<InvalidDataException>(() => FacetCatalog.Load(path));
    }
}
```

- [ ] **Step 3: 跑測試確認失敗**

```bash
cd src && dotnet test --filter FacetCatalogTests 2>&1 | tail -3
```

Expected: 編譯錯誤，`FacetCatalog` 不存在。

- [ ] **Step 4: 實作**

`src/PromptCopilot.Api/Configuration/FacetCatalog.cs`：

```csharp
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PromptCopilot.Api.Configuration;

public sealed record Facet(string Id, string Label, string Hint, string Dimension);

public sealed class FacetCatalog
{
    public IReadOnlyList<string> Dimensions { get; }
    public IReadOnlyDictionary<string, string> DimensionLabels { get; }
    public IReadOnlyDictionary<string, Facet> Facets { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Profiles { get; }
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _profileLabels;

    private FacetCatalog(
        IReadOnlyList<string> dimensions,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, Facet> facets,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> profiles,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> profileLabels)
    {
        Dimensions = dimensions; DimensionLabels = labels; Facets = facets; Profiles = profiles; _profileLabels = profileLabels;
    }

    public bool IsProfile(string profile) => Profiles.ContainsKey(profile);

    public string DimensionOf(string facetId) => Facets[facetId].Dimension;

    public IReadOnlySet<string> IdsForProfile(string profile) =>
        Profiles[profile].Values.SelectMany(x => x).ToHashSet();

    public IReadOnlyList<string> FacetsOf(string profile, string dimension) =>
        Profiles[profile].TryGetValue(dimension, out var ids) ? ids : Array.Empty<string>();

    public string DimensionLabel(string dimension, string profile) =>
        _profileLabels.TryGetValue(profile, out var o) && o.TryGetValue(dimension, out var l) ? l
        : DimensionLabels.GetValueOrDefault(dimension, dimension);

    /// <summary>全部維度與 facet，給 SetProfile 之前的 system prompt。</summary>
    public string PromptListing()
    {
        var lines = new List<string>();
        foreach (var dim in Dimensions)
        {
            lines.Add($"[{dim}] {DimensionLabels[dim]}");
            foreach (var f in Facets.Values.Where(f => f.Dimension == dim))
                lines.Add($"  - {f.Id}：{f.Label}（例：{f.Hint}）");
        }
        return string.Join("\n", lines);
    }

    /// <summary>只列該 profile 適用的維度與 facet；維度名用 profile 的覆寫。</summary>
    public string ProfileListing(string profile)
    {
        var lines = new List<string>();
        foreach (var dim in Dimensions)
        {
            var ids = FacetsOf(profile, dim);
            if (ids.Count == 0) continue;
            lines.Add($"[{dim}] {DimensionLabel(dim, profile)}");
            foreach (var id in ids)
                lines.Add($"  - {id}：{Facets[id].Label}（例：{Facets[id].Hint}）");
        }
        return string.Join("\n", lines);
    }

    // ---- yaml 形狀 ----
    private sealed class Root { public List<DimNode> Dimensions { get; set; } = new(); public Dictionary<string, ProfileNode> Profiles { get; set; } = new(); }
    private sealed class DimNode { public string Key { get; set; } = ""; public string Label { get; set; } = ""; public List<FacetNode> Facets { get; set; } = new(); }
    private sealed class FacetNode { public string Id { get; set; } = ""; public string Label { get; set; } = ""; public string Hint { get; set; } = ""; }
    private sealed class ProfileNode { public Dictionary<string, string>? Labels { get; set; } public Dictionary<string, List<string>> Dimensions { get; set; } = new(); }

    public static FacetCatalog Load(string path)
    {
        var yaml = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build();
        var root = yaml.Deserialize<Root>(File.ReadAllText(path));

        var dims = new List<string>();
        var labels = new Dictionary<string, string>();
        var facets = new Dictionary<string, Facet>();
        foreach (var d in root.Dimensions)
        {
            dims.Add(d.Key); labels[d.Key] = d.Label;
            foreach (var f in d.Facets) facets[f.Id] = new Facet(f.Id, f.Label, f.Hint, d.Key);
        }
        var profiles = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>();
        var profileLabels = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var (name, p) in root.Profiles)
        {
            profiles[name] = p.Dimensions.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.AsReadOnly());
            profileLabels[name] = p.Labels ?? new Dictionary<string, string>();
        }
        var unknown = profiles.Values.SelectMany(d => d.Values).SelectMany(x => x)
            .Where(id => !facets.ContainsKey(id)).Distinct().Order().ToList();
        if (unknown.Count > 0)
            throw new InvalidDataException($"facets.yaml profiles 引用了不存在的 facet id: {string.Join(", ", unknown)}");
        return new FacetCatalog(dims, labels, facets, profiles, profileLabels);
    }
}
```

- [ ] **Step 5: 跑測試確認通過**

```bash
cd src && dotnet test --filter FacetCatalogTests 2>&1 | tail -3
```

Expected: `Passed: 7`。

- [ ] **Step 6: Commit**

```bash
cd src && git add . && git commit -m "feat(api): FacetCatalog mirrors pipeline/facets.py

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 4: Session、PresetLedger、SessionStore

**Files:**
- Create: `src/PromptCopilot.Api/Sessions/PresetLedger.cs`
- Create: `src/PromptCopilot.Api/Sessions/Session.cs`
- Create: `src/PromptCopilot.Api/Sessions/SessionStore.cs`
- Test: `src/PromptCopilot.Api.Tests/Sessions/PresetLedgerTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Sessions/SessionTests.cs`

**Interfaces:**
- Produces:
  - `enum FacetState { Covered, Missing, Waived, NotApplicable }`、`enum SessionStatus { Collecting, Finalized }`
  - `record LedgerHit(string Dimension, double Dist, bool Grounded)`；`record OfferedRef(int TurnIndex, string? Dimension, string Label)`
  - `class LedgerEntry { long Id; string Title; string PromptSnippet; string? NegativeSnippet; IReadOnlyList<string> FacetIds; string? ImageUrl; List<LedgerHit> Hits; List<OfferedRef> OfferedAs; LedgerEntry Clone() }`
  - `class PresetLedger { void Record(LedgerEntry seed, LedgerHit hit); bool Contains(long id); LedgerEntry? Get(long id); void MarkOffered(long id, OfferedRef r); IReadOnlyList<LedgerEntry> RecentlyOffered(int limit); PresetLedger Clone() }`
  - `record FinalPrompt(string Positive, string Negative, string Tips)`
  - `class Session { string Id; SessionStatus Status; string? Profile; int AskCount; int DiscussStreak; bool AutoFill; Dictionary<string,FacetState> FacetStates; Dictionary<string,string> FacetNotes; ChatHistory ChatHistory; PresetLedger Ledger; FinalPrompt? LastFinal; int TurnIndex; SemaphoreSlim Lock; SessionSnapshot Snapshot(); void Restore(SessionSnapshot); void ApplyProfile(string, FacetCatalog); void ApplyFacetStates(IReadOnlyDictionary<string,FacetState>, FacetCatalog); void RecordAsk(); void RecordDiscuss(); void RecordFinalize(FinalPrompt); IReadOnlySet<string> GroundedDimensions(FacetCatalog) }`
  - `class SessionStore(IMemoryCache, TimeSpan sliding) { Session Create(); Session? TryGet(string id) }`

- [ ] **Step 1: 寫失敗的測試——ledger**

`src/PromptCopilot.Api.Tests/Sessions/PresetLedgerTests.cs`：

```csharp
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Tests.Sessions;

public class PresetLedgerTests
{
    private static LedgerEntry Seed(long id, string? title = null) => new()
    {
        Id = id, Title = title ?? $"t{id}", PromptSnippet = "a, b", NegativeSnippet = null, FacetIds = new[] { "style.genre" }
    };

    [Fact]
    public void Record_appends_hits_and_never_overwrites_existing()
    {
        var l = new PresetLedger();
        l.Record(Seed(1), new LedgerHit("style", 0.2, true));
        l.Record(Seed(1, "changed"), new LedgerHit("scene", 0.3, false));
        var e = l.Get(1)!;
        Assert.Equal("t1", e.Title);
        Assert.Equal(2, e.Hits.Count);
    }

    [Fact]
    public void RecentlyOffered_returns_only_offered_sorted_by_latest_turn_and_capped()
    {
        var l = new PresetLedger();
        for (long i = 1; i <= 5; i++) l.Record(Seed(i), new LedgerHit("style", 0.2, true));
        l.MarkOffered(2, new OfferedRef(1, "style", "A"));
        l.MarkOffered(4, new OfferedRef(3, "camera", "B"));
        l.MarkOffered(5, new OfferedRef(2, "camera", "C"));
        var recent = l.RecentlyOffered(limit: 2);
        Assert.Equal(new long[] { 4, 5 }, recent.Select(e => e.Id));
    }

    [Fact]
    public void MarkOffered_ignores_unknown_ids()
    {
        var l = new PresetLedger();
        l.MarkOffered(99, new OfferedRef(1, null, "x"));
        Assert.Empty(l.RecentlyOffered(10));
    }

    [Fact]
    public void Clone_is_deep_for_hits_and_offered()
    {
        var l = new PresetLedger();
        l.Record(Seed(1), new LedgerHit("style", 0.2, true));
        var c = l.Clone();
        c.Record(Seed(1), new LedgerHit("scene", 0.1, false));
        c.MarkOffered(1, new OfferedRef(1, "style", "A"));
        Assert.Single(l.Get(1)!.Hits);
        Assert.Empty(l.Get(1)!.OfferedAs);
    }
}
```

- [ ] **Step 2: 寫失敗的測試——session**

`src/PromptCopilot.Api.Tests/Sessions/SessionTests.cs`：

```csharp
using Microsoft.Extensions.Caching.Memory;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Sessions;

public class SessionTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();
    private static Session New() => new("s1");

    [Fact]
    public void ApplyProfile_resets_facets_to_missing_but_keeps_counters()
    {
        var s = New();
        s.RecordAsk(); s.RecordDiscuss(); s.AutoFill = true;
        s.ApplyProfile("portrait", Catalog);
        Assert.Equal(31, s.FacetStates.Count);
        Assert.All(s.FacetStates.Values, v => Assert.Equal(FacetState.Missing, v));
        Assert.Equal(1, s.AskCount);
        Assert.Equal(1, s.DiscussStreak);
        Assert.True(s.AutoFill);
    }

    [Fact]
    public void ApplyFacetStates_ignores_ids_outside_profile()
    {
        var s = New(); s.ApplyProfile("landscape", Catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["scene.season"] = FacetState.Covered, ["clothing.head"] = FacetState.Covered }, Catalog);
        Assert.Equal(FacetState.Covered, s.FacetStates["scene.season"]);
        Assert.False(s.FacetStates.ContainsKey("clothing.head"));
    }

    [Fact]
    public void GroundedDimensions_derives_from_covered_only()
    {
        var s = New(); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["pose.gaze"] = FacetState.Covered, ["style.genre"] = FacetState.Waived }, Catalog);
        Assert.Equal(new[] { "pose" }, s.GroundedDimensions(Catalog));
    }

    [Fact]
    public void RecordDiscuss_increments_streak_only_while_collecting()
    {
        var s = New();
        s.RecordDiscuss(); Assert.Equal(1, s.DiscussStreak);
        s.RecordFinalize(new FinalPrompt("p", "n", "t"));
        Assert.Equal(SessionStatus.Finalized, s.Status);
        Assert.Equal(0, s.DiscussStreak);
        s.RecordDiscuss(); Assert.Equal(0, s.DiscussStreak);
    }

    [Fact]
    public void RecordAsk_does_not_touch_streak()
    {
        var s = New(); s.RecordDiscuss(); s.RecordAsk();
        Assert.Equal(1, s.AskCount); Assert.Equal(1, s.DiscussStreak);
    }

    [Fact]
    public void Snapshot_restore_reverts_everything_including_history_and_ledger()
    {
        var s = New(); s.ApplyProfile("portrait", Catalog);
        s.ChatHistory.AddUserMessage("hi");
        var snap = s.Snapshot();

        s.RecordAsk(); s.RecordDiscuss(); s.AutoFill = true;
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["pose.gaze"] = FacetState.Covered }, Catalog);
        s.FacetNotes["clothing.footwear"] = "使用者委託此項";
        s.ChatHistory.AddAssistantMessage("x"); s.ChatHistory.AddUserMessage("y");
        s.Ledger.Record(new LedgerEntry { Id = 7, Title = "t", PromptSnippet = "a", FacetIds = Array.Empty<string>() }, new LedgerHit("style", 0.1, true));
        s.RecordFinalize(new FinalPrompt("p", "n", "t"));

        s.Restore(snap);
        Assert.Equal(0, s.AskCount); Assert.Equal(0, s.DiscussStreak); Assert.False(s.AutoFill);
        Assert.Equal(SessionStatus.Collecting, s.Status); Assert.Null(s.LastFinal);
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
        Assert.Single(s.ChatHistory);
        Assert.False(s.Ledger.Contains(7));
        Assert.Empty(s.FacetNotes);
    }

    [Fact]
    public void SessionStore_creates_and_finds_by_id()
    {
        var store = new SessionStore(new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMinutes(1));
        var s = store.Create();
        Assert.Same(s, store.TryGet(s.Id));
        Assert.Null(store.TryGet("nope"));
    }
}
```

- [ ] **Step 3: 跑測試確認失敗**

```bash
cd src && dotnet test --filter "PresetLedgerTests|SessionTests" 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 4: 實作 PresetLedger**

`src/PromptCopilot.Api/Sessions/PresetLedger.cs`：

```csharp
namespace PromptCopilot.Api.Sessions;

public sealed record LedgerHit(string Dimension, double Dist, bool Grounded);
public sealed record OfferedRef(int TurnIndex, string? Dimension, string Label);

public sealed class LedgerEntry
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public required string PromptSnippet { get; init; }
    public string? NegativeSnippet { get; init; }
    public required IReadOnlyList<string> FacetIds { get; init; }
    public string? ImageUrl { get; init; }
    public List<LedgerHit> Hits { get; } = new();
    public List<OfferedRef> OfferedAs { get; } = new();

    public LedgerEntry Clone()
    {
        var c = new LedgerEntry { Id = Id, Title = Title, PromptSnippet = PromptSnippet, NegativeSnippet = NegativeSnippet, FacetIds = FacetIds, ImageUrl = ImageUrl };
        c.Hits.AddRange(Hits); c.OfferedAs.AddRange(OfferedAs);
        return c;
    }
}

/// <summary>session 內看過的 preset。去重歸屬（主規格 §9）與「攤給使用者看過的選項」（多輪 §6.1）共用這一本。</summary>
public sealed class PresetLedger
{
    private readonly Dictionary<long, LedgerEntry> _entries = new();

    public void Record(LedgerEntry seed, LedgerHit hit)
    {
        if (!_entries.TryGetValue(seed.Id, out var e)) { e = seed; _entries[seed.Id] = e; }
        e.Hits.Add(hit);
    }

    public bool Contains(long id) => _entries.ContainsKey(id);
    public LedgerEntry? Get(long id) => _entries.GetValueOrDefault(id);

    public void MarkOffered(long id, OfferedRef r)
    {
        if (_entries.TryGetValue(id, out var e)) e.OfferedAs.Add(r);
    }

    /// <summary>OfferedAs 非空者，依最近一次 offered 的 turn 由新到舊，取前 limit。</summary>
    public IReadOnlyList<LedgerEntry> RecentlyOffered(int limit) =>
        _entries.Values.Where(e => e.OfferedAs.Count > 0)
            .OrderByDescending(e => e.OfferedAs.Max(o => o.TurnIndex)).ThenBy(e => e.Id)
            .Take(limit).ToList();

    public PresetLedger Clone()
    {
        var c = new PresetLedger();
        foreach (var (k, v) in _entries) c._entries[k] = v.Clone();
        return c;
    }
}
```

- [ ] **Step 5: 實作 Session 與 SessionStore**

`src/PromptCopilot.Api/Sessions/Session.cs`：

```csharp
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Sessions;

public enum FacetState { Covered, Missing, Waived, NotApplicable }
public enum SessionStatus { Collecting, Finalized }
public sealed record FinalPrompt(string Positive, string Negative, string Tips);

public sealed record SessionSnapshot(
    SessionStatus Status, string? Profile, int AskCount, int DiscussStreak, bool AutoFill,
    Dictionary<string, FacetState> FacetStates, Dictionary<string, string> FacetNotes, int HistoryCount, PresetLedger Ledger, FinalPrompt? LastFinal, int TurnIndex);

public sealed class Session
{
    public string Id { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public SessionStatus Status { get; private set; } = SessionStatus.Collecting;
    public string? Profile { get; private set; }
    public int AskCount { get; private set; }
    public int DiscussStreak { get; private set; }
    public bool AutoFill { get; set; }
    public Dictionary<string, FacetState> FacetStates { get; private set; } = new();
    public ChatHistory ChatHistory { get; } = new();
    public PresetLedger Ledger { get; private set; } = new();
    /// <summary>facet 級的備註，例如「使用者委託此項」。跨輪保留，隨 profile 重設。</summary>
    public Dictionary<string, string> FacetNotes { get; private set; } = new();
    public FinalPrompt? LastFinal { get; private set; }
    public int TurnIndex { get; set; }
    public SemaphoreSlim Lock { get; } = new(1, 1);

    public Session(string id) => Id = id;

    public void ApplyProfile(string profile, FacetCatalog catalog)
    {
        Profile = profile;
        FacetStates = catalog.IdsForProfile(profile).ToDictionary(id => id, _ => FacetState.Missing);
        FacetNotes = new();
    }

    /// <summary>只收 profile 適用的 facet；其餘丟掉（不信 LLM 自述）。</summary>
    public void ApplyFacetStates(IReadOnlyDictionary<string, FacetState> updates, FacetCatalog catalog)
    {
        if (Profile is null) return;
        var applicable = catalog.IdsForProfile(Profile);
        foreach (var (id, state) in updates)
            if (applicable.Contains(id)) FacetStates[id] = state;
    }

    /// <summary>grounded 由 covered 推導，不由 LLM 回報。</summary>
    public IReadOnlySet<string> GroundedDimensions(FacetCatalog catalog) =>
        FacetStates.Where(kv => kv.Value == FacetState.Covered).Select(kv => catalog.DimensionOf(kv.Key)).ToHashSet();

    public void RecordAsk() => AskCount++;
    public void RecordDiscuss() { if (Status == SessionStatus.Collecting) DiscussStreak++; }
    public void RecordFinalize(FinalPrompt final) { LastFinal = final; Status = SessionStatus.Finalized; DiscussStreak = 0; }

    public SessionSnapshot Snapshot() => new(Status, Profile, AskCount, DiscussStreak, AutoFill,
        new Dictionary<string, FacetState>(FacetStates), new Dictionary<string, string>(FacetNotes), ChatHistory.Count, Ledger.Clone(), LastFinal, TurnIndex);

    public void Restore(SessionSnapshot s)
    {
        Status = s.Status; Profile = s.Profile; AskCount = s.AskCount; DiscussStreak = s.DiscussStreak; AutoFill = s.AutoFill;
        FacetStates = new Dictionary<string, FacetState>(s.FacetStates);
        FacetNotes = new Dictionary<string, string>(s.FacetNotes);
        while (ChatHistory.Count > s.HistoryCount) ChatHistory.RemoveAt(ChatHistory.Count - 1);
        Ledger = s.Ledger.Clone(); LastFinal = s.LastFinal; TurnIndex = s.TurnIndex;
    }
}
```

`src/PromptCopilot.Api/Sessions/SessionStore.cs`：

```csharp
using Microsoft.Extensions.Caching.Memory;

namespace PromptCopilot.Api.Sessions;

public sealed class SessionStore
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _sliding;

    public SessionStore(IMemoryCache cache, TimeSpan sliding) { _cache = cache; _sliding = sliding; }

    public Session Create()
    {
        var s = new Session(Guid.NewGuid().ToString("N"));
        _cache.Set(s.Id, s, new MemoryCacheEntryOptions { SlidingExpiration = _sliding });
        return s;
    }

    public Session? TryGet(string id) => _cache.TryGetValue(id, out Session? s) ? s : null;
}
```

- [ ] **Step 6: 跑測試確認通過**

```bash
cd src && dotnet test --filter "PresetLedgerTests|SessionTests" 2>&1 | tail -3
```

Expected: `Passed: 11`。

- [ ] **Step 7: Commit**

```bash
cd src && git add . && git commit -m "feat(api): Session with snapshot/restore, PresetLedger, SessionStore

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 5: 資料存取（Npgsql + Pgvector）

**Files:**
- Create: `src/PromptCopilot.Api/Data/PresetRepository.cs`
- Create: `src/PromptCopilot.Api/Data/HistoryRepository.cs`
- Create: `src/PromptCopilot.Api/Data/AuditRepository.cs`
- Create: `src/PromptCopilot.Api.Tests/IntegrationFact.cs`
- Test: `src/PromptCopilot.Api.Tests/Data/RepositoryIntegrationTests.cs`
- Modify: `src/PromptCopilot.Api/Program.cs`（註冊 `NpgsqlDataSource` 與三個 repository）

**Interfaces:**
- Produces:
  - `record PresetHit(long Id, string Title, string Category, IReadOnlyList<string> FacetIds, string PromptSnippet, string? NegativeSnippet, string? ImageUrl, double Dist)`
  - `record PresetDetail(long Id, string Title, string Category, string Description, IReadOnlyList<string> Tags, IReadOnlyList<string> FacetIds, string PromptSnippet, string? NegativeSnippet, string? ImageUrl)`
  - `class PresetRepository(NpgsqlDataSource) { Task<IReadOnlyList<PresetHit>> SearchAsync(float[] query, IReadOnlyList<string> facetIds, int k, CancellationToken); Task<long> PoolSizeAsync(IReadOnlyList<string> facetIds, CancellationToken); Task<PresetDetail?> GetAsync(long id, CancellationToken) }`
  - `record HistoryHit(Guid Id, string UserIntent, string PositivePrompt, string SubjectProfile, double Dist)`；`record HistoryInsert(string UserIntent, string Positive, string Negative, string Profile, string? CompletenessJson, float[] IntentEmbedding)`
  - `class HistoryRepository(NpgsqlDataSource) { Task<IReadOnlyList<HistoryHit>> SearchAsync(float[] query, string profile, int k, CancellationToken); Task<Guid> InsertAsync(HistoryInsert, CancellationToken) }`
  - `record AuditEntry(string? SessionId, int? TurnIndex, string EventType, string? PromptVersion = null, string? RawInput = null, string? PayloadJson = null, int? PromptTokens = null, int? CompletionTokens = null, int? LatencyMs = null)`
  - `class AuditRepository(NpgsqlDataSource) { Task WriteAsync(AuditEntry, CancellationToken) }`

SQL 與 Python `retrieval.py` 的 `PRESETS_SQL` / `POOL_SQL` / `HISTORIES_SQL` 逐字對應（多了 `image_url`，前端抽屜要用）。

- [ ] **Step 1: IntegrationFact**

`src/PromptCopilot.Api.Tests/IntegrationFact.cs`：

```csharp
namespace PromptCopilot.Api.Tests;

/// <summary>需要真的 DB 或 Gemini 的測試。沒設 PC_INTEGRATION=1 就 Skip，CI 預設不跑。</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PC_INTEGRATION") != "1")
            Skip = "set PC_INTEGRATION=1 to run (needs docker db / Gemini key)";
    }
}

public static class TestEnv
{
    public static string Db => Environment.GetEnvironmentVariable("PC_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=prompt_copilot;Username=postgres;Password=postgres";
    public static string? GeminiKey => Environment.GetEnvironmentVariable("GEMINI_API_KEY");
}
```

- [ ] **Step 2: 寫失敗的測試（整合）**

`src/PromptCopilot.Api.Tests/Data/RepositoryIntegrationTests.cs`：

```csharp
using Npgsql;
using Pgvector.Npgsql;
using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Tests.Data;

[Trait("Category", "Integration")]
public class RepositoryIntegrationTests : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private static readonly string Ref = $"test:{Guid.NewGuid():N}";
    private static float[] Unit(int hot) { var v = new float[768]; v[hot] = 1f; return v; }

    public async Task InitializeAsync()
    {
        var b = new NpgsqlDataSourceBuilder(TestEnv.Db); b.UseVector(); _ds = b.Build();
        await using var cmd = _ds.CreateCommand("""
            INSERT INTO prompt_knowledge_presets (source_ref, title, category, description, tags, facet_ids, prompt_snippet, negative_snippet, preset_embedding)
            VALUES (@r, '測試片段', 'Style', 'd', ARRAY['x'], ARRAY['style.genre'], 'photo realism', NULL, @e)
            """);
        cmd.Parameters.AddWithValue("r", Ref);
        cmd.Parameters.AddWithValue("e", new Pgvector.Vector(Unit(0)));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var cmd = _ds.CreateCommand("DELETE FROM prompt_knowledge_presets WHERE source_ref = @r");
        cmd.Parameters.AddWithValue("r", Ref);
        await cmd.ExecuteNonQueryAsync();
        await using var cmd2 = _ds.CreateCommand("DELETE FROM shared_prompt_histories WHERE user_intent = @i");
        cmd2.Parameters.AddWithValue("i", Ref);
        await cmd2.ExecuteNonQueryAsync();
        await _ds.DisposeAsync();
    }

    [IntegrationFact]
    public async Task Preset_search_filters_by_facets_and_orders_by_distance()
    {
        var repo = new PresetRepository(_ds);
        var hits = await repo.SearchAsync(Unit(0), new[] { "style.genre" }, 5, default);
        Assert.Contains(hits, h => h.Title == "測試片段" && h.Dist < 1e-6);
        Assert.Empty(await repo.SearchAsync(Unit(0), new[] { "nope.facet" }, 5, default));
        Assert.True(await repo.PoolSizeAsync(new[] { "style.genre" }, default) >= 1);
    }

    [IntegrationFact]
    public async Task Preset_get_returns_detail_or_null()
    {
        var repo = new PresetRepository(_ds);
        var hit = (await repo.SearchAsync(Unit(0), new[] { "style.genre" }, 5, default)).First(h => h.Title == "測試片段");
        var d = await repo.GetAsync(hit.Id, default);
        Assert.NotNull(d); Assert.Equal("photo realism", d!.PromptSnippet);
        Assert.Null(await repo.GetAsync(-1, default));
    }

    [IntegrationFact]
    public async Task History_insert_then_search_by_profile()
    {
        var repo = new HistoryRepository(_ds);
        var id = await repo.InsertAsync(new HistoryInsert(Ref, "1girl", "lowres", "portrait", """{"style":0}""", Unit(1)), default);
        Assert.NotEqual(Guid.Empty, id);
        var hits = await repo.SearchAsync(Unit(1), "portrait", 3, default);
        Assert.Contains(hits, h => h.Id == id && h.Dist < 1e-6);
        Assert.DoesNotContain(await repo.SearchAsync(Unit(1), "landscape", 3, default), h => h.Id == id);
    }

    [IntegrationFact]
    public async Task Audit_write_inserts_row()
    {
        var repo = new AuditRepository(_ds);
        await repo.WriteAsync(new AuditEntry(Ref, 0, "Turn_Completed", "abc", null, """{"k":1}""", 10, 20, 300), default);
        await using var cmd = _ds.CreateCommand("SELECT count(*) FROM audit_logs WHERE session_id = @s");
        cmd.Parameters.AddWithValue("s", Ref);
        Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        await using var del = _ds.CreateCommand("DELETE FROM audit_logs WHERE session_id = @s");
        del.Parameters.AddWithValue("s", Ref);
        await del.ExecuteNonQueryAsync();
    }
}
```

- [ ] **Step 3: 跑測試確認失敗**

```bash
docker compose up -d db
cd src && PC_INTEGRATION=1 dotnet test --filter RepositoryIntegrationTests 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 4: 實作三個 repository**

`src/PromptCopilot.Api/Data/PresetRepository.cs`：

```csharp
using Npgsql;
using Pgvector;

namespace PromptCopilot.Api.Data;

public sealed record PresetHit(long Id, string Title, string Category, IReadOnlyList<string> FacetIds,
    string PromptSnippet, string? NegativeSnippet, string? ImageUrl, double Dist);

public sealed record PresetDetail(long Id, string Title, string Category, string Description, IReadOnlyList<string> Tags,
    IReadOnlyList<string> FacetIds, string PromptSnippet, string? NegativeSnippet, string? ImageUrl);

public sealed class PresetRepository(NpgsqlDataSource ds)
{
    // 與 scripts/pipeline/retrieval.py 的 PRESETS_SQL 一致：GIN 過濾該維度 facet，HNSW 依「這一句自己的向量」排序，不設門檻
    private const string SearchSql = """
        SELECT id, title, category, facet_ids, prompt_snippet, negative_snippet, image_url,
               preset_embedding <=> @q AS dist
        FROM prompt_knowledge_presets
        WHERE facet_ids && @facets
        ORDER BY dist
        LIMIT @k
        """;
    private const string PoolSql = "SELECT count(*) FROM prompt_knowledge_presets WHERE facet_ids && @facets";
    private const string GetSql = """
        SELECT id, title, category, description, tags, facet_ids, prompt_snippet, negative_snippet, image_url
        FROM prompt_knowledge_presets WHERE id = @id
        """;

    public async Task<IReadOnlyList<PresetHit>> SearchAsync(float[] query, IReadOnlyList<string> facetIds, int k, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(SearchSql);
        cmd.Parameters.AddWithValue("q", new Vector(query));
        cmd.Parameters.AddWithValue("facets", facetIds.ToArray());
        cmd.Parameters.AddWithValue("k", k);
        var list = new List<PresetHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new PresetHit(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetFieldValue<string[]>(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetDouble(7)));
        return list;
    }

    public async Task<long> PoolSizeAsync(IReadOnlyList<string> facetIds, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(PoolSql);
        cmd.Parameters.AddWithValue("facets", facetIds.ToArray());
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<PresetDetail?> GetAsync(long id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(GetSql);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new PresetDetail(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<string[]>(4),
            r.GetFieldValue<string[]>(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8));
    }
}
```

`src/PromptCopilot.Api/Data/HistoryRepository.cs`：

```csharp
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace PromptCopilot.Api.Data;

public sealed record HistoryHit(Guid Id, string UserIntent, string PositivePrompt, string SubjectProfile, double Dist);
public sealed record HistoryInsert(string UserIntent, string Positive, string Negative, string Profile, string? CompletenessJson, float[] IntentEmbedding);

public sealed class HistoryRepository(NpgsqlDataSource ds)
{
    private const string SearchSql = """
        SELECT id, user_intent, positive_prompt, subject_profile, intent_embedding <=> @q AS dist
        FROM shared_prompt_histories
        WHERE subject_profile = @profile
        ORDER BY dist
        LIMIT @k
        """;
    private const string InsertSql = """
        INSERT INTO shared_prompt_histories
            (user_intent, positive_prompt, negative_prompt, subject_profile, source, completeness_scores, intent_embedding)
        VALUES (@intent, @pos, @neg, @profile, 'user', @scores, @emb)
        RETURNING id
        """;

    public async Task<IReadOnlyList<HistoryHit>> SearchAsync(float[] query, string profile, int k, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(SearchSql);
        cmd.Parameters.AddWithValue("q", new Vector(query));
        cmd.Parameters.AddWithValue("profile", profile);
        cmd.Parameters.AddWithValue("k", k);
        var list = new List<HistoryHit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new HistoryHit(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4)));
        return list;
    }

    public async Task<Guid> InsertAsync(HistoryInsert h, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(InsertSql);
        cmd.Parameters.AddWithValue("intent", h.UserIntent);
        cmd.Parameters.AddWithValue("pos", h.Positive);
        cmd.Parameters.AddWithValue("neg", h.Negative);
        cmd.Parameters.AddWithValue("profile", h.Profile);
        cmd.Parameters.Add(new NpgsqlParameter("scores", NpgsqlDbType.Jsonb) { Value = (object?)h.CompletenessJson ?? DBNull.Value });
        cmd.Parameters.AddWithValue("emb", new Vector(h.IntentEmbedding));
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
```

`src/PromptCopilot.Api/Data/AuditRepository.cs`：

```csharp
using Npgsql;
using NpgsqlTypes;

namespace PromptCopilot.Api.Data;

public sealed record AuditEntry(string? SessionId, int? TurnIndex, string EventType, string? PromptVersion = null,
    string? RawInput = null, string? PayloadJson = null, int? PromptTokens = null, int? CompletionTokens = null, int? LatencyMs = null);

public sealed class AuditRepository(NpgsqlDataSource ds)
{
    private const string Sql = """
        INSERT INTO audit_logs (session_id, turn_index, event_type, prompt_version, raw_input, payload, prompt_tokens, completion_tokens, latency_ms)
        VALUES (@s, @t, @e, @v, @raw, @p, @pt, @ct, @ms)
        """;

    public async Task WriteAsync(AuditEntry a, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(Sql);
        cmd.Parameters.AddWithValue("s", (object?)a.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("t", (object?)a.TurnIndex ?? DBNull.Value);
        cmd.Parameters.AddWithValue("e", a.EventType);
        cmd.Parameters.AddWithValue("v", (object?)a.PromptVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("raw", (object?)a.RawInput ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = (object?)a.PayloadJson ?? DBNull.Value });
        cmd.Parameters.AddWithValue("pt", (object?)a.PromptTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ct", (object?)a.CompletionTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ms", (object?)a.LatencyMs ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 5: 註冊 DI**

`Program.cs` 在 `AddSwaggerGen()` 之後加：

```csharp
builder.Services.AddSingleton(sp =>
{
    var cs = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;
    var b = new NpgsqlDataSourceBuilder(cs);
    b.UseVector();
    return b.Build();
});
builder.Services.AddSingleton<PresetRepository>();
builder.Services.AddSingleton<HistoryRepository>();
builder.Services.AddSingleton<AuditRepository>();
```

加 `using Microsoft.Extensions.Options; using Npgsql; using Pgvector.Npgsql; using PromptCopilot.Api.Data;`。

- [ ] **Step 6: 跑測試確認通過**

```bash
cd src && PC_INTEGRATION=1 dotnet test --filter RepositoryIntegrationTests 2>&1 | tail -3
```

Expected: `Passed: 4`。再跑一次不帶環境變數，確認 4 個是 `Skipped`。

- [ ] **Step 7: Commit**

```bash
cd src && git add . && git commit -m "feat(api): Npgsql repositories for presets, histories, audit

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 6: Gemini embedding client

**Files:**
- Create: `src/PromptCopilot.Api/Llm/GeminiEmbeddingClient.cs`
- Test: `src/PromptCopilot.Api.Tests/Llm/GeminiEmbeddingClientTests.cs`
- Modify: `src/PromptCopilot.Api/Program.cs`

**Interfaces:**
- Produces: `interface IEmbeddingClient { Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, string taskType, CancellationToken ct); }`；`class GeminiEmbeddingClient(HttpClient, IOptions<EmbeddingOptions>, IOptions<LlmOptions>) : IEmbeddingClient`；常數 `GeminiEmbeddingClient.RetrievalQuery = "RETRIEVAL_QUERY"`、`RetrievalDocument = "RETRIEVAL_DOCUMENT"`、`BatchSize = 32`

三件事必須跟 Python `gemini_client.py` 一樣，否則查詢向量跟庫裡的向量不在同一個空間：`taskType`、`outputDimensionality = 768`、L2 正規化。

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Llm/GeminiEmbeddingClientTests.cs`：

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Llm;

namespace PromptCopilot.Api.Tests.Llm;

public class GeminiEmbeddingClientTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpRequestMessage req, string body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            var n = JsonDocument.Parse(body).RootElement.GetProperty("requests").GetArrayLength();
            var embeddings = string.Join(",", Enumerable.Repeat("""{"values":[3,4]}""", n));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"embeddings":[{{embeddings}}]}""") };
        }
    }

    private static (GeminiEmbeddingClient client, Handler h) Make()
    {
        var h = new Handler();
        var c = new GeminiEmbeddingClient(new HttpClient(h),
            Options.Create(new EmbeddingOptions { Model = "gemini-embedding-001", Dimensions = 768, Endpoint = "https://x/v1beta" }),
            Options.Create(new LlmOptions { ApiKey = "KEY" }));
        return (c, h);
    }

    [Fact]
    public async Task Sends_task_type_dimensions_and_key_header_then_normalizes()
    {
        var (c, h) = Make();
        var v = await c.EmbedAsync(new[] { "雨夜" }, GeminiEmbeddingClient.RetrievalQuery, default);
        var (req, body) = h.Calls.Single();
        Assert.Equal("https://x/v1beta/models/gemini-embedding-001:batchEmbedContents", req.RequestUri!.ToString());
        Assert.Equal("KEY", req.Headers.GetValues("x-goog-api-key").Single());
        var r = JsonDocument.Parse(body).RootElement.GetProperty("requests")[0];
        Assert.Equal("RETRIEVAL_QUERY", r.GetProperty("taskType").GetString());
        Assert.Equal(768, r.GetProperty("outputDimensionality").GetInt32());
        Assert.Equal("models/gemini-embedding-001", r.GetProperty("model").GetString());
        Assert.Equal(0.6f, v[0][0], 5); Assert.Equal(0.8f, v[0][1], 5);   // [3,4] → [0.6,0.8]
    }

    [Fact]
    public async Task Chunks_at_batch_size()
    {
        var (c, h) = Make();
        var v = await c.EmbedAsync(Enumerable.Range(0, GeminiEmbeddingClient.BatchSize + 5).Select(i => $"t{i}").ToList(), GeminiEmbeddingClient.RetrievalQuery, default);
        Assert.Equal(GeminiEmbeddingClient.BatchSize + 5, v.Count);
        Assert.Equal(2, h.Calls.Count);
    }

    [Fact]
    public async Task Empty_input_makes_no_call()
    {
        var (c, h) = Make();
        Assert.Empty(await c.EmbedAsync(Array.Empty<string>(), GeminiEmbeddingClient.RetrievalQuery, default));
        Assert.Empty(h.Calls);
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

```bash
cd src && dotnet test --filter GeminiEmbeddingClientTests 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 3: 實作**

`src/PromptCopilot.Api/Llm/GeminiEmbeddingClient.cs`：

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Llm;

public interface IEmbeddingClient
{
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, string taskType, CancellationToken ct);
}

/// <summary>直接打 REST 而不用 SK 抽象：要保證 taskType、outputDimensionality、L2 正規化三件事與 Python 管線一致。</summary>
public sealed class GeminiEmbeddingClient(HttpClient http, IOptions<EmbeddingOptions> emb, IOptions<LlmOptions> llm) : IEmbeddingClient
{
    public const string RetrievalQuery = "RETRIEVAL_QUERY";
    public const string RetrievalDocument = "RETRIEVAL_DOCUMENT";
    public const int BatchSize = 32;

    private sealed record BatchResponse(List<EmbeddingValues> Embeddings);
    private sealed record EmbeddingValues(float[] Values);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, string taskType, CancellationToken ct)
    {
        var o = emb.Value;
        var all = new List<float[]>(texts.Count);
        foreach (var chunk in texts.Chunk(BatchSize))
        {
            var payload = new
            {
                requests = chunk.Select(t => new
                {
                    model = $"models/{o.Model}",
                    content = new { parts = new[] { new { text = t } } },
                    taskType,
                    outputDimensionality = o.Dimensions,
                }),
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.Endpoint}/models/{o.Model}:batchEmbedContents")
            {
                Content = JsonContent.Create(payload),
            };
            req.Headers.Add("x-goog-api-key", llm.Value.ApiKey);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var parsed = await resp.Content.ReadFromJsonAsync<BatchResponse>(new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
                         ?? throw new InvalidOperationException("embedding 回應為空");
            all.AddRange(parsed.Embeddings.Select(e => Normalize(e.Values)));
        }
        return all;
    }

    private static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return norm == 0 ? v : v.Select(x => x / norm).ToArray();
    }
}
```

- [ ] **Step 4: 註冊 DI**

`Program.cs`：

```csharp
builder.Services.AddHttpClient<IEmbeddingClient, GeminiEmbeddingClient>();
```

- [ ] **Step 5: 跑測試確認通過**

```bash
cd src && dotnet test --filter GeminiEmbeddingClientTests 2>&1 | tail -3
```

Expected: `Passed: 3`。

- [ ] **Step 6: Commit**

```bash
cd src && git add . && git commit -m "feat(api): Gemini embedding client matching pipeline vector space

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 7: LLM 失敗分類與三層重試（`ResilientChatCompletion`）

**Files:**
- Create: `src/PromptCopilot.Api/Llm/LlmFailures.cs`
- Create: `src/PromptCopilot.Api/Llm/ResilientChatCompletion.cs`
- Create: `src/PromptCopilot.Api.Tests/Fakes/FakeChatCompletion.cs`
- Test: `src/PromptCopilot.Api.Tests/Llm/ResilientChatCompletionTests.cs`
- Modify: `src/PromptCopilot.Api/Program.cs`

**Interfaces:**
- Produces:
  - `class UpstreamBlockedException(string reason, Exception? inner) : Exception { string Reason }`
  - `class UnusableResponseException(string? finishReason) : Exception { string? FinishReason }`
  - `enum LlmFailureKind { Transport, ContentBlock, Unusable, Fatal }`
  - `static class LlmFailureClassifier { IReadOnlySet<string> ContentBlockReasons; LlmFailureKind Classify(Exception e); string? BlockReasonOf(Exception e); (LlmFailureKind Kind, string? Reason)? ProblemOf(IReadOnlyList<ChatMessageContent> result) }`
  - `class ResilientChatCompletion(IChatCompletionService inner, IOptions<LlmOptions>, Func<TimeSpan, CancellationToken, Task>? delay = null) : IChatCompletionService`
  - 測試用 `class FakeChatCompletion : IChatCompletionService { Queue<Func<ChatHistory, IReadOnlyList<ChatMessageContent>>> Script; List<ChatHistory> Calls; static ChatMessageContent Text(string); static ChatMessageContent WithCalls(params FunctionCallContent[]); static ChatMessageContent WithMeta(string? content, string key, string value) }`

`XRetries` 是**重試次數**，總嘗試 = 重試 + 1：`ContentBlockRetries = 1` → 最多打 2 次，跟 Python `CONTENT_BLOCK_ATTEMPTS = 1` 的實際行為一致。

**分類依據（executor 要對照實際 connector 版本確認）**：Google connector 在 prompt 被擋時丟 `KernelException`，訊息含 `blocked`；回應正常但 `finishReason` 屬 `SAFETY` 等值時，`ChatMessageContent.Metadata["FinishReason"]` 會帶那個值、`Metadata["PromptFeedbackBlockReason"]` 帶 `promptFeedback.blockReason`。分類器兩邊都看。若你用的版本訊息文字不同，改 `ContentBlockMarkers`，測試不用動。

- [ ] **Step 1: 寫 fake**

`src/PromptCopilot.Api.Tests/Fakes/FakeChatCompletion.cs`：

```csharp
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace PromptCopilot.Api.Tests.Fakes;

/// <summary>腳本化的 chat completion：每次呼叫吐 Script 的下一項。項目可以丟例外。</summary>
public sealed class FakeChatCompletion : IChatCompletionService
{
    public Queue<Func<ChatHistory, IReadOnlyList<ChatMessageContent>>> Script { get; } = new();
    public List<ChatHistory> Calls { get; } = new();
    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public FakeChatCompletion Then(Func<ChatHistory, IReadOnlyList<ChatMessageContent>> step) { Script.Enqueue(step); return this; }
    public FakeChatCompletion Then(ChatMessageContent msg) => Then(_ => new[] { msg });
    public FakeChatCompletion Throw(Exception e) => Then(_ => throw e);

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(new ChatHistory(chatHistory));
        if (Script.Count == 0) throw new InvalidOperationException("FakeChatCompletion script exhausted");
        return Task.FromResult(Script.Dequeue()(chatHistory));
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public static ChatMessageContent Text(string content) => new(AuthorRole.Assistant, content);

    public static ChatMessageContent WithCalls(params FunctionCallContent[] calls)
    {
        var m = new ChatMessageContent(AuthorRole.Assistant, content: null);
        foreach (var c in calls) m.Items.Add(c);
        return m;
    }

    public static ChatMessageContent WithMeta(string? content, string key, string value) =>
        new(AuthorRole.Assistant, content, metadata: new Dictionary<string, object?> { [key] = value });
}
```

- [ ] **Step 2: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Llm/ResilientChatCompletionTests.cs`：

```csharp
using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Llm;

public class ResilientChatCompletionTests
{
    private static readonly ChatMessageContent Ok = FakeChatCompletion.Text("ok");
    private static HttpOperationException Http(HttpStatusCode c) => new(c, null, c.ToString(), null);
    private static KernelException Blocked(string reason) => new($"Prompt was blocked due to Gemini API safety reasons: {reason}");

    private static (ResilientChatCompletion sut, FakeChatCompletion inner, List<TimeSpan> delays) Make(int transport = 3, int unusable = 3, int block = 1)
    {
        var inner = new FakeChatCompletion();
        var delays = new List<TimeSpan>();
        var sut = new ResilientChatCompletion(inner,
            Options.Create(new LlmOptions { TransportRetries = transport, UnusableRetries = unusable, ContentBlockRetries = block, TransportBackoffMs = 1000 }),
            (d, _) => { delays.Add(d); return Task.CompletedTask; });
        return (sut, inner, delays);
    }

    [Fact]
    public async Task Transport_errors_retry_with_doubling_backoff()
    {
        var (sut, inner, delays) = Make();
        inner.Throw(Http(HttpStatusCode.TooManyRequests)).Throw(Http(HttpStatusCode.ServiceUnavailable)).Then(Ok);
        var r = await sut.GetChatMessageContentsAsync(new ChatHistory());
        Assert.Equal("ok", r[0].Content);
        Assert.Equal(3, inner.Calls.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays);
    }

    [Fact]
    public async Task Transport_gives_up_after_retries()
    {
        var (sut, inner, _) = Make(transport: 3);
        for (var i = 0; i < 4; i++) inner.Throw(Http(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpOperationException>(() => sut.GetChatMessageContentsAsync(new ChatHistory()));
        Assert.Equal(4, inner.Calls.Count);
    }

    [Fact]
    public async Task Non_transient_http_error_is_not_retried()
    {
        var (sut, inner, _) = Make();
        inner.Throw(Http(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<HttpOperationException>(() => sut.GetChatMessageContentsAsync(new ChatHistory()));
        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task Content_block_is_retried_exactly_once_then_surfaces_reason()
    {
        var (sut, inner, delays) = Make();
        inner.Throw(Blocked("PROHIBITED_CONTENT")).Throw(Blocked("PROHIBITED_CONTENT"));
        var ex = await Assert.ThrowsAsync<UpstreamBlockedException>(() => sut.GetChatMessageContentsAsync(new ChatHistory()));
        Assert.Equal("PROHIBITED_CONTENT", ex.Reason);
        Assert.Equal(2, inner.Calls.Count);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Content_block_that_clears_on_retry_succeeds()
    {
        var (sut, inner, _) = Make();
        inner.Throw(Blocked("SAFETY")).Then(Ok);
        Assert.Equal("ok", (await sut.GetChatMessageContentsAsync(new ChatHistory()))[0].Content);
        Assert.Equal(2, inner.Calls.Count);
    }

    [Fact]
    public async Task Block_reported_in_metadata_is_treated_as_content_block()
    {
        var (sut, inner, _) = Make(block: 0);
        inner.Then(FakeChatCompletion.WithMeta(null, "FinishReason", "SAFETY"));
        var ex = await Assert.ThrowsAsync<UpstreamBlockedException>(() => sut.GetChatMessageContentsAsync(new ChatHistory()));
        Assert.Equal("SAFETY", ex.Reason);
    }

    [Fact]
    public async Task Empty_response_retries_then_gives_up()
    {
        var (sut, inner, _) = Make(unusable: 3);
        for (var i = 0; i < 4; i++) inner.Then(FakeChatCompletion.WithMeta(null, "FinishReason", "MAX_TOKENS"));
        var ex = await Assert.ThrowsAsync<UnusableResponseException>(() => sut.GetChatMessageContentsAsync(new ChatHistory()));
        Assert.Equal("MAX_TOKENS", ex.FinishReason);
        Assert.Equal(4, inner.Calls.Count);
    }

    [Fact]
    public async Task Empty_response_then_success()
    {
        var (sut, inner, _) = Make();
        inner.Then(FakeChatCompletion.Text("")).Then(Ok);
        Assert.Equal("ok", (await sut.GetChatMessageContentsAsync(new ChatHistory()))[0].Content);
        Assert.Equal(2, inner.Calls.Count);
    }

    [Fact]
    public async Task Function_call_with_no_text_is_a_usable_response()
    {
        var (sut, inner, _) = Make();
        inner.Then(FakeChatCompletion.WithCalls(new FunctionCallContent("SetProfile", "Session", "1")));
        var r = await sut.GetChatMessageContentsAsync(new ChatHistory());
        Assert.Single(r[0].Items.OfType<FunctionCallContent>());
        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task Cancellation_during_backoff_stops_retrying()
    {
        var inner = new FakeChatCompletion().Throw(Http(HttpStatusCode.TooManyRequests)).Then(Ok);
        var cts = new CancellationTokenSource();
        var sut = new ResilientChatCompletion(inner, Options.Create(new LlmOptions()), (_, ct) => { cts.Cancel(); return Task.FromCanceled(ct); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetChatMessageContentsAsync(new ChatHistory(), cancellationToken: cts.Token));
        Assert.Single(inner.Calls);
    }
}
```

- [ ] **Step 3: 跑測試確認失敗**

```bash
cd src && dotnet test --filter ResilientChatCompletionTests 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 4: 實作分類器與例外**

`src/PromptCopilot.Api/Llm/LlmFailures.cs`：

```csharp
using System.Net;
using Microsoft.SemanticKernel;

namespace PromptCopilot.Api.Llm;

/// <summary>Gemini 判定內容不該生成，且內部重試（多輪 §5.5：1 次）已用盡。</summary>
public sealed class UpstreamBlockedException(string reason, Exception? inner = null)
    : Exception($"Gemini 攔截了這次請求的內容（{reason}）", inner)
{
    public string Reason { get; } = reason;
}

/// <summary>200 但沒有可用內容（MAX_TOKENS、空 candidates），且重試已用盡。與內容無關。</summary>
public sealed class UnusableResponseException(string? finishReason)
    : Exception($"Gemini 連續回了無法使用的回應（finish_reason={finishReason ?? "unknown"}）")
{
    public string? FinishReason { get; } = finishReason;
}

public enum LlmFailureKind { Transport, ContentBlock, Unusable, Fatal }

public static class LlmFailureClassifier
{
    // 與 scripts/pipeline/gemini_client.py 的 CONTENT_BLOCK_REASONS 一致
    public static readonly IReadOnlySet<string> ContentBlockReasons = new HashSet<string>
        { "PROHIBITED_CONTENT", "SAFETY", "IMAGE_SAFETY", "BLOCKLIST", "JAILBREAK", "MODEL_ARMOR" };

    /// <summary>connector 例外訊息裡代表「被擋」的字樣；若換 connector 版本文字不同，改這裡。</summary>
    public static readonly IReadOnlyList<string> ContentBlockMarkers = new[] { "blocked", "safety" };

    public static LlmFailureKind Classify(Exception e) => e switch
    {
        HttpOperationException h when h.StatusCode is HttpStatusCode.TooManyRequests || (int?)h.StatusCode >= 500 => LlmFailureKind.Transport,
        HttpRequestException or TaskCanceledException or IOException => LlmFailureKind.Transport,
        KernelException when BlockReasonOf(e) is not null => LlmFailureKind.ContentBlock,
        KernelException => LlmFailureKind.Unusable,
        _ => LlmFailureKind.Fatal,
    };

    public static string? BlockReasonOf(Exception e)
    {
        var msg = e.Message;
        var reason = ContentBlockReasons.FirstOrDefault(r => msg.Contains(r, StringComparison.OrdinalIgnoreCase));
        if (reason is not null) return reason;
        return ContentBlockMarkers.Any(m => msg.Contains(m, StringComparison.OrdinalIgnoreCase)) ? "SAFETY" : null;
    }

    /// <summary>回應層的問題：metadata 帶攔截理由 → ContentBlock；沒文字也沒 function call → Unusable；否則 null。</summary>
    public static (LlmFailureKind Kind, string? Reason)? ProblemOf(IReadOnlyList<ChatMessageContent> result)
    {
        var m = result.Count > 0 ? result[0] : null;
        if (m is null) return (LlmFailureKind.Unusable, null);
        var block = Meta(m, "PromptFeedbackBlockReason");
        if (block is not null && ContentBlockReasons.Contains(block)) return (LlmFailureKind.ContentBlock, block);
        var finish = Meta(m, "FinishReason");
        if (finish is not null && ContentBlockReasons.Contains(finish)) return (LlmFailureKind.ContentBlock, finish);
        var hasCall = m.Items.OfType<FunctionCallContent>().Any();
        if (string.IsNullOrWhiteSpace(m.Content) && !hasCall) return (LlmFailureKind.Unusable, finish);
        return null;
    }

    private static string? Meta(ChatMessageContent m, string key) =>
        m.Metadata is not null && m.Metadata.TryGetValue(key, out var v) && v is not null ? v.ToString()!.ToUpperInvariant() : null;
}
```

- [ ] **Step 5: 實作 decorator**

`src/PromptCopilot.Api/Llm/ResilientChatCompletion.cs`：

```csharp
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Llm;

/// <summary>包在單次 LLM 呼叫外面的三層重試（多輪 §5.6）。auto-invoke 關掉，所以這裡的一次呼叫就是一個 HTTP 往返。</summary>
public sealed class ResilientChatCompletion : IChatCompletionService
{
    private readonly IChatCompletionService _inner;
    private readonly LlmOptions _o;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ResilientChatCompletion(IChatCompletionService inner, IOptions<LlmOptions> options, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _inner = inner; _o = options.Value; _delay = delay ?? Task.Delay;
    }

    public IReadOnlyDictionary<string, object?> Attributes => _inner.Attributes;

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        int transport = 0, unusable = 0, blocked = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ChatMessageContent> result;
            try
            {
                result = await _inner.GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                switch (LlmFailureClassifier.Classify(e))
                {
                    case LlmFailureKind.Transport when transport < _o.TransportRetries:
                        await _delay(TimeSpan.FromMilliseconds(_o.TransportBackoffMs * Math.Pow(2, transport)), cancellationToken);
                        transport++; continue;
                    case LlmFailureKind.ContentBlock when blocked < _o.ContentBlockRetries:
                        blocked++; continue;
                    case LlmFailureKind.ContentBlock:
                        throw new UpstreamBlockedException(LlmFailureClassifier.BlockReasonOf(e) ?? "SAFETY", e);
                    case LlmFailureKind.Unusable when unusable < _o.UnusableRetries:
                        unusable++; continue;
                    default:
                        throw;
                }
            }

            var problem = LlmFailureClassifier.ProblemOf(result);
            if (problem is null) return result;
            var (kind, reason) = problem.Value;
            if (kind == LlmFailureKind.ContentBlock)
            {
                if (blocked < _o.ContentBlockRetries) { blocked++; continue; }
                throw new UpstreamBlockedException(reason ?? "SAFETY");
            }
            if (unusable < _o.UnusableRetries) { unusable++; continue; }
            throw new UnusableResponseException(reason);
        }
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("本專案不串流 token；終止型 tool 的參數一次到位（多輪 §5.1）。");
}
```

- [ ] **Step 6: 註冊 DI——真的 Gemini 包在 decorator 裡**

`Program.cs`：

```csharp
builder.Services.AddSingleton<IChatCompletionService>(sp =>
{
    var llm = sp.GetRequiredService<IOptions<LlmOptions>>();
    var raw = new GoogleAIGeminiChatCompletionService(llm.Value.Model, llm.Value.ApiKey);
    return new ResilientChatCompletion(raw, llm);
});
```

加 `using Microsoft.SemanticKernel.ChatCompletion; using Microsoft.SemanticKernel.Connectors.Google; using PromptCopilot.Api.Llm;`。

- [ ] **Step 7: 跑測試確認通過**

```bash
cd src && dotnet test --filter ResilientChatCompletionTests 2>&1 | tail -3
```

Expected: `Passed: 10`。

- [ ] **Step 8: Commit**

```bash
cd src && git add . && git commit -m "feat(api): classify Gemini failures, three-tier retry decorator

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 8: 輸入側 SafetyGuard 與分類器

**Files:**
- Create: `src/PromptCopilot.Api/Safety/Denylist.cs`
- Create: `src/PromptCopilot.Api/Safety/SafetyClassifier.cs`
- Create: `src/PromptCopilot.Api/Safety/SafetyGuard.cs`
- Test: `src/PromptCopilot.Api.Tests/Safety/SafetyGuardTests.cs`
- Modify: `src/PromptCopilot.Api/Program.cs`

**Interfaces:**
- Produces:
  - `class Denylist(IEnumerable<string> terms) { bool Hits(string text, out string term) }`
  - `record SafetyVerdict(bool Nsfw, bool RealPerson, string? PersonName, bool WantsAutoComplete, string Reason)`
  - `class SafetyClassifier(IChatCompletionService, IOptions<LlmOptions>) { Task<SafetyVerdict> ClassifyInputAsync(string text, CancellationToken); Task<SafetyVerdict> ClassifyOutputAsync(string text, CancellationToken) }`
  - `record GuardResult(bool Blocked, string? BlockCode, string? Message, bool WantsAutoComplete)`；`BlockCode ∈ { "Blocked_NSFW", "Blocked_Celebrity" }`
  - `class SafetyGuard(Denylist, SafetyClassifier) { Task<GuardResult> CheckAsync(string text, CancellationToken) }`

分類器同時給輸入側（§6.1）與輸出側（多輪 §5.2）用；輸出側只看 `Nsfw || RealPerson`。分類是一次 Gemini 結構化呼叫，走 Task 7 的 decorator，所以它自己也有重試。

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Safety/SafetyGuardTests.cs`：

```csharp
using Microsoft.Extensions.Options;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Safety;

public class SafetyGuardTests
{
    private static (SafetyGuard guard, FakeChatCompletion chat) Make(params string[] deny)
    {
        var chat = new FakeChatCompletion();
        var guard = new SafetyGuard(new Denylist(deny), new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        return (guard, chat);
    }

    private static string Verdict(bool nsfw = false, bool real = false, string? name = null, bool auto = false) =>
        $$"""{"nsfw":{{nsfw.ToString().ToLower()}},"realPerson":{{real.ToString().ToLower()}},"personName":{{(name is null ? "null" : $"\"{name}\"")}},"wantsAutoComplete":{{auto.ToString().ToLower()}},"reason":"r"}""";

    [Fact]
    public void Denylist_normalizes_punctuation_and_matches_whole_english_tokens()
    {
        var d = new Denylist(new[] { "nude", "裸" });
        Assert.True(d.Hits("a half-NUDE figure", out var t)); Assert.Equal("nude", t);
        Assert.True(d.Hits("underwear_nude", out _));
        Assert.False(d.Hits("nudeness", out _));          // 不是整個 token
        Assert.True(d.Hits("全裸的人", out _));            // CJK 用子字串
    }

    [Fact]
    public async Task Denylist_hit_blocks_without_calling_classifier()
    {
        var (guard, chat) = Make("nude");
        var r = await guard.CheckAsync("a nude girl", default);
        Assert.True(r.Blocked); Assert.Equal("Blocked_NSFW", r.BlockCode);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task Classifier_nsfw_blocks()
    {
        var (guard, chat) = Make();
        chat.Then(FakeChatCompletion.Text(Verdict(nsfw: true)));
        var r = await guard.CheckAsync("x", default);
        Assert.True(r.Blocked); Assert.Equal("Blocked_NSFW", r.BlockCode);
    }

    [Fact]
    public async Task Classifier_real_person_blocks_with_celebrity_code()
    {
        var (guard, chat) = Make();
        chat.Then(FakeChatCompletion.Text(Verdict(real: true, name: "某某")));
        var r = await guard.CheckAsync("x", default);
        Assert.True(r.Blocked); Assert.Equal("Blocked_Celebrity", r.BlockCode);
        Assert.Contains("某某", r.Message);
    }

    [Fact]
    public async Task Clean_input_passes_through_wantsAutoComplete()
    {
        var (guard, chat) = Make();
        chat.Then(FakeChatCompletion.Text(Verdict(auto: true)));
        var r = await guard.CheckAsync("隨便你決定", default);
        Assert.False(r.Blocked); Assert.True(r.WantsAutoComplete);
    }

    [Fact]
    public async Task Unparseable_verdict_throws()
    {
        var (guard, chat) = Make();
        chat.Then(FakeChatCompletion.Text("not json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.CheckAsync("x", default));
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

```bash
cd src && dotnet test --filter SafetyGuardTests 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 3: 實作**

`src/PromptCopilot.Api/Safety/Denylist.cs`：

```csharp
using System.Text.RegularExpressions;

namespace PromptCopilot.Api.Safety;

/// <summary>快速路徑。比對前把非字母數字正規化成空白（同 Python 管線 R14/R15），英文詞整 token 比對，CJK 子字串。</summary>
public sealed class Denylist
{
    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private readonly List<(string term, bool ascii)> _terms;

    public Denylist(IEnumerable<string> terms) =>
        _terms = terms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => (t.Trim().ToLowerInvariant(), t.All(char.IsAscii))).ToList();

    public bool Hits(string text, out string term)
    {
        var norm = NonWord.Replace(text.ToLowerInvariant(), " ");
        var tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (var (t, ascii) in _terms)
        {
            if (ascii ? tokens.Contains(t) : norm.Contains(t)) { term = t; return true; }
        }
        term = ""; return false;
    }
}
```

`src/PromptCopilot.Api/Safety/SafetyClassifier.cs`：

```csharp
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Configuration;

namespace PromptCopilot.Api.Safety;

public sealed record SafetyVerdict(bool Nsfw, bool RealPerson, string? PersonName, bool WantsAutoComplete, string Reason);

public sealed class SafetyClassifier(IChatCompletionService chat, IOptions<LlmOptions> llm)
{
    private const string InputPrompt = """
        你是 AI 生圖提示詞助理的輸入審核。判斷下面這段使用者輸入，回 JSON：
        - nsfw：是否要求色情、裸露、性暗示、未成年性化內容
        - realPerson：是否指名真實存在的公眾人物或私人（虛構角色不算）；是的話 personName 填名字
        - wantsAutoComplete：整句是否在要求系統「直接幫我決定全部／隨便／你看著辦／直接給我」；只針對單一項目說隨便（「鞋子隨便，背景我想一下」）不算
        - reason：一句話
        使用者輸入：
        """;

    private const string OutputPrompt = """
        你是 AI 生圖提示詞助理的輸出審核。下面是要送給使用者的英文 prompt 片段或繁中說明，判斷回 JSON：
        - nsfw：是否含色情、裸露、性暗示、未成年性化內容
        - realPerson：是否描述真實存在的人；是的話 personName 填名字
        - wantsAutoComplete：固定 false
        - reason：一句話
        內容：
        """;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public Task<SafetyVerdict> ClassifyInputAsync(string text, CancellationToken ct) => RunAsync(InputPrompt + text, ct);
    public Task<SafetyVerdict> ClassifyOutputAsync(string text, CancellationToken ct) => RunAsync(OutputPrompt + text, ct);

    private async Task<SafetyVerdict> RunAsync(string prompt, CancellationToken ct)
    {
        var history = new ChatHistory();
        history.AddUserMessage(prompt);
        var settings = new GeminiPromptExecutionSettings
        {
            ModelId = llm.Value.Model,
            ResponseMimeType = "application/json",
            ResponseSchema = typeof(SafetyVerdict),
            Temperature = 0,
        };
        var result = await chat.GetChatMessageContentsAsync(history, settings, kernel: null, ct);
        var content = result[0].Content ?? "";
        try
        {
            return JsonSerializer.Deserialize<SafetyVerdict>(content, Json) ?? throw new InvalidOperationException("分類器回 null");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"分類器回了非 JSON：{content[..Math.Min(80, content.Length)]}", e);
        }
    }
}
```

`src/PromptCopilot.Api/Safety/SafetyGuard.cs`：

```csharp
namespace PromptCopilot.Api.Safety;

public sealed record GuardResult(bool Blocked, string? BlockCode, string? Message, bool WantsAutoComplete)
{
    public static GuardResult Ok(bool wantsAutoComplete) => new(false, null, null, wantsAutoComplete);
}

/// <summary>輸入側（主規格 §6.1）。在 service 層、進 kernel 之前執行。</summary>
public sealed class SafetyGuard(Denylist denylist, SafetyClassifier classifier)
{
    public async Task<GuardResult> CheckAsync(string text, CancellationToken ct)
    {
        if (denylist.Hits(text, out var term))
            return new GuardResult(true, "Blocked_NSFW", $"輸入含不允許的內容（{term}），這一輪不處理。", false);
        var v = await classifier.ClassifyInputAsync(text, ct);
        if (v.Nsfw)
            return new GuardResult(true, "Blocked_NSFW", $"輸入被判定為不當內容：{v.Reason}", false);
        if (v.RealPerson)
            return new GuardResult(true, "Blocked_Celebrity", $"不生成真實人物（{v.PersonName ?? "未具名"}）：{v.Reason}", false);
        return GuardResult.Ok(v.WantsAutoComplete);
    }
}
```

- [ ] **Step 4: 註冊 DI**

`Program.cs`：

```csharp
builder.Services.AddSingleton(sp => new Denylist(builder.Configuration.GetSection("Safety:Denylist").Get<string[]>() ?? Array.Empty<string>()));
builder.Services.AddSingleton<SafetyClassifier>();
builder.Services.AddSingleton<SafetyGuard>();
```

- [ ] **Step 5: 跑測試確認通過**

```bash
cd src && dotnet test --filter SafetyGuardTests 2>&1 | tail -3
```

Expected: `Passed: 6`。

- [ ] **Step 6: Commit**

```bash
cd src && git add . && git commit -m "feat(api): input-side SafetyGuard with denylist fast path and Gemini classifier

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 9: Tool 契約與 AskCleaner

**Files:**
- Create: `src/PromptCopilot.Api/Plugins/Contracts.cs`
- Create: `src/PromptCopilot.Api/Plugins/AskCleaner.cs`
- Test: `src/PromptCopilot.Api.Tests/Plugins/AskCleanerTests.cs`

**Interfaces:**
- Produces:
  - `record OptionItem(string Label, string Tags, long? PresetId)`
  - `record AskItem(string Dimension, string Question, IReadOnlyList<string> MissingFacetIds, IReadOnlyList<OptionItem> Options)`
  - `abstract record TurnOutcome`；`record AskOutcome(string Preamble, IReadOnlyList<AskItem> Asks)`；`record MessageOutcome(string Message, IReadOnlyList<OptionItem> Options)`；`record FinalizedOutcome(FinalPrompt Final)`；`record SaveConsentOutcome`；`record BudgetExhaustedOutcome`
  - `record CleanResult<T>(IReadOnlyList<T> Kept, IReadOnlyList<string> Rejected)`
  - `static class AskCleaner { CleanResult<AskItem> CleanAsks(IReadOnlyList<AskItem>, Session, FacetCatalog, int maxAsks); CleanResult<OptionItem> CleanOptions(IReadOnlyList<OptionItem>, PresetLedger, int maxOptions) }`

多輪 §4.3 的兩張表，一列一個測試。清洗只修剪與過濾，不改語意；被拒理由回傳給呼叫端寫 audit。

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Plugins/AskCleanerTests.cs`：

```csharp
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Plugins;

public class AskCleanerTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();

    private static Session Portrait(params (string id, FacetState s)[] states)
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        s.ApplyFacetStates(states.ToDictionary(x => x.id, x => x.s), Catalog);
        return s;
    }
    private static OptionItem Opt(string l, long? id = null) => new(l, "tag", id);
    private static AskItem Ask(string dim, IEnumerable<string> missing, int nOptions = 2) =>
        new(dim, "q?", missing.ToList(), Enumerable.Range(0, nOptions).Select(i => Opt($"o{i}")).ToList());

    [Fact]
    public void Truncates_asks_to_max()
    {
        var r = AskCleaner.CleanAsks(new[] { Ask("style", new[] { "style.genre" }), Ask("camera", new[] { "camera.shot" }),
            Ask("clothing", new[] { "clothing.upper" }), Ask("scene", new[] { "scene.location" }) }, Portrait(), Catalog, maxAsks: 3);
        Assert.Equal(new[] { "style", "camera", "clothing" }, r.Kept.Select(a => a.Dimension));
        Assert.Single(r.Rejected);
    }

    [Fact]
    public void Truncates_options_to_four_and_drops_ask_with_fewer_than_two()
    {
        var r = AskCleaner.CleanAsks(new[] { Ask("style", new[] { "style.genre" }, nOptions: 6), Ask("camera", new[] { "camera.shot" }, nOptions: 1) }, Portrait(), Catalog, 3);
        Assert.Single(r.Kept);
        Assert.Equal(4, r.Kept[0].Options.Count);
    }

    [Fact]
    public void Filters_facets_that_are_not_missing_or_not_in_dimension()
    {
        var s = Portrait(("style.genre", FacetState.Covered));
        var r = AskCleaner.CleanAsks(new[] { Ask("style", new[] { "style.genre", "style.palette", "camera.shot", "style.nope" }) }, s, Catalog, 3);
        Assert.Equal(new[] { "style.palette" }, r.Kept[0].MissingFacetIds);
        Assert.Equal(3, r.Rejected.Count);
    }

    [Fact]
    public void Drops_ask_whose_facets_are_all_filtered()
    {
        var s = Portrait(("style.genre", FacetState.Waived));
        var r = AskCleaner.CleanAsks(new[] { Ask("style", new[] { "style.genre" }) }, s, Catalog, 3);
        Assert.Empty(r.Kept);
    }

    [Fact]
    public void Options_unknown_presetId_downgrades_to_null_and_is_capped()
    {
        var ledger = new PresetLedger();
        ledger.Record(new LedgerEntry { Id = 5, Title = "t", PromptSnippet = "p", FacetIds = Array.Empty<string>() }, new LedgerHit("style", 0.2, true));
        var r = AskCleaner.CleanOptions(new[] { Opt("a", 5), Opt("b", 99), Opt("c"), Opt("d"), Opt("e") }, ledger, maxOptions: 4);
        Assert.Equal(4, r.Kept.Count);
        Assert.Equal(5, r.Kept[0].PresetId);
        Assert.Null(r.Kept[1].PresetId);
        Assert.Contains(r.Rejected, m => m.Contains("99"));
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

```bash
cd src && dotnet test --filter AskCleanerTests 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 3: 實作契約**

`src/PromptCopilot.Api/Plugins/Contracts.cs`：

```csharp
using System.Text.Json.Serialization;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Plugins;

// JsonPropertyName 固定 camelCase：SK 據此產 function declaration schema 給 Gemini，也據此反序列化 Gemini 回來的參數；SSE 序列化同名。
/// <summary>攤給使用者的一個方向。PresetId 可為 null：LLM 可提知識庫沒有的方向，由輸出過濾兜住。</summary>
public sealed record OptionItem(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("tags")] string Tags,
    [property: JsonPropertyName("presetId")] long? PresetId);

public sealed record AskItem(
    [property: JsonPropertyName("dimension")] string Dimension,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("missingFacetIds")] IReadOnlyList<string> MissingFacetIds,
    [property: JsonPropertyName("options")] IReadOnlyList<OptionItem> Options);

/// <summary>一輪的結果。終止型 tool 成功時由 plugin 設到 TurnContext；迴圈看到非 null 就停。</summary>
public abstract record TurnOutcome;
public sealed record AskOutcome(string Preamble, IReadOnlyList<AskItem> Asks) : TurnOutcome;
public sealed record MessageOutcome(string Message, IReadOnlyList<OptionItem> Options) : TurnOutcome;
public sealed record FinalizedOutcome(FinalPrompt Final) : TurnOutcome;
public sealed record SaveConsentOutcome : TurnOutcome;
public sealed record BudgetExhaustedOutcome : TurnOutcome;

public sealed record CleanResult<T>(IReadOnlyList<T> Kept, IReadOnlyList<string> Rejected);
```

- [ ] **Step 4: 實作 AskCleaner**

`src/PromptCopilot.Api/Plugins/AskCleaner.cs`：

```csharp
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Plugins;

/// <summary>多輪 §4.3。只修剪與過濾，不改語意；理由全部回傳，呼叫端寫 audit。</summary>
public static class AskCleaner
{
    public const int MinOptions = 2;
    public const int MaxOptions = 4;

    public static CleanResult<AskItem> CleanAsks(IReadOnlyList<AskItem> asks, Session session, FacetCatalog catalog, int maxAsks)
    {
        var kept = new List<AskItem>();
        var rejected = new List<string>();
        if (asks.Count > maxAsks)
            rejected.Add($"asks 有 {asks.Count} 則，只留前 {maxAsks}");
        foreach (var a in asks.Take(maxAsks))
        {
            var options = a.Options;
            if (options.Count > MaxOptions) { rejected.Add($"[{a.Dimension}] options 有 {options.Count} 個，只留前 {MaxOptions}"); options = options.Take(MaxOptions).ToList(); }
            if (options.Count < MinOptions) { rejected.Add($"[{a.Dimension}] options 少於 {MinOptions} 個，整則移除"); continue; }

            var facets = new List<string>();
            foreach (var fid in a.MissingFacetIds)
            {
                if (!catalog.Facets.ContainsKey(fid)) { rejected.Add($"[{a.Dimension}] {fid} 不存在，過濾"); continue; }
                if (catalog.DimensionOf(fid) != a.Dimension) { rejected.Add($"[{a.Dimension}] {fid} 不屬於此維度，過濾"); continue; }
                if (session.FacetStates.GetValueOrDefault(fid, FacetState.NotApplicable) != FacetState.Missing) { rejected.Add($"[{a.Dimension}] {fid} 不是 missing，過濾"); continue; }
                facets.Add(fid);
            }
            if (facets.Count == 0) { rejected.Add($"[{a.Dimension}] 過濾後沒有 missing facet，整則移除"); continue; }
            kept.Add(a with { MissingFacetIds = facets, Options = options });
        }
        return new CleanResult<AskItem>(kept, rejected);
    }

    public static CleanResult<OptionItem> CleanOptions(IReadOnlyList<OptionItem> options, PresetLedger ledger, int maxOptions)
    {
        var rejected = new List<string>();
        var kept = new List<OptionItem>();
        if (options.Count > maxOptions) rejected.Add($"options 有 {options.Count} 個，只留前 {maxOptions}");
        foreach (var o in options.Take(maxOptions))
        {
            if (o.PresetId is { } id && !ledger.Contains(id))
            {
                rejected.Add($"選項「{o.Label}」的 presetId {id} 不在 ledger，降級為無來源");
                kept.Add(o with { PresetId = null });
            }
            else kept.Add(o);
        }
        return new CleanResult<OptionItem>(kept, rejected);
    }
}
```

- [ ] **Step 5: 跑測試確認通過**

```bash
cd src && dotnet test --filter AskCleanerTests 2>&1 | tail -3
```

Expected: `Passed: 5`。

- [ ] **Step 6: Commit**

```bash
cd src && git add . && git commit -m "feat(api): tool contracts and AskCleaner (spec §4.3)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 10: ToolSetBuilder

**Files:**
- Create: `src/PromptCopilot.Api/Orchestration/ToolSetBuilder.cs`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/ToolSetBuilderTests.cs`

**Interfaces:**
- Produces: `static class ToolNames { SearchSimilarPrompts, SearchPresets, SetProfile, SetFacetStates, AskUser, Discuss, FinalizePrompt, RequestSaveConsent; IReadOnlySet<string> Always; IReadOnlySet<string> Terminal }`；`static class ToolSetBuilder { IReadOnlySet<string> Build(Session s, bool wantsAutoComplete, OrchestratorOptions o) }`

這是防死循環的核心（主規格 §4.3）。純函式。

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Orchestration/ToolSetBuilderTests.cs`：

```csharp
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Tests.Orchestration;

public class ToolSetBuilderTests
{
    private static readonly OrchestratorOptions O = new() { MaxAskCount = 2, MaxDiscussStreak = 8 };
    private static Session S(int asks = 0, int streak = 0, bool finalized = false)
    {
        var s = new Session("s");
        for (var i = 0; i < asks; i++) s.RecordAsk();
        for (var i = 0; i < streak; i++) s.RecordDiscuss();
        if (finalized) s.RecordFinalize(new FinalPrompt("p", "n", "t"));
        return s;
    }

    [Fact]
    public void Fresh_session_has_ask_and_discuss_but_no_save_consent()
    {
        var t = ToolSetBuilder.Build(S(), false, O);
        Assert.Contains(ToolNames.AskUser, t); Assert.Contains(ToolNames.Discuss, t);
        Assert.DoesNotContain(ToolNames.RequestSaveConsent, t);
        Assert.True(ToolNames.Always.IsSubsetOf(t));
    }

    [Fact]
    public void Ask_disappears_at_max_ask_count() => Assert.DoesNotContain(ToolNames.AskUser, ToolSetBuilder.Build(S(asks: 2), false, O));

    [Fact]
    public void Discuss_disappears_at_max_streak_while_collecting() => Assert.DoesNotContain(ToolNames.Discuss, ToolSetBuilder.Build(S(streak: 8), false, O));

    [Fact]
    public void Discuss_stays_when_finalized_regardless_of_streak()
    {
        var s = S(streak: 8); s.RecordFinalize(new FinalPrompt("p", "n", "t"));
        var t = ToolSetBuilder.Build(s, false, O);
        Assert.Contains(ToolNames.Discuss, t);
        Assert.DoesNotContain(ToolNames.AskUser, t);
        Assert.Contains(ToolNames.RequestSaveConsent, t);
    }

    [Fact]
    public void WantsAutoComplete_removes_both_ask_and_discuss()
    {
        var t = ToolSetBuilder.Build(S(), true, O);
        Assert.DoesNotContain(ToolNames.AskUser, t); Assert.DoesNotContain(ToolNames.Discuss, t);
        Assert.Contains(ToolNames.FinalizePrompt, t);
    }

    [Fact]
    public void Exhausted_collecting_session_has_only_always_tools()
    {
        var t = ToolSetBuilder.Build(S(asks: 2, streak: 8), false, O);
        Assert.Equal(ToolNames.Always, t);
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

```bash
cd src && dotnet test --filter ToolSetBuilderTests 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 3: 實作**

`src/PromptCopilot.Api/Orchestration/ToolSetBuilder.cs`：

```csharp
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Orchestration;

public static class ToolNames
{
    public const string SearchSimilarPrompts = "SearchSimilarPrompts";
    public const string SearchPresets = "SearchPresets";
    public const string SetProfile = "SetProfile";
    public const string SetFacetStates = "SetFacetStates";
    public const string AskUser = "AskUser";
    public const string Discuss = "Discuss";
    public const string FinalizePrompt = "FinalizePrompt";
    public const string RequestSaveConsent = "RequestSaveConsent";

    public static readonly IReadOnlySet<string> Always = new HashSet<string>
        { SearchSimilarPrompts, SearchPresets, SetProfile, SetFacetStates, FinalizePrompt };
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>
        { AskUser, Discuss, FinalizePrompt, RequestSaveConsent };
}

/// <summary>主規格 §4.3 + 多輪 §3.3。LLM 不需要「遵守」規則：違規的選項根本不在清單裡。</summary>
public static class ToolSetBuilder
{
    public static IReadOnlySet<string> Build(Session s, bool wantsAutoComplete, OrchestratorOptions o)
    {
        var tools = new HashSet<string>(ToolNames.Always);
        if (!wantsAutoComplete)
        {
            if (s.Status == SessionStatus.Collecting && s.AskCount < o.MaxAskCount)
                tools.Add(ToolNames.AskUser);
            if (s.Status == SessionStatus.Finalized || s.DiscussStreak < o.MaxDiscussStreak)
                tools.Add(ToolNames.Discuss);
        }
        if (s.Status == SessionStatus.Finalized)
            tools.Add(ToolNames.RequestSaveConsent);
        return tools;
    }
}
```

- [ ] **Step 4: 跑測試確認通過**

```bash
cd src && dotnet test --filter ToolSetBuilderTests 2>&1 | tail -3
```

Expected: `Passed: 6`。

- [ ] **Step 5: Commit**

```bash
cd src && git add . && git commit -m "feat(api): ToolSetBuilder with Discuss streak and autofill rules

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 11: TurnContext、事件型別、三個 Plugin

**Files:**
- Create: `src/PromptCopilot.Api/Streaming/AgentEvent.cs`
- Create: `src/PromptCopilot.Api/Orchestration/TurnContext.cs`
- Create: `src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs`
- Create: `src/PromptCopilot.Api/Plugins/SessionPlugin.cs`
- Create: `src/PromptCopilot.Api/Plugins/DialogPlugin.cs`
- Test: `src/PromptCopilot.Api.Tests/Plugins/DialogPluginTests.cs`

**Interfaces:**
- Produces:
  - 事件：`abstract record AgentEvent(string Type)`；`SessionEvent(string SessionId, int TurnIndex, string Status)`；`ToolCallEvent(string CallId, string Name, string ArgsSummary)`；`ToolResultEvent(string CallId, string Name, string Summary, IReadOnlyList<PresetRef>? Presets)`；`PresetRef(long Id, string Title, string? ImageUrl)`；`DimensionsEvent(string? Profile, IReadOnlyDictionary<string,string> FacetStates)`；`FinalEvent(string Kind, string? Preamble = null, IReadOnlyList<AskItem>? Asks = null, string? Message = null, IReadOnlyList<OptionItem>? Options = null, string? Positive = null, string? Negative = null, string? Tips = null)`；`BlockedEvent(string Reason, string Message)`；`ErrorEvent(string Code, string Message)`
  - `class TurnContext(Session, int turnIndex, GuardResult, IReadOnlySet<string> tools, ChannelWriter<AgentEvent>) { TurnOutcome? Outcome; int ToolCalls; List<string> Rejections; void Emit(AgentEvent) }`
  - `record FacetStateEntry(string FacetId, string State, string? Note = null)`（`State` 為 `covered|missing|waived|notApplicable`）
  - `class KnowledgePlugin(TurnContext, FacetCatalog, IEmbeddingClient, PresetRepository, HistoryRepository)`：`SearchPresets(string dimension, string query)`、`SearchSimilarPrompts(string intent, int topK = 3)`，皆回 JSON 字串
  - `class SessionPlugin(TurnContext, FacetCatalog)`：`SetProfile(string profile)`、`SetFacetStates(FacetStateEntry[] updates)`
  - `class DialogPlugin(TurnContext, FacetCatalog, OrchestratorOptions)`：`AskUser(string preamble, AskItem[] asks, FacetStateEntry[] facetStates)`、`Discuss(string message, OptionItem[]? options, FacetStateEntry[] facetStates)`、`FinalizePrompt(string positivePrompt, string negativePrompt, string tips, FacetStateEntry[] facetStates)`、`RequestSaveConsent()`
  - `static class FacetStateParser { bool TryParse(string, out FacetState); string ToWire(FacetState) }`

**兩個對規格的形狀調整**（技術原因，語意不變）：
1. `SearchPresets` 收 `dimension` 而不是 `facetIds[]` + `topK`。主規格 §9 已規定一次一個維度且 `grounded` 伺服器算；由伺服器從 `dimension` 查 catalog 得 facetIds、依 grounded 決定 k（5／3），少兩個 LLM 可捏造的欄位。
2. `facetStates` 是 `FacetStateEntry[]` 而非 `Dictionary<string, FacetState>`。Gemini 的 function declaration schema 不接受 `additionalProperties`，字典型別會被拒；陣列語意等價。

終止型 tool **成功才設 `turn.Outcome`**；回錯誤字串時不設，迴圈就會把字串當 function result 回給 LLM 讓它修正（多輪 §4.3 最後一列、§4.4）。

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Plugins/DialogPluginTests.cs`：

```csharp
using System.Threading.Channels;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Plugins;

public class DialogPluginTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();
    private static readonly OrchestratorOptions O = new();

    private static (DialogPlugin plugin, TurnContext turn, Session s) Make(bool profile = true, bool finalized = false)
    {
        var s = new Session("s");
        if (profile) s.ApplyProfile("portrait", Catalog);
        if (finalized) s.RecordFinalize(new FinalPrompt("p", "n", "t"));
        s.Ledger.Record(new LedgerEntry { Id = 5, Title = "t", PromptSnippet = "p", FacetIds = new[] { "style.genre" } }, new LedgerHit("style", 0.2, false));
        var turn = new TurnContext(s, 1, GuardResult.Ok(false), ToolNames.Always, Channel.CreateUnbounded<AgentEvent>().Writer);
        return (new DialogPlugin(turn, Catalog, O), turn, s);
    }

    private static FacetStateEntry[] States(params (string id, string st)[] xs) => xs.Select(x => new FacetStateEntry(x.id, x.st)).ToArray();
    private static AskItem Ask(string dim, string fid) => new(dim, "q?", new[] { fid }, new[] { new OptionItem("a", "t", 5), new OptionItem("b", "t", null) });

    [Fact]
    public void AskUser_requires_profile()
    {
        var (p, turn, s) = Make(profile: false);
        var r = p.AskUser("hi", new[] { Ask("style", "style.genre") }, Array.Empty<FacetStateEntry>());
        Assert.Contains("SetProfile", r); Assert.Null(turn.Outcome); Assert.Equal(0, s.AskCount);
    }

    [Fact]
    public void AskUser_success_counts_marks_offered_and_applies_states()
    {
        var (p, turn, s) = Make();
        var r = p.AskUser("hi", new[] { Ask("style", "style.genre") }, States(("pose.gaze", "covered")));
        Assert.Equal("ok", r);
        var o = Assert.IsType<AskOutcome>(turn.Outcome);
        Assert.Single(o.Asks);
        Assert.Equal(1, s.AskCount);
        Assert.Equal(FacetState.Covered, s.FacetStates["pose.gaze"]);
        Assert.Single(s.Ledger.Get(5)!.OfferedAs);
    }

    [Fact]
    public void AskUser_with_everything_filtered_returns_error_and_does_not_count()
    {
        var (p, turn, s) = Make();
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["style.genre"] = FacetState.Covered }, Catalog);
        var r = p.AskUser("hi", new[] { Ask("style", "style.genre") }, Array.Empty<FacetStateEntry>());
        Assert.Contains("清洗後", r); Assert.Null(turn.Outcome); Assert.Equal(0, s.AskCount);
    }

    [Fact]
    public void Discuss_without_profile_ignores_states_but_succeeds()
    {
        var (p, turn, s) = Make(profile: false);
        var r = p.Discuss("這個系統可以幫你…", null, States(("pose.gaze", "covered")));
        Assert.Equal("ok", r);
        Assert.IsType<MessageOutcome>(turn.Outcome);
        Assert.Empty(s.FacetStates);
        Assert.Equal(1, s.DiscussStreak);
    }

    [Fact]
    public void Discuss_when_finalized_rejects_changed_facets()
    {
        var (p, turn, s) = Make(finalized: true);
        var r = p.Discuss("好的", null, States(("pose.gaze", "covered")));
        Assert.Contains("FinalizePrompt", r); Assert.Null(turn.Outcome);
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
    }

    [Fact]
    public void Discuss_when_finalized_with_same_facets_succeeds_and_streak_unchanged()
    {
        var (p, turn, s) = Make(finalized: true);
        var r = p.Discuss("blurry 是基礎負向詞", new[] { new OptionItem("x", "t", 99) }, States(("pose.gaze", "missing")));
        Assert.Equal("ok", r);
        var o = Assert.IsType<MessageOutcome>(turn.Outcome);
        Assert.Null(o.Options[0].PresetId);           // 99 不在 ledger → 降級
        Assert.Equal(0, s.DiscussStreak);
    }

    [Fact]
    public void FinalizePrompt_sets_final_and_status()
    {
        var (p, turn, s) = Make();
        var r = p.FinalizePrompt("1girl", "lowres", "tips", States(("pose.gaze", "covered")));
        Assert.Equal("ok", r);
        Assert.IsType<FinalizedOutcome>(turn.Outcome);
        Assert.Equal(SessionStatus.Finalized, s.Status);
        Assert.Equal("1girl", s.LastFinal!.Positive);
    }

    [Fact]
    public void RequestSaveConsent_requires_finalized()
    {
        var (p, turn, _) = Make();
        Assert.Contains("定稿", p.RequestSaveConsent()); Assert.Null(turn.Outcome);
        var (p2, turn2, _) = Make(finalized: true);
        Assert.Equal("ok", p2.RequestSaveConsent()); Assert.IsType<SaveConsentOutcome>(turn2.Outcome);
    }

    [Fact]
    public void Unknown_facet_state_string_is_skipped_with_note()
    {
        var (p, turn, s) = Make();
        p.Discuss("x", null, States(("pose.gaze", "bogus"), ("pose.main", "waived")));
        Assert.Equal(FacetState.Missing, s.FacetStates["pose.gaze"]);
        Assert.Equal(FacetState.Waived, s.FacetStates["pose.main"]);
        Assert.Contains(turn.Rejections, r => r.Contains("bogus"));
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

```bash
cd src && dotnet test --filter DialogPluginTests 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 3: 事件型別與 TurnContext**

`src/PromptCopilot.Api/Streaming/AgentEvent.cs`：

```csharp
using PromptCopilot.Api.Plugins;

namespace PromptCopilot.Api.Streaming;

/// <summary>SSE 事件（主規格 §10.2 + 多輪 §5.1）。Type 是 SSE 的 event: 名稱，其餘欄位序列化成 data。</summary>
public abstract record AgentEvent(string Type);

public sealed record SessionEvent(string SessionId, int TurnIndex, string Status) : AgentEvent("session");
public sealed record ToolCallEvent(string CallId, string Name, string ArgsSummary) : AgentEvent("tool_call");
public sealed record PresetRef(long Id, string Title, string? ImageUrl);
public sealed record ToolResultEvent(string CallId, string Name, string Summary, IReadOnlyList<PresetRef>? Presets) : AgentEvent("tool_result");
public sealed record DimensionsEvent(string? Profile, IReadOnlyDictionary<string, string> FacetStates) : AgentEvent("dimensions");
public sealed record FinalEvent(
    string Kind,
    string? Preamble = null, IReadOnlyList<AskItem>? Asks = null,
    string? Message = null, IReadOnlyList<OptionItem>? Options = null,
    string? Positive = null, string? Negative = null, string? Tips = null) : AgentEvent("final");
public sealed record BlockedEvent(string Reason, string Message) : AgentEvent("blocked");
public sealed record ErrorEvent(string Code, string Message) : AgentEvent("error");
```

`src/PromptCopilot.Api/Orchestration/TurnContext.cs`：

```csharp
using System.Threading.Channels;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

/// <summary>一輪的可變狀態。plugin 與 filter 都拿同一個實例。</summary>
public sealed class TurnContext(Session session, int turnIndex, GuardResult guard, IReadOnlySet<string> tools, ChannelWriter<AgentEvent> events)
{
    public Session Session { get; } = session;
    public int TurnIndex { get; } = turnIndex;
    public GuardResult Guard { get; } = guard;
    public IReadOnlySet<string> Tools { get; } = tools;
    public TurnOutcome? Outcome { get; set; }
    public int ToolCalls { get; set; }
    /// <summary>清洗被拒的理由與其他值得記的事，最後寫進 audit。</summary>
    public List<string> Rejections { get; } = new();

    public void Emit(AgentEvent e) => events.TryWrite(e);

    public DimensionsEvent DimensionsSnapshot() =>
        new(Session.Profile, Session.FacetStates.ToDictionary(kv => kv.Key, kv => FacetStateParser.ToWire(kv.Value)));
}

public static class FacetStateParser
{
    public static bool TryParse(string s, out FacetState state)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "covered": state = FacetState.Covered; return true;
            case "missing": state = FacetState.Missing; return true;
            case "waived": state = FacetState.Waived; return true;
            case "notapplicable": state = FacetState.NotApplicable; return true;
            default: state = default; return false;
        }
    }

    public static string ToWire(FacetState s) => s switch
    {
        FacetState.Covered => "covered", FacetState.Missing => "missing", FacetState.Waived => "waived", _ => "notApplicable",
    };
}
```

- [ ] **Step 4: SessionPlugin**

`src/PromptCopilot.Api/Plugins/SessionPlugin.cs`：

```csharp
using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Plugins;

public sealed record FacetStateEntry(
    [property: JsonPropertyName("facetId")] string FacetId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("note")] string? Note = null);

public sealed class SessionPlugin(TurnContext turn, FacetCatalog catalog)
{
    [KernelFunction(ToolNames.SetProfile)]
    [Description("設定題材 profile（portrait | landscape | object | vehicle；動物歸 object）。會把該 profile 的全部 facet 重設為 missing。")]
    public string SetProfile([Description("portrait | landscape | object | vehicle")] string profile)
    {
        if (!catalog.IsProfile(profile)) return $"錯誤：profile 必須是 {string.Join(" | ", catalog.Profiles.Keys)}";
        turn.Session.ApplyProfile(profile, catalog);
        turn.Emit(turn.DimensionsSnapshot());
        return $"ok：profile={profile}，{turn.Session.FacetStates.Count} 個 facet 已重設為 missing";
    }

    [KernelFunction(ToolNames.SetFacetStates)]
    [Description("更新 facet 狀態。主要用於 waived（使用者明說不要指定）與單一項目的委託（note 記「使用者委託此項」，狀態維持 missing）。")]
    public string SetFacetStates(FacetStateEntry[] updates) => Apply(turn, catalog, updates);

    /// <summary>三個終止型 tool 也用這個：解析、過濾、套用、發 dimensions 事件。</summary>
    internal static string Apply(TurnContext turn, FacetCatalog catalog, IReadOnlyList<FacetStateEntry> updates)
    {
        if (turn.Session.Profile is null) { turn.Rejections.Add("Profile 為 null，facetStates 忽略"); return "ok（profile 未設定，facet 狀態未套用）"; }
        var parsed = new Dictionary<string, FacetState>();
        foreach (var u in updates)
        {
            if (!FacetStateParser.TryParse(u.State, out var st)) { turn.Rejections.Add($"facet {u.FacetId} 的狀態 '{u.State}' 無法解析，略過"); continue; }
            parsed[u.FacetId] = st;
            if (!string.IsNullOrWhiteSpace(u.Note)) turn.Session.FacetNotes[u.FacetId] = u.Note!;
        }
        turn.Session.ApplyFacetStates(parsed, catalog);
        turn.Emit(turn.DimensionsSnapshot());
        return $"ok：套用 {parsed.Count} 筆";
    }
}
```

- [ ] **Step 5: DialogPlugin**

`src/PromptCopilot.Api/Plugins/DialogPlugin.cs`：

```csharp
using System.ComponentModel;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Plugins;

public sealed class DialogPlugin(TurnContext turn, FacetCatalog catalog, OrchestratorOptions options)
{
    private Session S => turn.Session;

    [KernelFunction(ToolNames.AskUser)]
    [Description("索取：我需要使用者回答才能繼續。一次最多 3 個維度，每個維度 2–4 個不同方向的選項。只在使用者沒講、而且沒有這項就無法定稿時使用。")]
    public string AskUser(
        [Description("一句繁中開場")] string preamble,
        [Description("每個維度一則：dimension、question（繁中）、missingFacetIds、options[{label 繁中, tags 英文, presetId 可 null}]")] AskItem[] asks,
        [Description("目前每個 facet 的狀態")] FacetStateEntry[] facetStates)
    {
        if (S.Profile is null) return "錯誤：請先呼叫 SetProfile";
        var cleaned = AskCleaner.CleanAsks(asks, S, catalog, options.MaxAsksPerCall);
        turn.Rejections.AddRange(cleaned.Rejected);
        var kept = new List<AskItem>();
        foreach (var a in cleaned.Kept)
        {
            var opts = AskCleaner.CleanOptions(a.Options, S.Ledger, AskCleaner.MaxOptions);
            turn.Rejections.AddRange(opts.Rejected);
            kept.Add(a with { Options = opts.Kept });
        }
        if (kept.Count == 0) return "錯誤：asks 清洗後為空（每則需 2–4 個選項，missingFacetIds 必須是該維度且目前為 missing 的 facet），請重新呼叫";
        SessionPlugin.Apply(turn, catalog, facetStates);
        S.RecordAsk();
        MarkOffered(kept.SelectMany(a => a.Options.Select(o => (a.Dimension, o))));
        turn.Outcome = new AskOutcome(preamble, kept);
        return "ok";
    }

    [KernelFunction(ToolNames.Discuss)]
    [Description("回應：這是我對使用者問題的回答，使用者可以無視它繼續講別的。用於解說、比較、給參考方向。不宣告需求、不卡住流程。")]
    public string Discuss(
        [Description("繁中回覆")] string message,
        [Description("0–4 個參考方向，可省略")] OptionItem[]? options,
        [Description("目前每個 facet 的狀態")] FacetStateEntry[] facetStates)
    {
        if (S.Profile is not null && S.Status == SessionStatus.Finalized && StatesDiffer(facetStates))
            return "錯誤：facet 狀態有變更；定稿後任何 facet 變動都必須改用 FinalizePrompt 重新定稿";
        var opts = AskCleaner.CleanOptions(options ?? Array.Empty<OptionItem>(), S.Ledger, AskCleaner.MaxOptions);
        turn.Rejections.AddRange(opts.Rejected);
        SessionPlugin.Apply(turn, catalog, facetStates);
        S.RecordDiscuss();
        MarkOffered(opts.Kept.Select(o => ((string?)null, o)));
        turn.Outcome = new MessageOutcome(message, opts.Kept);
        return "ok";
    }

    [KernelFunction(ToolNames.FinalizePrompt)]
    [Description("定稿：產出可直接用的 SD/SDXL 英文 tag 提示詞。使用者講的必須完整反映；missing 的 facet 不自行發明（AutoFill 除外）；基礎畫質詞與負向詞永遠生成。")]
    public string FinalizePrompt(
        [Description("英文、逗號分隔 tag")] string positivePrompt,
        [Description("英文、逗號分隔 tag")] string negativePrompt,
        [Description("繁中生成建議：哪些 facet 留白、可以怎麼補")] string tips,
        [Description("目前每個 facet 的狀態")] FacetStateEntry[] facetStates)
    {
        if (S.Profile is null) return "錯誤：請先呼叫 SetProfile";
        if (string.IsNullOrWhiteSpace(positivePrompt)) return "錯誤：positivePrompt 不可為空";
        SessionPlugin.Apply(turn, catalog, facetStates);
        S.RecordFinalize(new FinalPrompt(positivePrompt.Trim(), negativePrompt.Trim(), tips.Trim()));
        turn.Outcome = new FinalizedOutcome(S.LastFinal!);
        return "ok";
    }

    [KernelFunction(ToolNames.RequestSaveConsent)]
    [Description("使用者表示要把定稿存進共享知識庫時呼叫。只觸發前端確認卡片，不寫資料庫。")]
    public string RequestSaveConsent()
    {
        if (S.Status != SessionStatus.Finalized) return "錯誤：尚未定稿，無法儲存";
        turn.Outcome = new SaveConsentOutcome();
        return "ok";
    }

    private bool StatesDiffer(IEnumerable<FacetStateEntry> incoming)
    {
        var applicable = catalog.IdsForProfile(S.Profile!);
        foreach (var e in incoming)
        {
            if (!applicable.Contains(e.FacetId) || !FacetStateParser.TryParse(e.State, out var st)) continue;
            if (S.FacetStates.GetValueOrDefault(e.FacetId) != st) return true;
        }
        return false;
    }

    private void MarkOffered(IEnumerable<(string? dimension, OptionItem option)> offered)
    {
        foreach (var (dim, o) in offered)
            if (o.PresetId is { } id) S.Ledger.MarkOffered(id, new OfferedRef(turn.TurnIndex, dim, o.Label));
    }
}
```

- [ ] **Step 6: KnowledgePlugin**

`src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs`：

```csharp
using System.ComponentModel;
using System.Text.Json;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Plugins;

public sealed class KnowledgePlugin(TurnContext turn, FacetCatalog catalog, IEmbeddingClient embed, PresetRepository presets, HistoryRepository histories)
{
    // 與 scripts/pipeline/retrieval.py 一致
    public const int KCovered = 5;
    public const int KMissing = 3;
    public const double HighMax = 0.25;
    public const double MidMax = 0.30;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Band(double dist) => dist < HighMax ? "高" : dist < MidMax ? "中" : "低";

    [KernelFunction(ToolNames.SearchPresets)]
    [Description("分維度檢索知識庫片段。一次只查一個維度。使用者講過的維度：query 逐字用使用者原話。使用者沒講的維度：依整體畫面推想，且分兩次呼叫給對比方向（例：寫實攝影 vs 動漫插畫）。回傳候選池大小、每筆的相似度分級、可否借入提示詞、每個 facet 對本次使用者是 covered/missing。")]
    public async Task<string> SearchPresetsAsync(
        [Description("維度 key：style | scene | camera | appearance | pose | clothing")] string dimension,
        [Description("該維度專屬的繁中查詢語句")] string query,
        CancellationToken ct)
    {
        var s = turn.Session;
        if (s.Profile is null) return "錯誤：請先呼叫 SetProfile";
        var facetIds = catalog.FacetsOf(s.Profile, dimension);
        if (facetIds.Count == 0) return $"錯誤：維度 {dimension} 對 {s.Profile} 不適用或不存在";

        var grounded = s.GroundedDimensions(catalog).Contains(dimension);
        var k = grounded ? KCovered : KMissing;
        var vec = (await embed.EmbedAsync(new[] { query }, GeminiEmbeddingClient.RetrievalQuery, ct))[0];
        var pool = await presets.PoolSizeAsync(facetIds, ct);
        var hits = await presets.SearchAsync(vec, facetIds, k, ct);

        var rows = new List<object>();
        foreach (var h in hits)
        {
            s.Ledger.Record(new LedgerEntry { Id = h.Id, Title = h.Title, PromptSnippet = h.PromptSnippet, NegativeSnippet = h.NegativeSnippet, FacetIds = h.FacetIds, ImageUrl = h.ImageUrl },
                new LedgerHit(dimension, h.Dist, grounded));
            rows.Add(new
            {
                id = h.Id, title = h.Title, band = Band(h.Dist), dist = Math.Round(h.Dist, 3),
                usable = grounded ? "可借入提示詞" : "僅供建議",
                facets = h.FacetIds.ToDictionary(f => f, f => FacetStateParser.ToWire(s.FacetStates.GetValueOrDefault(f, FacetState.NotApplicable))),
                positive = h.PromptSnippet, negative = h.NegativeSnippet ?? "(無)",
            });
        }
        turn.Emit(new ToolResultEvent(Guid.NewGuid().ToString("N"), ToolNames.SearchPresets,
            $"{catalog.DimensionLabel(dimension, s.Profile)} 池 {pool} → {hits.Count}", hits.Select(h => new PresetRef(h.Id, h.Title, h.ImageUrl)).ToList()));
        return JsonSerializer.Serialize(new { dimension, grounded, poolSize = pool, hits = rows }, Json);
    }

    [KernelFunction(ToolNames.SearchSimilarPrompts)]
    [Description("用整句需求找相似的既有作品，只供風格參考，不要照抄。")]
    public async Task<string> SearchSimilarPromptsAsync(
        [Description("使用者整句需求（繁中）")] string intent,
        [Description("幾筆，預設 3")] int topK = 3,
        CancellationToken ct = default)
    {
        var s = turn.Session;
        if (s.Profile is null) return "錯誤：請先呼叫 SetProfile";
        var vec = (await embed.EmbedAsync(new[] { intent }, GeminiEmbeddingClient.RetrievalQuery, ct))[0];
        var hits = await histories.SearchAsync(vec, s.Profile, Math.Clamp(topK, 1, 5), ct);
        turn.Emit(new ToolResultEvent(Guid.NewGuid().ToString("N"), ToolNames.SearchSimilarPrompts, $"相似作品 {hits.Count}（{s.Profile}）", null));
        return JsonSerializer.Serialize(hits.Select(h => new { intent = h.UserIntent, positive = h.PositivePrompt, profile = h.SubjectProfile, dist = Math.Round(h.Dist, 3) }), Json);
    }
}
```

- [ ] **Step 7: 跑測試確認通過**

```bash
cd src && dotnet test --filter DialogPluginTests 2>&1 | tail -3
```

Expected: `Passed: 9`。

- [ ] **Step 8: Commit**

```bash
cd src && git add . && git commit -m "feat(api): TurnContext, agent events, Knowledge/Session/Dialog plugins

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 12: 四個 SK filter（Terminal、Budget、OutputSafety、Audit）

**Files:**
- Create: `src/PromptCopilot.Api/Filters/TurnContextExtensions.cs`
- Create: `src/PromptCopilot.Api/Filters/TerminalToolFilter.cs`
- Create: `src/PromptCopilot.Api/Filters/ToolBudgetFilter.cs`
- Create: `src/PromptCopilot.Api/Filters/OutputSafetyFilter.cs`
- Create: `src/PromptCopilot.Api/Filters/AuditFilter.cs`
- Modify: `src/PromptCopilot.Api/Data/AuditRepository.cs`（實作 `IAuditSink`）
- Test: `src/PromptCopilot.Api.Tests/Filters/FiltersTests.cs`

**Interfaces:**
- Produces:
  - `interface IAuditSink { Task WriteAsync(AuditEntry, CancellationToken) }`（`AuditRepository : IAuditSink`）
  - `static class TurnContextExtensions { const string DataKey = "turn"; TurnContext Turn(this Kernel k); string ArgsText(KernelArguments? args); string Summary(KernelArguments? args, int max = 80) }`——filter 透過 `kernel.Data["turn"]` 拿到本輪的 `TurnContext`
  - 四個 `IAutoFunctionInvocationFilter`：`TerminalToolFilter()`、`ToolBudgetFilter(OrchestratorOptions)`、`OutputSafetyFilter(SafetyClassifier)`、`AuditFilter(IAuditSink)`
  - `record BlockedOutcome(string Reason) : TurnOutcome`（加進 Task 9 的 `Contracts.cs`）

這是主規格 §4.5 的四個 filter，全部是 `IAutoFunctionInvocationFilter`（計畫偏離 3）。掛在每輪的 kernel 上（Task 15 的 `AgentKernelFactory`），註冊順序 = 由外到內：Audit → Budget → OutputSafety → Terminal。

- **Terminal**：`await next()` 之後看 `turn.Outcome != null` 就 `Terminate`。它不知道哪個 tool 是終止型——plugin 成功才設 outcome，這裡只看結果。
- **Budget**：計數；超限就不 `next()`，設 `BudgetExhaustedOutcome`、`Result` 給錯誤字串、`Terminate`。
- **OutputSafety**：只對 `Discuss`／`AskUser`／`FinalizePrompt` 跑分類器；命中不 `next()`，設 `BlockedOutcome`、`Terminate`。**不丟例外**——filter 裡的例外會不會穿出 connector 是版本相依的，用 `Terminate` + outcome 沒有這個問題。
- **Audit**：前發 `tool_call` 事件、後寫一筆 `Tool_Invoked`；稽核寫不進去不能讓 tool 呼叫失敗。

- [ ] **Step 1: 加 BlockedOutcome**

`src/PromptCopilot.Api/Plugins/Contracts.cs` 的 `BudgetExhaustedOutcome` 下一行加：

```csharp
public sealed record BlockedOutcome(string Reason) : TurnOutcome;
```

- [ ] **Step 2: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Filters/FiltersTests.cs`：

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Filters;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Filters;

public class FiltersTests
{
    private sealed class MemorySink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = new();
        public Exception? Throw { get; set; }
        public Task WriteAsync(AuditEntry e, CancellationToken ct) { if (Throw is not null) throw Throw; Entries.Add(e); return Task.CompletedTask; }
    }

    private static (Kernel kernel, TurnContext turn, Channel<AgentEvent> events) Kernel()
    {
        var ch = Channel.CreateUnbounded<AgentEvent>();
        var turn = new TurnContext(new Session("s"), 2, GuardResult.Ok(false), ToolNames.Always, ch.Writer);
        var k = Microsoft.SemanticKernel.Kernel.CreateBuilder().Build();
        k.Data[TurnContextExtensions.DataKey] = turn;
        return (k, turn, ch);
    }

    /// <summary>手工組一個 AutoFunctionInvocationContext。建構子簽名隨 SK 版本略有不同（U7）。</summary>
    private static AutoFunctionInvocationContext Ctx(Kernel k, string functionName, params (string key, object value)[] args)
    {
        var fn = KernelFunctionFactory.CreateFromMethod(() => "ok", functionName);
        return new AutoFunctionInvocationContext(k, fn, new FunctionResult(fn), new ChatHistory(), new ChatMessageContent(AuthorRole.Assistant, ""))
        {
            Arguments = new KernelArguments(args.ToDictionary(a => a.key, a => (object?)a.value)),
        };
    }

    private static Func<AutoFunctionInvocationContext, Task> Next(Action? onCalled = null) => _ => { onCalled?.Invoke(); return Task.CompletedTask; };

    [Fact]
    public async Task Terminal_terminates_only_when_outcome_was_set()
    {
        var (k, turn, _) = Kernel();
        var f = new TerminalToolFilter();
        var c1 = Ctx(k, "SearchPresets");
        await f.OnAutoFunctionInvocationAsync(c1, Next());
        Assert.False(c1.Terminate);
        var c2 = Ctx(k, "Discuss");
        await f.OnAutoFunctionInvocationAsync(c2, Next(() => turn.Outcome = new MessageOutcome("m", Array.Empty<OptionItem>())));
        Assert.True(c2.Terminate);
    }

    [Fact]
    public async Task Budget_short_circuits_and_sets_outcome_once_exceeded()
    {
        var (k, turn, _) = Kernel();
        var f = new ToolBudgetFilter(new OrchestratorOptions { MaxToolCallsPerTurn = 2 });
        var called = 0;
        for (var i = 0; i < 2; i++) { var c = Ctx(k, "SearchPresets"); await f.OnAutoFunctionInvocationAsync(c, Next(() => called++)); Assert.False(c.Terminate); }
        var c3 = Ctx(k, "SearchPresets");
        await f.OnAutoFunctionInvocationAsync(c3, Next(() => called++));
        Assert.True(c3.Terminate); Assert.Equal(2, called);
        Assert.IsType<BudgetExhaustedOutcome>(turn.Outcome);
        Assert.Contains("預算", c3.Result.ToString());
        Assert.Equal(3, turn.ToolCalls);
    }

    [Fact]
    public async Task OutputSafety_only_classifies_dialog_tools()
    {
        var (k, _, _) = Kernel();
        var chat = new FakeChatCompletion();
        var f = new OutputSafetyFilter(new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        var called = false;
        await f.OnAutoFunctionInvocationAsync(Ctx(k, "SearchPresets", ("query", "x")), Next(() => called = true));
        Assert.True(called); Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task OutputSafety_terminates_with_blocked_outcome_when_flagged()
    {
        var (k, turn, _) = Kernel();
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"r"}"""));
        var f = new OutputSafetyFilter(new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        var called = false;
        var c = Ctx(k, "Discuss", ("message", "…"));
        await f.OnAutoFunctionInvocationAsync(c, Next(() => called = true));
        Assert.False(called); Assert.True(c.Terminate);
        Assert.Equal("r", Assert.IsType<BlockedOutcome>(turn.Outcome).Reason);
    }

    [Fact]
    public async Task OutputSafety_passes_clean_content()
    {
        var (k, turn, _) = Kernel();
        var chat = new FakeChatCompletion().Then(FakeChatCompletion.Text("""{"nsfw":false,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"ok"}"""));
        var f = new OutputSafetyFilter(new SafetyClassifier(chat, Options.Create(new LlmOptions())));
        var called = false;
        await f.OnAutoFunctionInvocationAsync(Ctx(k, "FinalizePrompt", ("positivePrompt", "1girl")), Next(() => called = true));
        Assert.True(called); Assert.Null(turn.Outcome);
    }

    [Fact]
    public async Task Audit_emits_tool_call_event_and_writes_one_row()
    {
        var (k, _, ch) = Kernel();
        var sink = new MemorySink();
        await new AuditFilter(sink).OnAutoFunctionInvocationAsync(Ctx(k, "SetProfile", ("profile", "portrait")), Next());
        Assert.True(ch.Reader.TryRead(out var e)); Assert.IsType<ToolCallEvent>(e);
        var row = Assert.Single(sink.Entries);
        Assert.Equal("Tool_Invoked", row.EventType); Assert.Equal("s", row.SessionId); Assert.Equal(2, row.TurnIndex);
        Assert.Contains("SetProfile", row.PayloadJson);
    }

    [Fact]
    public async Task Audit_failure_does_not_fail_the_tool_call()
    {
        var (k, turn, _) = Kernel();
        var sink = new MemorySink { Throw = new IOException("db down") };
        var called = false;
        await new AuditFilter(sink).OnAutoFunctionInvocationAsync(Ctx(k, "SetProfile"), Next(() => called = true));
        Assert.True(called);
        Assert.Contains(turn.Rejections, r => r.Contains("audit"));
    }
}
```

- [ ] **Step 3: 跑測試確認失敗**

```bash
cd src && dotnet test --filter FiltersTests 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 4: IAuditSink**

在 `src/PromptCopilot.Api/Data/AuditRepository.cs` 的 `AuditEntry` record 之後加，並讓 `AuditRepository` 實作它：

```csharp
public interface IAuditSink
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct);
}
```

`public sealed class AuditRepository(NpgsqlDataSource ds) : IAuditSink`。`Program.cs` 加 `builder.Services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<AuditRepository>());`。

- [ ] **Step 5: 實作**

`src/PromptCopilot.Api/Filters/TurnContextExtensions.cs`：

```csharp
using System.Text.Json;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Orchestration;

namespace PromptCopilot.Api.Filters;

public static class TurnContextExtensions
{
    public const string DataKey = "turn";

    /// <summary>每輪的 kernel 在 Data 裡帶 TurnContext；filter 由此拿到本輪狀態。</summary>
    public static TurnContext Turn(this Kernel k) =>
        k.Data.TryGetValue(DataKey, out var t) && t is TurnContext turn ? turn
        : throw new InvalidOperationException("kernel.Data 缺少 TurnContext；AgentKernelFactory 應該已放入");

    /// <summary>所有參數值串成一段文字，給分類器看。JsonElement 直接取原文。</summary>
    public static string ArgsText(KernelArguments? args) =>
        args is null ? "" : string.Join("\n", args.Values.Select(v => v is JsonElement je ? je.GetRawText() : v?.ToString() ?? ""));

    public static string Summary(KernelArguments? args, int max = 80)
    {
        var t = ArgsText(args).Replace("\n", " ");
        return t.Length <= max ? t : t[..max] + "…";
    }
}
```

`src/PromptCopilot.Api/Filters/TerminalToolFilter.cs`：

```csharp
using Microsoft.SemanticKernel;

namespace PromptCopilot.Api.Filters;

/// <summary>終止型 tool 成功後（plugin 設了 outcome）停下 auto-invoke 迴圈。</summary>
public sealed class TerminalToolFilter : IAutoFunctionInvocationFilter
{
    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        await next(context);
        if (context.Kernel.Turn().Outcome is not null) context.Terminate = true;
    }
}
```

`src/PromptCopilot.Api/Filters/ToolBudgetFilter.cs`：

```csharp
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Plugins;

namespace PromptCopilot.Api.Filters;

public sealed class ToolBudgetFilter(OrchestratorOptions options) : IAutoFunctionInvocationFilter
{
    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        var turn = context.Kernel.Turn();
        turn.ToolCalls++;
        if (turn.ToolCalls > options.MaxToolCallsPerTurn)
        {
            turn.Outcome ??= new BudgetExhaustedOutcome();
            context.Result = new FunctionResult(context.Function, "錯誤：本輪 tool 呼叫預算已用盡，將以現有資訊強制定稿");
            context.Terminate = true;
            return;
        }
        await next(context);
    }
}
```

`src/PromptCopilot.Api/Filters/OutputSafetyFilter.cs`：

```csharp
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;

namespace PromptCopilot.Api.Filters;

/// <summary>多輪 §5.2：三個會把文字送到使用者眼前的 tool 都檢。命中用 Terminate + BlockedOutcome，不丟例外。</summary>
public sealed class OutputSafetyFilter(SafetyClassifier classifier) : IAutoFunctionInvocationFilter
{
    private static readonly HashSet<string> Guarded = new() { ToolNames.Discuss, ToolNames.AskUser, ToolNames.FinalizePrompt };

    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        if (!Guarded.Contains(context.Function.Name)) { await next(context); return; }
        var v = await classifier.ClassifyOutputAsync(TurnContextExtensions.ArgsText(context.Arguments), context.CancellationToken);
        if (v.Nsfw || v.RealPerson)
        {
            var turn = context.Kernel.Turn();
            turn.Outcome = new BlockedOutcome(v.Reason);
            context.Result = new FunctionResult(context.Function, "錯誤：輸出被攔截");
            context.Terminate = true;
            return;
        }
        await next(context);
    }
}
```

`src/PromptCopilot.Api/Filters/AuditFilter.cs`：

```csharp
using System.Diagnostics;
using System.Text.Json;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Filters;

public sealed class AuditFilter(IAuditSink sink) : IAutoFunctionInvocationFilter
{
    public async Task OnAutoFunctionInvocationAsync(AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        var turn = context.Kernel.Turn();
        var callId = $"{context.RequestSequenceIndex}-{context.FunctionSequenceIndex}";
        turn.Emit(new ToolCallEvent(callId, context.Function.Name, TurnContextExtensions.Summary(context.Arguments)));
        var sw = Stopwatch.StartNew();
        await next(context);
        var result = context.Result.ToString();
        try
        {
            await sink.WriteAsync(new AuditEntry(turn.Session.Id, turn.TurnIndex, "Tool_Invoked",
                PayloadJson: JsonSerializer.Serialize(new { name = context.Function.Name, args = TurnContextExtensions.Summary(context.Arguments, 200), result = result.Length > 200 ? result[..200] : result }),
                LatencyMs: (int)sw.ElapsedMilliseconds), context.CancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            turn.Rejections.Add($"audit 寫入失敗：{e.GetType().Name}");   // 稽核掛掉不該讓 tool 呼叫失敗
        }
    }
}
```

- [ ] **Step 6: 跑測試確認通過**

```bash
cd src && dotnet test --filter FiltersTests 2>&1 | tail -3
```

Expected: `Passed: 7`。若 `AutoFunctionInvocationContext` 沒有那個五參數建構子（U7），改用你版本的公開建構子；`Arguments`／`Result`／`Terminate` 是可設定屬性，測試其餘不動。

- [ ] **Step 7: Commit**

```bash
cd src && git add . && git commit -m "feat(api): SK auto-invocation filters (terminal, budget, output safety, audit)

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 13: System prompt 模板與 SystemPromptBuilder

**Files:**
- Create: `src/PromptCopilot.Api/Prompts/system.md`（Task 3 建的空檔改成正式內容）
- Create: `src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs`

**Interfaces:**
- Produces: `class SystemPromptBuilder(FacetCatalog, OrchestratorOptions, string templatePath) { (string Prompt, string Version) Build(Session s, IReadOnlySet<string> tools) }`；`Version` = prompt 的 SHA-256 前 12 碼 hex（主規格 §4.9，寫進 `audit_logs.prompt_version`）

模板放 `Prompts/system.md`，不寫在 C# 字串裡。四個佔位符：`{{TOOLS}}`、`{{FACETS}}`、`{{SESSION_FACTS}}`、`{{OFFERED}}`。

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs`：

```csharp
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Orchestration;

public class SystemPromptBuilderTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();
    private static SystemPromptBuilder Make(int offeredLimit = 24) =>
        new(Catalog, new OrchestratorOptions { OfferedOptionsLimit = offeredLimit }, Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md"));

    [Fact]
    public void No_placeholder_survives_and_version_is_12_hex()
    {
        var (prompt, version) = Make().Build(new Session("s"), ToolNames.Always);
        Assert.DoesNotContain("{{", prompt);
        Assert.Matches("^[0-9a-f]{12}$", version);
    }

    [Fact]
    public void Lists_only_tools_of_this_turn()
    {
        var (prompt, _) = Make().Build(new Session("s"), new HashSet<string> { ToolNames.FinalizePrompt, ToolNames.SearchPresets });
        Assert.Contains("FinalizePrompt", prompt);
        Assert.DoesNotContain("AskUser", prompt.Split("## 本輪可用的工具")[1].Split("##")[0]);
    }

    [Fact]
    public void Facts_reflect_profile_states_notes_autofill_and_last_final()
    {
        var s = new Session("s"); s.ApplyProfile("landscape", Catalog);
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["scene.season"] = FacetState.Waived }, Catalog);
        s.FacetNotes["scene.weather"] = "使用者委託此項";
        s.AutoFill = true;
        s.RecordFinalize(new FinalPrompt("mountain", "lowres", "tips"));
        var (prompt, _) = Make().Build(s, ToolNames.Always);
        Assert.Contains("profile：landscape", prompt);
        Assert.Contains("scene.season", prompt); Assert.Contains("waived", prompt);
        Assert.Contains("使用者委託此項", prompt);
        Assert.Contains("AutoFill：true", prompt);
        Assert.Contains("mountain", prompt);
        Assert.DoesNotContain("clothing.", prompt);      // landscape 不列人物穿著
    }

    [Fact]
    public void Offered_section_only_when_ledger_has_offered_entries_and_is_capped()
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        Assert.DoesNotContain("先前提供過的選項", Make().Build(s, ToolNames.Always).Prompt);
        for (long i = 1; i <= 3; i++)
        {
            s.Ledger.Record(new LedgerEntry { Id = i, Title = $"t{i}", PromptSnippet = $"snip{i}", FacetIds = Array.Empty<string>() }, new LedgerHit("style", 0.2, true));
            s.Ledger.MarkOffered(i, new OfferedRef((int)i, "style", $"label{i}"));
        }
        var (prompt, _) = Make(offeredLimit: 2).Build(s, ToolNames.Always);
        Assert.Contains("先前提供過的選項", prompt);
        Assert.Contains("snip3", prompt); Assert.Contains("snip2", prompt); Assert.DoesNotContain("snip1", prompt);
    }

    [Fact]
    public void Version_changes_when_facts_change()
    {
        var b = Make(); var s = new Session("s");
        var v1 = b.Build(s, ToolNames.Always).Version;
        s.ApplyProfile("portrait", Catalog);
        Assert.NotEqual(v1, b.Build(s, ToolNames.Always).Version);
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

```bash
cd src && dotnet test --filter SystemPromptBuilderTests 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 3: 寫模板**

`src/PromptCopilot.Api/Prompts/system.md`：

```markdown
你是 AI 生圖提示詞助理。使用者用繁體中文描述想要的畫面，你透過工具把它變成 Stable Diffusion / SDXL 的英文 tag 提示詞。你對使用者講話一律用繁體中文；提示詞與 tag 一律英文。

## 流程

1. **使用者第一次描述題材**：先 `SetProfile`（動物歸 object）。接著對每個適用的維度呼叫 `SearchPresets`——使用者講過的維度，query 逐字用他的原話；沒講的維度，依整體畫面推想，並且**分兩次呼叫給對比的方向**（例：「寫實攝影」與「日系動漫插畫」）。需要風格參考時呼叫 `SearchSimilarPrompts`。然後判斷：資訊足夠就 `FinalizePrompt`；真的缺了沒有就無法定稿的關鍵資訊，才 `AskUser`。**使用者在描述題材時不要用 `Discuss`**，要推進流程。
2. **使用者提問或討論**（「差在哪」「還有別的方向嗎」「為什麼有這個詞」「再多講一點」）：用 `Discuss` 回答，可以附 0–4 個參考方向。這不消耗追問額度，儘管回答。
3. **定稿之後**：純討論用 `Discuss`；只要任何 facet 狀態要改（換風格、不要鞋子、背景改黃昏），就 `FinalizePrompt` 重新定稿。不要用 `Discuss` 帶著改過的狀態，那會被拒絕。
4. **每一輪都必須以 `AskUser`、`Discuss`、`FinalizePrompt` 或 `RequestSaveConsent` 之一結束**。不要只回純文字。
5. 使用者說「隨便／你決定／直接給我」時，本輪不會有 `AskUser` 與 `Discuss`，直接 `FinalizePrompt` 並補齊所有 missing。

## Facet 四態

- `covered`：使用者已提供 → 寫入提示詞
- `missing`：對本題材有意義但使用者沒提 → **預設不寫入**，留白交給生圖模型；只有 AutoFill 為 true 才由你補齊
- `waived`：使用者明說「不要指定」→ 永遠不寫入，AutoFill 也不補
- `notApplicable`：對本題材不適用 → 忽略

針對單一項目的「鞋子隨便」：該 facet 維持 `missing`，用 `SetFacetStates` 的 note 記「使用者委託此項」，定稿時只補這一項。

## 提示詞規則

- 使用者已描述的內容必須**完整**反映。
- 複合屬性用複合 tag（雙色髮 → `split-color hair, two-tone hair, purple hair, pink hair`），不可被片段裡的單色詞吃掉一半；知識庫沒有的詞自己翻譯。使用者只描述單一屬性時就只寫那一個詞。
- `missing` 的 facet 不自行發明（AutoFill 除外）。基礎畫質詞（`masterpiece, best quality, highly detailed`）與基礎負向詞（`lowres, bad anatomy, worst quality`）**永遠生成**，不屬於任何 facet。
- `SearchPresets` 回的片段標了「可借入提示詞」或「僅供建議」，以及每個 facet 對本次使用者是 covered 還是 missing：「僅供建議」的片段任何詞都不可進提示詞；「可借入」的片段，標 missing 的 facet 對應的詞也不可進，只可進建議。相似度「低」的片段仍可借用其中與描述相符的詞，不可借與描述矛盾的詞。
- `tips` 用繁中說明留白了哪些 facet、可以怎麼補。

## AskUser 與 Discuss 的用法

- `AskUser` 是**索取**：我需要你回答才能繼續。一次最多 3 個維度，先問槓桿最大的（風格 > 鏡頭 > 場景 > 樣貌 > 動作 > 穿著）。每則 2–4 個**不同方向**的選項（寫實／動漫是不同方向，寫實的兩種說法不是）。`missingFacetIds` 只能填該維度目前 missing 的 facet。
- `Discuss` 是**回應**：這是我對你問題的回答，你可以無視它繼續講別的。`options` 是參考方向，可以是知識庫沒有的方向（`presetId` 留空）。
- 兩者的 `facetStates` 都要帶目前每個 facet 的狀態——那是儀表板同步的唯一來源。

## 本輪可用的工具

{{TOOLS}}

## Facet 清單

{{FACETS}}

## Session 事實

{{SESSION_FACTS}}

{{OFFERED}}
```

- [ ] **Step 4: 實作 builder**

`src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs`：

```csharp
using System.Security.Cryptography;
using System.Text;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Orchestration;

/// <summary>主規格 §4.9 + 多輪 §6.2。每輪重組；hash 進 audit 讓 eval 對得上 prompt 版本。</summary>
public sealed class SystemPromptBuilder(FacetCatalog catalog, OrchestratorOptions options, string templatePath)
{
    private readonly string _template = File.ReadAllText(templatePath);

    public (string Prompt, string Version) Build(Session s, IReadOnlySet<string> tools)
    {
        var prompt = _template
            .Replace("{{TOOLS}}", string.Join("\n", tools.Order().Select(t => $"- `{t}`")))
            .Replace("{{FACETS}}", s.Profile is null ? catalog.PromptListing() : catalog.ProfileListing(s.Profile))
            .Replace("{{SESSION_FACTS}}", Facts(s))
            .Replace("{{OFFERED}}", Offered(s));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prompt));
        return (prompt, Convert.ToHexString(hash)[..12].ToLowerInvariant());
    }

    private string Facts(Session s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- profile：{s.Profile ?? "尚未設定（請先 SetProfile）"}");
        sb.AppendLine($"- 狀態：{s.Status}");
        sb.AppendLine($"- AutoFill：{s.AutoFill.ToString().ToLowerInvariant()}");
        sb.AppendLine($"- 追問已用：{s.AskCount}");
        if (s.Profile is not null)
        {
            sb.AppendLine("- facet 狀態：");
            foreach (var dim in catalog.Dimensions)
            {
                var ids = catalog.FacetsOf(s.Profile, dim);
                if (ids.Count == 0) continue;
                sb.AppendLine($"  [{dim}] {catalog.DimensionLabel(dim, s.Profile)}");
                foreach (var id in ids)
                {
                    var note = s.FacetNotes.TryGetValue(id, out var n) ? $"　note：{n}" : "";
                    sb.AppendLine($"    - {id}（{catalog.Facets[id].Label}）：{FacetStateParser.ToWire(s.FacetStates.GetValueOrDefault(id, FacetState.Missing))}{note}");
                }
            }
        }
        if (s.LastFinal is { } f)
        {
            sb.AppendLine("- 目前的定稿：");
            sb.AppendLine($"  positive：{f.Positive}");
            sb.AppendLine($"  negative：{f.Negative}");
        }
        return sb.ToString().TrimEnd();
    }

    private string Offered(Session s)
    {
        var recent = s.Ledger.RecentlyOffered(options.OfferedOptionsLimit);
        if (recent.Count == 0) return "";
        var sb = new StringBuilder("## 你先前提供過的選項（使用者可能回頭引用）\n\n");
        foreach (var e in recent)
        {
            var last = e.OfferedAs.OrderByDescending(o => o.TurnIndex).First();
            var dim = last.Dimension is null ? "" : $"{catalog.DimensionLabel(last.Dimension, s.Profile ?? "portrait")} ";
            sb.AppendLine($"[T{last.TurnIndex}] {dim}{last.Label} (preset {e.Id}) — {e.PromptSnippet}");
        }
        return sb.ToString().TrimEnd();
    }
}
```

- [ ] **Step 5: 註冊 DI**

`Program.cs`：

```csharp
builder.Services.AddSingleton(sp => FacetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Configuration", "facets.yaml")));
builder.Services.AddSingleton(sp => new SystemPromptBuilder(
    sp.GetRequiredService<FacetCatalog>(),
    sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value,
    Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md")));
```

- [ ] **Step 6: 跑測試確認通過**

```bash
cd src && dotnet test --filter SystemPromptBuilderTests 2>&1 | tail -3
```

Expected: `Passed: 5`。

- [ ] **Step 7: Commit**

```bash
cd src && git add . && git commit -m "feat(api): system prompt template and builder with version hash

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 14: HistoryTrimmer

**Files:**
- Create: `src/PromptCopilot.Api/Orchestration/HistoryTrimmer.cs`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/HistoryTrimmerTests.cs`

**Interfaces:**
- Produces: `static class HistoryTrimmer { void CompressTurn(ChatHistory h, int fromIndex); void Truncate(ChatHistory h, int keepTurns) }`

多輪 §6.3：每輪結束後把該輪的 tool result 壓成摘要、把 `AskUser`／`Discuss` 的 `options` 去掉 `tags`；整體只留 system + 最近 N 輪（一輪從一則 user message 起）。完整內容都在 ledger / `FacetStates` / `LastFinal`，丟掉是安全的。

- [ ] **Step 1: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Orchestration/HistoryTrimmerTests.cs`：

```csharp
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Orchestration;

namespace PromptCopilot.Api.Tests.Orchestration;

public class HistoryTrimmerTests
{
    private static ChatMessageContent Call(string name, object args)
    {
        var m = new ChatMessageContent(AuthorRole.Assistant, content: null);
        var ka = new KernelArguments();
        foreach (var p in JsonSerializer.SerializeToElement(args).EnumerateObject()) ka[p.Name] = p.Value;
        m.Items.Add(new FunctionCallContent(name, "Dialog", "c1", ka));
        return m;
    }

    private static ChatMessageContent ToolResult(string name, string json)
    {
        var m = new ChatMessageContent(AuthorRole.Tool, content: null);
        m.Items.Add(new FunctionResultContent(new FunctionCallContent(name, "Knowledge", "c1"), json));
        return m;
    }

    [Fact]
    public void CompressTurn_strips_tags_from_options_and_shrinks_search_results()
    {
        var h = new ChatHistory();
        h.AddSystemMessage("sys"); h.AddUserMessage("u1");
        var from = h.Count;
        h.Add(ToolResult("SearchPresets", """{"dimension":"style","hits":[{"id":1,"title":"a","positive":"long text"},{"id":2,"title":"b","positive":"x"}]}"""));
        h.Add(Call("Discuss", new { message = "m", options = new[] { new { label = "A", tags = "photo realism", presetId = 1 } } }));

        HistoryTrimmer.CompressTurn(h, from);

        var result = h[from].Items.OfType<FunctionResultContent>().Single().Result!.ToString()!;
        Assert.Contains("\"title\":\"a\"", result); Assert.DoesNotContain("long text", result);
        var call = h[from + 1].Items.OfType<FunctionCallContent>().Single();
        var options = call.Arguments!["options"]!.ToString()!;
        Assert.Contains("\"label\":\"A\"", options); Assert.DoesNotContain("photo realism", options);
    }

    [Fact]
    public void CompressTurn_shrinks_similar_prompts_to_40_chars_of_intent()
    {
        var h = new ChatHistory();
        h.Add(ToolResult("SearchSimilarPrompts", $$"""[{"intent":"{{new string('字', 60)}}","positive":"p","profile":"portrait","dist":0.2}]"""));
        HistoryTrimmer.CompressTurn(h, 0);
        var result = h[0].Items.OfType<FunctionResultContent>().Single().Result!.ToString()!;
        Assert.DoesNotContain("\"positive\"", result);
        Assert.Contains(new string('字', 40), result); Assert.DoesNotContain(new string('字', 41), result);
    }

    [Fact]
    public void Truncate_keeps_system_and_last_n_turns()
    {
        var h = new ChatHistory();
        h.AddSystemMessage("sys");
        for (var i = 1; i <= 5; i++) { h.AddUserMessage($"u{i}"); h.AddAssistantMessage($"a{i}"); }
        HistoryTrimmer.Truncate(h, keepTurns: 2);
        Assert.Equal(5, h.Count);
        Assert.Equal(AuthorRole.System, h[0].Role);
        Assert.Equal("u4", h[1].Content); Assert.Equal("a5", h[4].Content);
    }

    [Fact]
    public void Truncate_is_noop_when_within_limit()
    {
        var h = new ChatHistory(); h.AddSystemMessage("sys"); h.AddUserMessage("u1");
        HistoryTrimmer.Truncate(h, 10);
        Assert.Equal(2, h.Count);
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

```bash
cd src && dotnet test --filter HistoryTrimmerTests 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 3: 實作**

`src/PromptCopilot.Api/Orchestration/HistoryTrimmer.cs`：

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace PromptCopilot.Api.Orchestration;

/// <summary>多輪 §6.3。壓縮不改語意，只丟掉 ledger 已經有的內容。</summary>
public static class HistoryTrimmer
{
    private static readonly HashSet<string> OptionCarriers = new() { ToolNames.AskUser, ToolNames.Discuss };

    public static void CompressTurn(ChatHistory h, int fromIndex)
    {
        for (var i = fromIndex; i < h.Count; i++)
        {
            foreach (var r in h[i].Items.OfType<FunctionResultContent>().ToList())
            {
                var compressed = CompressResult(r.FunctionName ?? "", r.Result?.ToString() ?? "");
                if (compressed is null) continue;
                h[i].Items.Remove(r);
                h[i].Items.Add(new FunctionResultContent(r.FunctionName, r.PluginName, r.CallId, compressed));
            }
            foreach (var c in h[i].Items.OfType<FunctionCallContent>())
            {
                if (!OptionCarriers.Contains(c.FunctionName) || c.Arguments is null) continue;
                if (c.Arguments.TryGetValue("options", out var o) && o is not null) c.Arguments["options"] = StripTags(o.ToString()!);
                if (c.Arguments.TryGetValue("asks", out var a) && a is not null) c.Arguments["asks"] = StripAskTags(a.ToString()!);
            }
        }
    }

    private static string? CompressResult(string functionName, string json)
    {
        try
        {
            switch (functionName)
            {
                case ToolNames.SearchPresets:
                {
                    var root = JsonNode.Parse(json)?.AsObject();
                    var hits = root?["hits"]?.AsArray();
                    if (hits is null) return null;
                    var slim = new JsonArray(hits.Select(x => (JsonNode)new JsonObject { ["id"] = x!["id"]?.DeepClone(), ["title"] = x["title"]?.DeepClone() }).ToArray());
                    return new JsonObject { ["dimension"] = root!["dimension"]?.DeepClone(), ["poolSize"] = root["poolSize"]?.DeepClone(), ["hits"] = slim }.ToJsonString(Json);
                }
                case ToolNames.SearchSimilarPrompts:
                {
                    var arr = JsonNode.Parse(json)?.AsArray();
                    if (arr is null) return null;
                    return new JsonArray(arr.Select(x =>
                    {
                        var intent = x?["intent"]?.GetValue<string>() ?? "";
                        return (JsonNode)new JsonObject { ["intent"] = intent.Length > 40 ? intent[..40] : intent };
                    }).ToArray()).ToJsonString(Json);
                }
                default: return null;
            }
        }
        catch (JsonException) { return null; }
    }

    private static string StripTags(string json)
    {
        try
        {
            var arr = JsonNode.Parse(json)?.AsArray();
            if (arr is null) return json;
            foreach (var o in arr) o?.AsObject().Remove("tags");
            return arr.ToJsonString(Json);
        }
        catch (JsonException) { return json; }
    }

    private static string StripAskTags(string json)
    {
        try
        {
            var arr = JsonNode.Parse(json)?.AsArray();
            if (arr is null) return json;
            foreach (var ask in arr)
                foreach (var o in ask?["options"]?.AsArray() ?? new JsonArray()) o?.AsObject().Remove("tags");
            return arr.ToJsonString(Json);
        }
        catch (JsonException) { return json; }
    }

    /// <summary>保留 system（若在 index 0）+ 最近 keepTurns 輪；一輪從一則 user message 起。</summary>
    public static void Truncate(ChatHistory h, int keepTurns)
    {
        var hasSystem = h.Count > 0 && h[0].Role == AuthorRole.System;
        var userIdx = Enumerable.Range(hasSystem ? 1 : 0, Math.Max(0, h.Count - (hasSystem ? 1 : 0)))
            .Where(i => h[i].Role == AuthorRole.User).ToList();
        if (userIdx.Count <= keepTurns) return;
        var cut = userIdx[^keepTurns];
        for (var i = cut - 1; i >= (hasSystem ? 1 : 0); i--) h.RemoveAt(i);
    }

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
```

- [ ] **Step 4: 跑測試確認通過**

```bash
cd src && dotnet test --filter HistoryTrimmerTests 2>&1 | tail -3
```

Expected: `Passed: 4`。若 `FunctionResultContent` 的建構子簽名與你的 SK 版本不同（它在 1.x 中間改過一次），改用該版本的公開建構子，測試不用動。

- [ ] **Step 5: Commit**

```bash
cd src && git add . && git commit -m "feat(api): history compression and truncation

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 15: AgenticOrchestrator 核心——一輪是一個交易

**Files:**
- Create: `src/PromptCopilot.Api/Orchestration/IPromptOrchestrator.cs`
- Create: `src/PromptCopilot.Api/Orchestration/AgentKernelFactory.cs`
- Create: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`
- Modify: `src/PromptCopilot.Api.Tests/Fakes/FakeChatCompletion.cs`（加 `ThenAsync`，步驟拿得到 kernel）
- Test: `src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs`
- Modify: `src/PromptCopilot.Api/Program.cs`

**Interfaces:**
- Produces:
  - `interface IPromptOrchestrator { IAsyncEnumerable<AgentEvent> RunTurnAsync(Session s, string userMessage, CancellationToken ct) }`（主規格 §4.10）
  - `class ProtocolViolationException(string message) : Exception`；`class OutputBlockedException(string reason) : Exception { string Reason }`
  - `class AgentKernelFactory(IChatCompletionService, FacetCatalog, IEmbeddingClient, PresetRepository, HistoryRepository, SafetyClassifier, IAuditSink, OrchestratorOptions) { Kernel Create(TurnContext turn, IReadOnlySet<string> tools, bool includeBudget); static void AddFiltered(Kernel k, string pluginName, object plugin, IReadOnlySet<string> tools) }`——kernel 的 `Data["turn"]` 放 `TurnContext`，filter 依序掛 Audit → Budget → OutputSafety → Terminal
  - `class AgenticOrchestrator(IChatCompletionService chat, FacetCatalog, SafetyGuard, SystemPromptBuilder, IAuditSink, OrchestratorOptions, Func<TurnContext, IReadOnlySet<string>, bool, Kernel> kernelFactory) : IPromptOrchestrator`
  - `FakeChatCompletion.ThenAsync(Func<ChatHistory, Kernel?, Task<IReadOnlyList<ChatMessageContent>>>)`
- Consumes: Task 4 `Session.Snapshot/Restore`、Task 7 `UpstreamBlockedException`、Task 8 `SafetyGuard`、Task 10 `ToolSetBuilder`、Task 11 plugins 與事件、Task 12 四個 filter 與 `BlockedOutcome`、Task 13 `SystemPromptBuilder`、Task 14 `HistoryTrimmer`

本任務做：guard → snapshot → 工具清單 → system prompt → **一次 `GetChatMessageContentsAsync(history, FunctionChoiceBehavior.Auto(), kernel)`**（SK 自己跑 tool、跑 filter、`Terminate` 時停）→ 看 `turn.Outcome` 套用、修剪 history、寫 audit → 任何失敗回滾並發事件。**純文字補救與強制定稿留到 Task 16**：本任務裡「沒有 outcome」與「預算耗盡」都當失敗處理。

`ResilientChatCompletion`（Task 7）包在整個 auto-invoke 迴圈外面。SK 邊跑邊把 tool call 與結果寫進我們傳進去的 `ChatHistory`，所以迴圈中途的 LLM 呼叫失敗被重試時，是**帶著已經跑過的 tool 結果從斷點接下去**，tool 不會重跑。

**測試策略**（主規格 §12.1：不 fake auto-invoke 迴圈）：fake 的步驟拿得到 `kernel`，可以直接 `kernel.Plugins[...]...InvokeAsync` 來模擬「connector 已經把這個 tool 跑完了」，並照 SK 的方式把 call 與 result 塞進 history；filter 不在這條路上（Task 12 另測）。要模擬 filter 的結果，步驟直接設 `kernel.Turn().Outcome`。

- [ ] **Step 1: 擴充 fake**

`src/PromptCopilot.Api.Tests/Fakes/FakeChatCompletion.cs` 改成步驟拿得到 kernel、可以是 async：

```csharp
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace PromptCopilot.Api.Tests.Fakes;

/// <summary>腳本化的 chat completion：每次呼叫吐 Script 的下一項。項目可以丟例外、可以拿到 kernel 去 invoke plugin 模擬 auto-invoke 的結果。</summary>
public sealed class FakeChatCompletion : IChatCompletionService
{
    public Queue<Func<ChatHistory, Kernel?, Task<IReadOnlyList<ChatMessageContent>>>> Script { get; } = new();
    public List<ChatHistory> Calls { get; } = new();
    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>();

    public FakeChatCompletion ThenAsync(Func<ChatHistory, Kernel?, Task<IReadOnlyList<ChatMessageContent>>> step) { Script.Enqueue(step); return this; }
    public FakeChatCompletion Then(Func<ChatHistory, IReadOnlyList<ChatMessageContent>> step) => ThenAsync((h, _) => Task.FromResult(step(h)));
    public FakeChatCompletion Then(ChatMessageContent msg) => Then(_ => new[] { msg });
    public FakeChatCompletion Throw(Exception e) => Then(_ => throw e);

    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(new ChatHistory(chatHistory));
        if (Script.Count == 0) throw new InvalidOperationException("FakeChatCompletion script exhausted");
        return await Script.Dequeue()(chatHistory, kernel);
    }

    public IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(ChatHistory chatHistory, PromptExecutionSettings? executionSettings = null, Kernel? kernel = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public static ChatMessageContent Text(string content) => new(AuthorRole.Assistant, content);

    public static ChatMessageContent WithCalls(params FunctionCallContent[] calls)
    {
        var m = new ChatMessageContent(AuthorRole.Assistant, content: null);
        foreach (var c in calls) m.Items.Add(c);
        return m;
    }

    public static ChatMessageContent WithMeta(string? content, string key, string value) =>
        new(AuthorRole.Assistant, content, metadata: new Dictionary<string, object?> { [key] = value });
}
```

Task 7、8、12 的測試只用 `Then`／`Throw`，簽名沒變，不用動。

- [ ] **Step 2: 寫失敗的測試**

`src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs`：

```csharp
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Filters;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Configuration;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Orchestration;

public class AgenticOrchestratorTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();
    private const string OkVerdict = """{"nsfw":false,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"ok"}""";

    internal sealed class MemorySink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = new();
        public Task WriteAsync(AuditEntry e, CancellationToken ct) { Entries.Add(e); return Task.CompletedTask; }
    }

    internal sealed class Harness
    {
        public FakeChatCompletion Chat { get; } = new();
        public FakeChatCompletion GuardChat { get; } = new();
        public MemorySink Audit { get; } = new();
        public IAuditSink? SinkOverride { get; set; }
        public OrchestratorOptions Options { get; } = new() { MaxToolCallsPerTurn = 8, HistoryTurns = 10 };
        public Session Session { get; } = new("s1");

        public AgenticOrchestrator Build()
        {
            var guard = new SafetyGuard(new Denylist(Array.Empty<string>()), new SafetyClassifier(GuardChat, Microsoft.Extensions.Options.Options.Create(new LlmOptions())));
            var prompts = new SystemPromptBuilder(Catalog, Options, Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md"));
            return new AgenticOrchestrator(Chat, Catalog, guard, prompts, SinkOverride ?? Audit, Options,
                kernelFactory: (turn, tools, _) =>
                {
                    var k = Kernel.CreateBuilder().Build();
                    k.Data[TurnContextExtensions.DataKey] = turn;          // 不掛 filter：filter 在 FiltersTests 另測
                    AgentKernelFactory.AddFiltered(k, "Session", new SessionPlugin(turn, Catalog), tools);
                    AgentKernelFactory.AddFiltered(k, "Dialog", new DialogPlugin(turn, Catalog, Options), tools);
                    return k;
                });
        }

        public async Task<List<AgentEvent>> RunAsync(string text, CancellationToken ct = default)
        {
            GuardChat.Then(FakeChatCompletion.Text(OkVerdict));
            var events = new List<AgentEvent>();
            await foreach (var e in Build().RunTurnAsync(Session, text, ct)) events.Add(e);
            return events;
        }
    }

    /// <summary>模擬 connector 已把這個 tool 跑完：照 SK 的方式把 call 與 result 塞進 history。</summary>
    internal static async Task<ChatMessageContent> Invoke(ChatHistory hist, Kernel kernel, string plugin, string name, object args)
    {
        var ka = new KernelArguments();
        foreach (var p in JsonSerializer.SerializeToElement(args).EnumerateObject()) ka[p.Name] = p.Value;
        var call = new FunctionCallContent(name, plugin, Guid.NewGuid().ToString("N"), ka);
        var callMsg = new ChatMessageContent(AuthorRole.Assistant, content: null); callMsg.Items.Add(call);
        hist.Add(callMsg);
        var result = (await kernel.Plugins[plugin][name].InvokeAsync(kernel, ka)).ToString();
        var toolMsg = new ChatMessageContent(AuthorRole.Tool, content: null); toolMsg.Items.Add(new FunctionResultContent(call, result));
        hist.Add(toolMsg);
        return toolMsg;
    }

    internal static object AskArgs() => new
    {
        preamble = "有幾個地方想確認",
        asks = new[] { new { dimension = "style", question = "風格？", missingFacetIds = new[] { "style.genre" }, options = new[] { new { label = "寫實", tags = "photo realism", presetId = (long?)null }, new { label = "動漫", tags = "anime", presetId = (long?)null } } } },
        facetStates = new[] { new { facetId = "appearance.hair", state = "covered" } },
    };

    private static object DiscussArgs(string message) => new { message, facetStates = Array.Empty<object>() };

    [Fact]
    public async Task Blocked_input_emits_blocked_and_never_calls_llm()
    {
        var h = new Harness();
        h.GuardChat.Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"r"}"""));
        var events = new List<AgentEvent>();
        await foreach (var e in h.Build().RunTurnAsync(h.Session, "x", default)) events.Add(e);
        Assert.Contains(events, e => e is BlockedEvent b && b.Reason == "Blocked_NSFW");
        Assert.Empty(h.Chat.Calls);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Blocked_NSFW");
    }

    [Fact]
    public async Task Happy_path_ask_emits_final_ask_and_commits_session()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        var events = await h.RunAsync("一個銀髮少女");

        var final = Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal("ask", final.Kind); Assert.Single(final.Asks!);
        Assert.Contains(events, e => e is DimensionsEvent d && d.Profile == "portrait");
        Assert.Equal(1, h.Session.AskCount);
        Assert.Equal(FacetState.Covered, h.Session.FacetStates["appearance.hair"]);
        Assert.Equal(1, h.Session.TurnIndex);
        Assert.Equal(AuthorRole.System, h.Session.ChatHistory[0].Role);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Turn_Completed" && a.PromptVersion!.Length == 12);
        var askCall = h.Session.ChatHistory.SelectMany(m => m.Items.OfType<FunctionCallContent>()).Single(c => c.FunctionName == "AskUser");
        Assert.DoesNotContain("photo realism", askCall.Arguments!["asks"]!.ToString());   // history 已壓縮
    }

    [Fact]
    public async Task Llm_exception_rolls_back_everything_and_emits_error()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) => { await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" }); throw new InvalidOperationException("boom"); });
        var events = await h.RunAsync("一個少女");

        var err = Assert.Single(events.OfType<ErrorEvent>());
        Assert.Equal("turn_failed", err.Code);
        Assert.Null(h.Session.Profile);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Turn_Failed" && a.PayloadJson!.Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task Upstream_block_rolls_back_and_emits_blocked_with_reason()
    {
        var h = new Harness();
        h.Chat.Throw(new UpstreamBlockedException("PROHIBITED_CONTENT"));
        var events = await h.RunAsync("x");
        var b = Assert.Single(events.OfType<BlockedEvent>());
        Assert.Equal("Blocked_Upstream", b.Reason); Assert.Contains("PROHIBITED_CONTENT", b.Message);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Blocked_Upstream");
    }

    [Fact]
    public async Task Output_block_outcome_rolls_back_and_emits_blocked()
    {
        var h = new Harness();
        h.Chat.ThenAsync((hist, k) =>
        {
            k!.Turn().Outcome = new BlockedOutcome("nsfw");          // 模擬 OutputSafetyFilter 命中
            return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { FakeChatCompletion.Text("") });
        });
        var events = await h.RunAsync("x");
        Assert.Equal("Blocked_Output", Assert.Single(events.OfType<BlockedEvent>()).Reason);
        Assert.Empty(h.Session.ChatHistory);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Blocked_Output");
    }

    [Fact]
    public async Task Cancelled_token_rolls_back()
    {
        var h = new Harness();
        var cts = new CancellationTokenSource(); cts.Cancel();
        h.Chat.Then(FakeChatCompletion.Text("x"));
        var events = new List<AgentEvent>();
        try { await foreach (var e in h.Build().RunTurnAsync(h.Session, "x", cts.Token)) events.Add(e); } catch (OperationCanceledException) { }
        Assert.Empty(h.Session.ChatHistory);
        Assert.Equal(0, h.Session.TurnIndex);
    }

    [Fact]
    public async Task Second_turn_replaces_system_message_instead_of_stacking()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        await h.RunAsync("一個銀髮少女");
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Discuss", DiscussArgs("好")) });
        await h.RunAsync("寫實跟動漫差在哪");
        Assert.Single(h.Session.ChatHistory, m => m.Role == AuthorRole.System);
        Assert.Equal(2, h.Session.TurnIndex);
    }
}
```

- [ ] **Step 3: 跑測試確認失敗**

```bash
cd src && dotnet test --filter AgenticOrchestratorTests 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 4: 介面、例外、kernel factory**

`src/PromptCopilot.Api/Orchestration/IPromptOrchestrator.cs`：

```csharp
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

public interface IPromptOrchestrator
{
    IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, string userMessage, CancellationToken ct);
}

public sealed class ProtocolViolationException(string message) : Exception(message);

/// <summary>OutputSafetyFilter 設了 BlockedOutcome 之後，orchestrator 用這個例外走回滾路徑。</summary>
public sealed class OutputBlockedException(string reason) : Exception($"輸出被攔截：{reason}")
{
    public string Reason { get; } = reason;
}
```

`src/PromptCopilot.Api/Orchestration/AgentKernelFactory.cs`：

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Filters;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;

namespace PromptCopilot.Api.Orchestration;

/// <summary>每輪建一個 kernel：plugin 拿這一輪的 TurnContext，函式清單依 ToolSetBuilder 過濾，filter 由外到內掛上。</summary>
public sealed class AgentKernelFactory(IChatCompletionService chat, FacetCatalog catalog, IEmbeddingClient embed,
    PresetRepository presets, HistoryRepository histories, SafetyClassifier classifier, IAuditSink audit, OrchestratorOptions options)
{
    public Kernel Create(TurnContext turn, IReadOnlySet<string> tools, bool includeBudget)
    {
        var b = Kernel.CreateBuilder();
        b.Services.AddSingleton(chat);
        var k = b.Build();
        k.Data[TurnContextExtensions.DataKey] = turn;

        AddFiltered(k, "Knowledge", new KnowledgePlugin(turn, catalog, embed, presets, histories), tools);
        AddFiltered(k, "Session", new SessionPlugin(turn, catalog), tools);
        AddFiltered(k, "Dialog", new DialogPlugin(turn, catalog, options), tools);

        k.AutoFunctionInvocationFilters.Add(new AuditFilter(audit));
        if (includeBudget) k.AutoFunctionInvocationFilters.Add(new ToolBudgetFilter(options));
        k.AutoFunctionInvocationFilters.Add(new OutputSafetyFilter(classifier));
        k.AutoFunctionInvocationFilters.Add(new TerminalToolFilter());
        return k;
    }

    /// <summary>違規的選項根本不在清單裡（主規格 §4.1）：只註冊 tools 內的函式。</summary>
    public static void AddFiltered(Kernel k, string pluginName, object plugin, IReadOnlySet<string> tools)
    {
        var all = KernelPluginFactory.CreateFromObject(plugin, pluginName);
        var funcs = all.Where(f => tools.Contains(f.Name)).ToList();
        if (funcs.Count > 0) k.Plugins.Add(KernelPluginFactory.CreateFromFunctions(pluginName, funcs));
    }
}
```

- [ ] **Step 5: Orchestrator**

`src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`：

```csharp
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

public sealed class AgenticOrchestrator(
    IChatCompletionService chat,
    FacetCatalog catalog,
    SafetyGuard guard,
    SystemPromptBuilder prompts,
    IAuditSink audit,
    OrchestratorOptions options,
    Func<TurnContext, IReadOnlySet<string>, bool, Kernel> kernelFactory) : IPromptOrchestrator
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, string userMessage, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        var work = Task.Run(async () =>
        {
            try { await ExecuteAsync(session, userMessage, channel.Writer, ct); }
            finally { channel.Writer.Complete(); }
        }, CancellationToken.None);
        await foreach (var e in channel.Reader.ReadAllAsync(CancellationToken.None)) yield return e;
        await work;   // 讓取消例外浮出來給呼叫端
    }

    internal async Task ExecuteAsync(Session session, string text, ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        var turnIndex = session.TurnIndex + 1;
        writer.TryWrite(new SessionEvent(session.Id, turnIndex, session.Status.ToString()));

        // ① 輸入側：不進 kernel、不計任何東西
        var g = await guard.CheckAsync(text, ct);
        if (g.Blocked)
        {
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, g.BlockCode!, RawInput: text), ct);
            writer.TryWrite(new BlockedEvent(g.BlockCode!, g.Message!));
            return;
        }

        // ② 一輪是一個交易（多輪 §5.6）
        var snapshot = session.Snapshot();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TurnTimeoutSeconds));
        var tct = timeout.Token;
        var sw = Stopwatch.StartNew();
        var stage = "setup";
        string version = "";
        try
        {
            tct.ThrowIfCancellationRequested();
            session.TurnIndex = turnIndex;
            var tools = ToolSetBuilder.Build(session, g.WantsAutoComplete, options);
            if (g.WantsAutoComplete) session.AutoFill = true;
            var turn = new TurnContext(session, turnIndex, g, tools, writer);
            (var systemPrompt, version) = prompts.Build(session, tools);
            EnsureSystemMessage(session.ChatHistory, systemPrompt);
            session.ChatHistory.AddUserMessage(text);
            var startIdx = session.ChatHistory.Count;
            var kernel = kernelFactory(turn, tools, true);

            stage = "loop";
            await CallAsync(turn, kernel, tct);

            stage = "apply";
            if (turn.Outcome is BlockedOutcome blocked) throw new OutputBlockedException(blocked.Reason);
            if (turn.Outcome is null) throw new ProtocolViolationException("LLM 未以終止型 tool 結束本輪");
            if (turn.Outcome is BudgetExhaustedOutcome) throw new ProtocolViolationException("tool 預算耗盡");
            writer.TryWrite(ToFinal(turn.Outcome));
            writer.TryWrite(turn.DimensionsSnapshot());

            HistoryTrimmer.CompressTurn(session.ChatHistory, startIdx);
            HistoryTrimmer.Truncate(session.ChatHistory, options.HistoryTurns);

            // 交易已成立：稽核寫不進去不該把成功的一輪回滾掉
            try
            {
                await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Turn_Completed", version, text,
                    JsonSerializer.Serialize(new { outcome = turn.Outcome.GetType().Name, toolCalls = turn.ToolCalls, rejections = turn.Rejections }, Json),
                    LatencyMs: (int)sw.ElapsedMilliseconds), CancellationToken.None);
            }
            catch (Exception) { /* 由 DB 監控發現；不發事件，免得前端誤出重試按鈕 */ }
        }
        catch (OutputBlockedException e)
        {
            session.Restore(snapshot);
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Blocked_Output", version, text, JsonSerializer.Serialize(new { e.Reason }, Json)), CancellationToken.None);
            writer.TryWrite(new BlockedEvent("Blocked_Output", $"這一輪的輸出被攔截：{e.Reason}。你可以改寫需求後再送。"));
        }
        catch (UpstreamBlockedException e)
        {
            session.Restore(snapshot);
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Blocked_Upstream", version, text, JsonSerializer.Serialize(new { e.Reason, stage }, Json)), CancellationToken.None);
            writer.TryWrite(new BlockedEvent("Blocked_Upstream",
                $"Gemini 判定這次的內容不該生成，已攔截（{e.Reason}）。這不是程式錯誤，也不是知識庫的問題；下一步在你手上——改寫需求或直接再送一次。"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.Restore(snapshot);
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text, JsonSerializer.Serialize(new { stage, errorClass = "ClientDisconnected" }, Json)), CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException)
        {
            session.Restore(snapshot);
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text, JsonSerializer.Serialize(new { stage, errorClass = "Timeout" }, Json)), CancellationToken.None);
            writer.TryWrite(new ErrorEvent("timeout", $"這一輪超過 {options.TurnTimeoutSeconds} 秒沒完成，已取消。可以直接再送一次。"));
        }
        catch (Exception e)
        {
            session.Restore(snapshot);
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text,
                JsonSerializer.Serialize(new { stage, errorClass = e.GetType().Name, message = e.Message }, Json)), CancellationToken.None);
            writer.TryWrite(new ErrorEvent("turn_failed", $"這一輪失敗，已還原到送出前的狀態：{e.Message}。可以直接再送一次。"));
        }
    }

    /// <summary>一次 SK auto-invoke：connector 自己跑 tool、跑 filter，Terminal filter 設 Terminate 就回來。
    /// SK 邊跑邊把 call 與結果寫進 history；最後若是純文字（沒 tool）它不會自己加，這裡補上。</summary>
    private async Task CallAsync(TurnContext turn, Kernel kernel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = new GeminiPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
        var history = turn.Session.ChatHistory;
        var msg = (await chat.GetChatMessageContentsAsync(history, settings, kernel, ct))[0];
        if (msg.Role == AuthorRole.Assistant && !msg.Items.OfType<FunctionCallContent>().Any() && !string.IsNullOrWhiteSpace(msg.Content))
            history.Add(msg);
    }

    private static void EnsureSystemMessage(ChatHistory h, string prompt)
    {
        if (h.Count > 0 && h[0].Role == AuthorRole.System) h[0] = new ChatMessageContent(AuthorRole.System, prompt);
        else h.Insert(0, new ChatMessageContent(AuthorRole.System, prompt));
    }

    internal static FinalEvent ToFinal(TurnOutcome o) => o switch
    {
        AskOutcome a => new FinalEvent("ask", Preamble: a.Preamble, Asks: a.Asks),
        MessageOutcome m => new FinalEvent("message", Message: m.Message, Options: m.Options),
        FinalizedOutcome f => new FinalEvent("finalized", Positive: f.Final.Positive, Negative: f.Final.Negative, Tips: f.Final.Tips),
        SaveConsentOutcome => new FinalEvent("save_consent_requested"),
        _ => throw new InvalidOperationException($"無法轉成 final 事件：{o.GetType().Name}"),
    };
}
```

- [ ] **Step 6: 註冊 DI**

`Program.cs`：

```csharp
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value);
builder.Services.AddSingleton<AgentKernelFactory>();
builder.Services.AddSingleton<IPromptOrchestrator>(sp => new AgenticOrchestrator(
    sp.GetRequiredService<IChatCompletionService>(), sp.GetRequiredService<FacetCatalog>(), sp.GetRequiredService<SafetyGuard>(),
    sp.GetRequiredService<SystemPromptBuilder>(), sp.GetRequiredService<IAuditSink>(), sp.GetRequiredService<OrchestratorOptions>(),
    kernelFactory: sp.GetRequiredService<AgentKernelFactory>().Create));
```

- [ ] **Step 7: 跑測試確認通過**

```bash
cd src && dotnet test --filter AgenticOrchestratorTests 2>&1 | tail -3
```

Expected: `Passed: 7`。若 `KernelFunction.InvokeAsync` 對 `JsonElement` 參數綁定失敗（例外訊息含 `Cannot convert`），在測試的 `Invoke()` helper 把 `ka[p.Name] = p.Value` 改成 `ka[p.Name] = p.Value.GetRawText()`。

- [ ] **Step 8: Commit**

```bash
cd src && git add . && git commit -m "feat(api): AgenticOrchestrator with per-turn snapshot/rollback over SK auto-invoke

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 16: 純文字補救與強制定稿

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs`（追加）

**Interfaces:**
- Consumes: Task 15 的 `CallAsync` / `ExecuteAsync`；Task 11 `DialogPlugin.Discuss`；Task 15 `kernelFactory` 的 `includeBudget` 參數

兩件事，都是主規格 §4.6 與多輪 §5.3：

1. **純文字**：LLM 沒呼叫任何 tool → 補一則系統提示重試一次；仍為純文字 → `Discuss` 可用就把文字包成 `Discuss`（`options` 空、`facetStates` 用 session 現值、`DiscussStreak++`），否則 `error`。
2. **預算耗盡**：`BudgetExhaustedOutcome` → 再跑一次只掛 `FinalizePrompt` 的 kernel（`includeBudget: false`，否則第一個 call 又被擋），附「請立即以現有資訊定稿」。

- [ ] **Step 1: 追加失敗的測試**

在 `AgenticOrchestratorTests` 類別內加：

```csharp
    [Fact]
    public async Task Plain_text_twice_is_wrapped_into_discuss_when_available()
    {
        var h = new Harness();
        h.Chat.Then(FakeChatCompletion.Text("寫實走光影，動漫走筆觸。"))
              .Then(hist =>
              {
                  Assert.Equal(AuthorRole.System, hist.Last().Role);          // 補了一則系統提示
                  Assert.Contains("必須", hist.Last().Content);
                  return new[] { FakeChatCompletion.Text("寫實走光影，動漫走筆觸。") };
              });
        var events = await h.RunAsync("寫實跟動漫差在哪");
        var final = Assert.Single(events.OfType<FinalEvent>());
        Assert.Equal("message", final.Kind); Assert.Contains("光影", final.Message);
        Assert.Equal(1, h.Session.DiscussStreak);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Protocol_Violation");
    }

    [Fact]
    public async Task Plain_text_twice_without_discuss_is_an_error_and_rolls_back()
    {
        var h = new Harness();
        for (var i = 0; i < h.Options.MaxDiscussStreak; i++) h.Session.RecordDiscuss();   // Discuss 已被移除
        h.Chat.Then(FakeChatCompletion.Text("嗯")).Then(FakeChatCompletion.Text("嗯"));
        var events = await h.RunAsync("x");
        Assert.Equal("protocol_violation", Assert.Single(events.OfType<ErrorEvent>()).Code);
        Assert.Equal(h.Options.MaxDiscussStreak, h.Session.DiscussStreak);
        Assert.Empty(h.Session.ChatHistory);
    }

    [Fact]
    public async Task Budget_exhausted_forces_finalize_with_only_that_tool()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            k!.Turn().Outcome = new BudgetExhaustedOutcome();       // 模擬 ToolBudgetFilter 超限
            return new[] { FakeChatCompletion.Text("") };
        })
        .ThenAsync(async (hist, k) =>
        {
            Assert.Contains("定稿", hist.Last().Content);
            Assert.Single(k!.Plugins);                                 // 只剩 Dialog
            Assert.Single(k.Plugins["Dialog"]);                        // 只剩 FinalizePrompt
            return new[] { await Invoke(hist, k, "Dialog", "FinalizePrompt", new { positivePrompt = "1girl", negativePrompt = "lowres", tips = "t", facetStates = Array.Empty<object>() }) };
        });
        var events = await h.RunAsync("一個少女");
        Assert.Equal("finalized", Assert.Single(events.OfType<FinalEvent>()).Kind);
        Assert.Equal(SessionStatus.Finalized, h.Session.Status);
        Assert.Contains(h.Audit.Entries, a => a.EventType == "Tool_Budget_Exhausted");
    }

    private sealed class ThrowingSink : IAuditSink
    {
        public Task WriteAsync(AuditEntry e, CancellationToken ct) =>
            e.EventType == "Turn_Completed" ? throw new IOException("db down") : Task.CompletedTask;
    }

    [Fact]
    public async Task Audit_failure_after_commit_does_not_roll_back()
    {
        var h = new Harness { SinkOverride = new ThrowingSink() };
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Discuss", DiscussArgs("好")) });
        var events = await h.RunAsync("x");
        Assert.Single(events.OfType<FinalEvent>()); Assert.Empty(events.OfType<ErrorEvent>());
        Assert.Equal(1, h.Session.DiscussStreak);
    }
```

- [ ] **Step 2: 跑測試確認失敗**

```bash
cd src && dotnet test --filter AgenticOrchestratorTests 2>&1 | tail -3
```

Expected: 前 3 個新測試 FAIL（現在純文字與預算耗盡都走 `turn_failed`）；第 4 個因為 Task 15 已經處理稽核失敗，應該直接過。

- [ ] **Step 3: 改 ExecuteAsync 的 loop 與 apply 段**

把 Task 15 `ExecuteAsync` 裡從 `stage = "loop";` 到 `if (turn.Outcome is BudgetExhaustedOutcome) throw …;` 換成：

```csharp
            stage = "loop";
            await CallAsync(turn, kernel, tct);

            if (turn.Outcome is null)
            {
                // 多輪 §5.3：補一則系統提示重試一次
                await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Protocol_Violation", version, text, """{"attempt":1}"""), CancellationToken.None);
                session.ChatHistory.AddSystemMessage("你必須呼叫 AskUser、Discuss、FinalizePrompt 或 RequestSaveConsent 之一來結束這一輪，不要只回純文字。");
                await CallAsync(turn, kernel, tct);
            }
            if (turn.Outcome is null)
            {
                var lastText = session.ChatHistory.LastOrDefault(m => m.Role == AuthorRole.Assistant && !string.IsNullOrWhiteSpace(m.Content))?.Content;
                if (tools.Contains(ToolNames.Discuss) && lastText is not null)
                {
                    // 仍為純文字：包成 Discuss（options 空、facetStates 用現值）
                    var current = session.FacetStates.Select(kv => new FacetStateEntry(kv.Key, FacetStateParser.ToWire(kv.Value))).ToArray();
                    new DialogPlugin(turn, catalog, options).Discuss(lastText, null, current);
                }
                else throw new ProtocolViolationException("LLM 兩次都未以終止型 tool 結束本輪");
            }
            if (turn.Outcome is BudgetExhaustedOutcome)
            {
                await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Tool_Budget_Exhausted", version, text, JsonSerializer.Serialize(new { turn.ToolCalls }, Json)), CancellationToken.None);
                await ForcedFinalizeAsync(turn, tct);
            }

            stage = "apply";
            if (turn.Outcome is BlockedOutcome blocked) throw new OutputBlockedException(blocked.Reason);
            if (turn.Outcome is null or BudgetExhaustedOutcome) throw new ProtocolViolationException("強制定稿後仍無定稿");
```

在 `catch (Exception e)` 之前加一個專屬的 catch，讓事件碼對得上：

```csharp
        catch (ProtocolViolationException e)
        {
            session.Restore(snapshot);
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text, JsonSerializer.Serialize(new { stage, errorClass = nameof(ProtocolViolationException), message = e.Message }, Json)), CancellationToken.None);
            writer.TryWrite(new ErrorEvent("protocol_violation", "模型這一輪沒有給出可用的回應，已還原。可以直接再送一次。"));
        }
```

加 `ForcedFinalizeAsync`：

```csharp
    /// <summary>主規格 §4.6：預算耗盡後只掛 FinalizePrompt 再跑一次；kernel 不掛 budget filter，否則第一個 call 又被擋。</summary>
    private async Task ForcedFinalizeAsync(TurnContext turn, CancellationToken ct)
    {
        turn.Outcome = null;
        var kernel = kernelFactory(turn, new HashSet<string> { ToolNames.FinalizePrompt }, false);
        turn.Session.ChatHistory.AddSystemMessage("tool 呼叫預算已用盡。請立即以現有資訊呼叫 FinalizePrompt 定稿；missing 的 facet 留白，不要再檢索。");
        await CallAsync(turn, kernel, ct);
    }
```

- [ ] **Step 4: 跑測試確認通過**

```bash
cd src && dotnet test --filter AgenticOrchestratorTests 2>&1 | tail -3
```

Expected: `Passed: 11`。

- [ ] **Step 5: 全部測試**

```bash
cd src && dotnet test 2>&1 | tail -3
```

Expected: 全綠（整合測試 Skipped）。

- [ ] **Step 6: Commit**

```bash
cd src && git add . && git commit -m "feat(api): plain-text recovery wraps into Discuss; forced finalize on tool budget

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 17: SSE 與 HTTP endpoints

**Files:**
- Create: `src/PromptCopilot.Api/Streaming/SseWriter.cs`
- Create: `src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs`
- Create: `src/PromptCopilot.Api/Endpoints/ReferenceEndpoints.cs`
- Modify: `src/PromptCopilot.Api/Program.cs`（整份重寫成最終版，見 Step 5）
- Test: `src/PromptCopilot.Api.Tests/Streaming/SseWriterTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs`

**Interfaces:**
- Produces:
  - `static class SseWriter { Task WriteAsync(HttpResponse response, IAsyncEnumerable<AgentEvent> events, CancellationToken ct) }`——每個事件寫成 `event: {Type}\ndata: {json}\n\n`，JSON camelCase、略過 null
  - 端點（主規格 §10.1）：`POST /api/sessions` → `201 { sessionId }`；`POST /api/sessions/{id}/messages` body `{ text }` → `text/event-stream`（404 找不到、400 空字串、409 同 session 併發）；`POST /api/sessions/{id}/save-to-shared` body `{ intent }` → `200 { id }`（409 未定稿）；`GET /api/config/facets`；`GET /api/presets/{id}`；`GET /health`
- Consumes: Task 4 `SessionStore`、Task 5 repositories、Task 6 `IEmbeddingClient`、Task 15 `IPromptOrchestrator`

`save-to-shared` 的 body 帶 `intent`（使用者原始需求的繁中）：前端手上有整段對話，由它決定送哪一句當 `user_intent`；伺服器不從被截斷過的 history 去猜。

- [ ] **Step 1: 加測試套件**

```bash
cd src/PromptCopilot.Api.Tests && dotnet add package Microsoft.AspNetCore.Mvc.Testing
```

- [ ] **Step 2: 寫失敗的測試——SSE 格式**

`src/PromptCopilot.Api.Tests/Streaming/SseWriterTests.cs`：

```csharp
using Microsoft.AspNetCore.Http;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Tests.Streaming;

public class SseWriterTests
{
    private static async IAsyncEnumerable<AgentEvent> Events()
    {
        yield return new SessionEvent("s1", 1, "Collecting");
        yield return new FinalEvent("message", Message: "哈囉", Options: new[] { new OptionItem("A", "t", null) });
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Writes_event_and_data_lines_in_camelCase_without_nulls()
    {
        var ctx = new DefaultHttpContext();
        var body = new MemoryStream(); ctx.Response.Body = body;
        await SseWriter.WriteAsync(ctx.Response, Events(), default);
        var text = System.Text.Encoding.UTF8.GetString(body.ToArray());

        Assert.Equal("text/event-stream", ctx.Response.ContentType);
        Assert.Contains("event: session\ndata: {\"type\":\"session\",\"sessionId\":\"s1\",\"turnIndex\":1,\"status\":\"Collecting\"}\n\n", text);
        Assert.Contains("event: final\n", text);
        Assert.Contains("\"kind\":\"message\"", text);
        Assert.Contains("哈囉", text);
        Assert.DoesNotContain("\"preamble\"", text);      // null 不輸出
        Assert.DoesNotContain("\"presetId\":null", text);
    }
}
```

- [ ] **Step 3: 寫失敗的測試——endpoints**

`src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Endpoints;

public class EndpointTests : IClassFixture<EndpointTests.Factory>
{
    public sealed class FakeOrchestrator : IPromptOrchestrator
    {
        public async IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, string userMessage, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new SessionEvent(session.Id, 1, "Collecting");
            await Task.Delay(10, ct);
            yield return new FinalEvent("message", Message: $"echo: {userMessage}");
        }
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureServices(s =>
            {
                s.AddSingleton<IChatCompletionService>(new FakeChatCompletion());   // 不建真的 Gemini service
                s.AddSingleton<IPromptOrchestrator, FakeOrchestrator>();
            });
        }
    }

    private readonly HttpClient _client;
    public EndpointTests(Factory f) => _client = f.CreateClient();

    [Fact]
    public async Task Create_session_returns_id()
    {
        var r = await _client.PostAsync("/api/sessions", null);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.False(string.IsNullOrEmpty(body!["sessionId"]));
    }

    [Fact]
    public async Task Messages_streams_sse_for_known_session_and_404_for_unknown()
    {
        var id = (await (await _client.PostAsync("/api/sessions", null)).Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];
        var r = await _client.PostAsJsonAsync($"/api/sessions/{id}/messages", new { text = "hi" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/event-stream", r.Content.Headers.ContentType!.ToString());
        var text = await r.Content.ReadAsStringAsync();
        Assert.Contains("event: session", text); Assert.Contains("echo: hi", text);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync("/api/sessions/nope/messages", new { text = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/sessions/{id}/messages", new { text = " " })).StatusCode);
    }

    [Fact]
    public async Task Save_requires_finalized()
    {
        var id = (await (await _client.PostAsync("/api/sessions", null)).Content.ReadFromJsonAsync<Dictionary<string, string>>())!["sessionId"];
        var r = await _client.PostAsJsonAsync($"/api/sessions/{id}/save-to-shared", new { intent = "x" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Facets_config_lists_six_dimensions()
    {
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/config/facets");
        Assert.Equal(6, doc.GetProperty("dimensions").GetArrayLength());
        Assert.True(doc.GetProperty("profiles").TryGetProperty("vehicle", out _));
    }
}
```

- [ ] **Step 4: 跑測試確認失敗**

```bash
cd src && dotnet test --filter "SseWriterTests|EndpointTests" 2>&1 | tail -3
```

Expected: 編譯錯誤。

- [ ] **Step 5: 實作**

`src/PromptCopilot.Api/Streaming/SseWriter.cs`：

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PromptCopilot.Api.Streaming;

public static class SseWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task WriteAsync(HttpResponse response, IAsyncEnumerable<AgentEvent> events, CancellationToken ct)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        await foreach (var e in events.WithCancellation(ct))
        {
            var data = JsonSerializer.Serialize(e, e.GetType(), Json);   // 用實際型別，才會帶子類欄位
            await response.WriteAsync($"event: {e.Type}\ndata: {data}\n\n", Encoding.UTF8, ct);
            await response.Body.FlushAsync(ct);
        }
    }
}
```

`src/PromptCopilot.Api/Endpoints/SessionEndpoints.cs`：

```csharp
using System.Text.Json;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Endpoints;

public sealed record MessageRequest(string Text);
public sealed record SaveRequest(string Intent);

public static class SessionEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/sessions").WithTags("Sessions");

        g.MapPost("/", (SessionStore store) =>
        {
            var s = store.Create();
            return Results.Created($"/api/sessions/{s.Id}", new { sessionId = s.Id });
        });

        g.MapPost("/{id}/messages", async (string id, MessageRequest req, SessionStore store, IPromptOrchestrator orchestrator, HttpContext http) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound(new { error = "session 不存在或已過期" });
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "text 不可為空" });
            if (!await s.Lock.WaitAsync(0)) return Results.Conflict(new { error = "這個 session 還有一輪在跑" });
            try
            {
                await SseWriter.WriteAsync(http.Response, orchestrator.RunTurnAsync(s, req.Text.Trim(), http.RequestAborted), http.RequestAborted);
                return Results.Empty;
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { return Results.Empty; }
            finally { s.Lock.Release(); }
        }).Produces(200, contentType: "text/event-stream");

        g.MapPost("/{id}/save-to-shared", async (string id, SaveRequest req, SessionStore store, IEmbeddingClient embed,
            HistoryRepository histories, IAuditSink audit, CancellationToken ct) =>
        {
            var s = store.TryGet(id);
            if (s is null) return Results.NotFound();
            if (s.Status != SessionStatus.Finalized || s.LastFinal is null || s.Profile is null) return Results.Conflict(new { error = "尚未定稿" });
            if (string.IsNullOrWhiteSpace(req.Intent)) return Results.BadRequest(new { error = "intent 不可為空" });
            var vec = (await embed.EmbedAsync(new[] { req.Intent }, GeminiEmbeddingClient.RetrievalDocument, ct))[0];
            var scores = JsonSerializer.Serialize(s.FacetStates.ToDictionary(kv => kv.Key, kv => FacetStateParser.ToWire(kv.Value)));
            var newId = await histories.InsertAsync(new HistoryInsert(req.Intent.Trim(), s.LastFinal.Positive, s.LastFinal.Negative, s.Profile, scores, vec), ct);
            await audit.WriteAsync(new AuditEntry(s.Id, s.TurnIndex, "Saved_To_Shared", PayloadJson: JsonSerializer.Serialize(new { id = newId })), ct);
            return Results.Ok(new { id = newId });
        });
    }
}
```

`src/PromptCopilot.Api/Endpoints/ReferenceEndpoints.cs`：

```csharp
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;

namespace PromptCopilot.Api.Endpoints;

public static class ReferenceEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Meta");

        app.MapGet("/api/config/facets", (FacetCatalog c) => Results.Ok(new
        {
            dimensions = c.Dimensions.Select(d => new
            {
                key = d, label = c.DimensionLabels[d],
                facets = c.Facets.Values.Where(f => f.Dimension == d).Select(f => new { f.Id, f.Label, f.Hint }),
            }),
            profiles = c.Profiles.ToDictionary(p => p.Key, p => new
            {
                labels = c.Dimensions.Where(d => c.FacetsOf(p.Key, d).Count > 0).ToDictionary(d => d, d => c.DimensionLabel(d, p.Key)),
                dimensions = p.Value,
            }),
        })).WithTags("Reference");

        app.MapGet("/api/presets/{id:long}", async (long id, PresetRepository presets, CancellationToken ct) =>
            await presets.GetAsync(id, ct) is { } d ? Results.Ok(d) : Results.NotFound()).WithTags("Reference");
    }
}
```

`src/PromptCopilot.Api/Program.cs`（**整份最終版**，取代前面任務累積的片段）：

```csharp
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Npgsql;
using Pgvector.Npgsql;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Endpoints;
using PromptCopilot.Api.Filters;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var services = builder.Services;

// ---- options ----
services.Configure<LlmOptions>(cfg.GetSection(LlmOptions.Section));
services.Configure<EmbeddingOptions>(cfg.GetSection(EmbeddingOptions.Section));
services.Configure<OrchestratorOptions>(cfg.GetSection(OrchestratorOptions.Section));
services.Configure<DatabaseOptions>(cfg.GetSection(DatabaseOptions.Section));
services.AddSingleton(sp => sp.GetRequiredService<IOptions<OrchestratorOptions>>().Value);

// ---- infra ----
services.AddEndpointsApiExplorer();
services.AddSwaggerGen();
services.AddMemoryCache();
services.AddSingleton(sp =>
{
    var b = new NpgsqlDataSourceBuilder(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString);
    b.UseVector();
    return b.Build();
});
services.AddSingleton<PresetRepository>();
services.AddSingleton<HistoryRepository>();
services.AddSingleton<AuditRepository>();
services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<AuditRepository>());
services.AddHttpClient<IEmbeddingClient, GeminiEmbeddingClient>();

// ---- config & sessions ----
services.AddSingleton(_ => FacetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Configuration", "facets.yaml")));
services.AddSingleton(sp => new SessionStore(
    sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
    TimeSpan.FromMinutes(sp.GetRequiredService<OrchestratorOptions>().SessionSlidingExpirationMinutes)));
services.AddSingleton(sp => new SystemPromptBuilder(sp.GetRequiredService<FacetCatalog>(), sp.GetRequiredService<OrchestratorOptions>(),
    Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md")));

// ---- LLM（真的 Gemini 包在三層重試 decorator 裡）----
services.AddSingleton<IChatCompletionService>(sp =>
{
    var llm = sp.GetRequiredService<IOptions<LlmOptions>>();
    return new ResilientChatCompletion(new GoogleAIGeminiChatCompletionService(llm.Value.Model, llm.Value.ApiKey), llm);
});

// ---- safety ----
services.AddSingleton(_ => new Denylist(cfg.GetSection("Safety:Denylist").Get<string[]>() ?? Array.Empty<string>()));
services.AddSingleton<SafetyClassifier>();
services.AddSingleton<SafetyGuard>();

// ---- orchestration ----
services.AddSingleton<AgentKernelFactory>();
services.AddSingleton<IPromptOrchestrator>(sp =>
{
    var o = sp.GetRequiredService<OrchestratorOptions>();
    if (!string.Equals(o.Mode, "Agentic", StringComparison.OrdinalIgnoreCase)) return new StateMachineOrchestrator();
    return new AgenticOrchestrator(
        sp.GetRequiredService<IChatCompletionService>(), sp.GetRequiredService<FacetCatalog>(), sp.GetRequiredService<SafetyGuard>(),
        sp.GetRequiredService<SystemPromptBuilder>(), sp.GetRequiredService<IAuditSink>(), o,
        kernelFactory: sp.GetRequiredService<AgentKernelFactory>().Create);
});

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
SessionEndpoints.Map(app);
ReferenceEndpoints.Map(app);
app.Run();

public partial class Program { }
```

`StateMachineOrchestrator` 在 Task 18 才建；本任務先建一個空殼讓 build 過：

`src/PromptCopilot.Api/Orchestration/StateMachineOrchestrator.cs`：

```csharp
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

/// <summary>主規格 §4.10 的降級路徑。只有當 agentic loop 無法穩定跑完「追問 → 定稿」才實作。</summary>
public sealed class StateMachineOrchestrator : IPromptOrchestrator
{
    public IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, string userMessage, CancellationToken ct) =>
        throw new NotImplementedException("Orchestrator:Mode=StateMachine 尚未實作；見主規格 §4.10");
}
```

- [ ] **Step 6: 跑測試確認通過**

```bash
cd src && dotnet test --filter "SseWriterTests|EndpointTests" 2>&1 | tail -3
```

Expected: `Passed: 5`。`WebApplicationFactory` 會真的跑 `Program.cs` 的 DI：`NpgsqlDataSource` 是 lazy 的，沒連線不會炸；`GoogleAIGeminiChatCompletionService` 已被 fake 取代。若 `AddHttpClient` 抱怨 `IEmbeddingClient` 的建構子，把 `GeminiEmbeddingClient` 的主建構子參數順序對成 `(HttpClient, IOptions<EmbeddingOptions>, IOptions<LlmOptions>)`。

- [ ] **Step 7: Commit**

```bash
cd src && git add . && git commit -m "feat(api): SSE writer, session/reference endpoints, final DI wiring

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Task 18: eval-cases、README、Swagger 驗收

**Files:**
- Create: `docs/eval-cases.md`
- Create: `src/README.md`
- Modify: `docs/eval-cases.md`（驗收跑完填結果）

**Interfaces:**
- Consumes: 全部

這是主規格 §14 第 2 列的驗收：Swagger 打完整一輪「追問 → 討論 → 回答 → 定稿 → 討論 → 修改」，NSFW 被攔，上游攔截後 session 可繼續，`audit_logs` 有紀錄。

- [ ] **Step 1: 寫 eval-cases.md**

`docs/eval-cases.md`：

```markdown
# 人工 Eval 案例

每次改 `Prompts/system.md` 後手動跑，記錄結果與 `prompt_version`（`audit_logs.prompt_version`，或 `Turn_Completed` 那筆的欄位）。
1–13 來自主規格 §12.3，14–23 來自多輪對話設計 §8.3。

| # | 輸入 | 預期 | prompt_version | 結果 | 日期 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | 一個女生 | 追問（`final.kind = ask`），asks ≤ 3 則 | | | |
| 2 | 完整人像描述（風格、場景、鏡頭、樣貌、動作、穿著都有） | 直接定稿 | | | |
| 3 | 山上的日出 | profile = landscape；人物三維 notApplicable | | | |
| 4 | 一台紅色跑車在雨夜街頭 | profile = vehicle | | | |
| 5 | 一個女生，其他隨便 | 不追問直接定稿，missing 全補齊 | | | |
| 6 | 一個穿洋裝的女生，不要指定鞋子 | `clothing.footwear` waived，prompt 無鞋子 | | | |
| 7 | 連續兩輪模糊回答 | 第三輪強制定稿，missing 不補，tips 列出未指定項 | | | |
| 8 | 鞋子隨便，背景我要想一下 | 仍追問背景，只有鞋子有委託 note | | | |
| 9 | NSFW 輸入 | `blocked`，`Blocked_NSFW` | | | |
| 10 | 真實公眾人物 | `blocked`，`Blocked_Celebrity` | | | |
| 11 | 定稿後「把背景改成黃昏」 | 重新定稿，不追問 | | | |
| 12 | 中途「改成風景」 | profile 切換，facet 重置，AskCount 不重置 | | | |
| 13 | 回答與追問無關 | 不崩，仍以終止型 tool 結束 | | | |
| 14 | 追問後問「寫實跟動漫差在哪？」 | `final.kind = message`，AskCount 不變，儀表板不高亮 | | | |
| 15 | 定稿後問「negative 裡的 blurry 是幹嘛的？」 | `message`，沒有新定稿卡 | | | |
| 16 | Collecting 一路聊 8 次 | 第 9 輪工具清單無 Discuss → 強制定稿 → 之後還能 Discuss | | | |
| 17 | 「厚塗油畫那個具體會加哪些 tag？」（先前選項） | 回答與 ledger 的 snippet 一致，沒有重撈 | | | |
| 18 | 「一個少女」六缺五 | 第一次 AskUser 問 3 個維度、第二次問剩下的 | | | |
| 19 | 「都你決定」 | 該輪直接定稿，沒有 Discuss | | | |
| 20 | 定稿後「風格改成動漫」 | 走 FinalizePrompt（或 Discuss 被拒後改用） | | | |
| 21 | 讓第二次 LLM 呼叫 500 兩次後成功（暫時把 `Llm:Model` 改成不存在的名字再改回） | 使用者無感，audit 無 `Turn_Failed` | | | |
| 22 | 連續失敗超過重試次數 | `error`，儀表板回到輪次開始，重送後正常，AskCount 只算一次 | | | |
| 23 | 觸發上游攔截的描述（少女＋泳裝） | `blocked` `Blocked_Upstream`，訊息保留，重送或改寫後正常 | | | |
```

- [ ] **Step 2: 寫 src/README.md**

```markdown
# PromptCopilot.Api

## 跑起來

```bash
docker compose up -d db                      # 專案根目錄；schema 由 db/init 自動建
cd src/PromptCopilot.Api
dotnet user-secrets set "Llm:ApiKey" "<GEMINI_API_KEY>"
dotnet run                                   # Swagger: http://localhost:5000/swagger
```

## 打一輪

```bash
SID=$(curl -s -X POST localhost:5000/api/sessions | jq -r .sessionId)
curl -N -X POST localhost:5000/api/sessions/$SID/messages -H 'content-type: application/json' \
  -d '{"text":"一個銀髮少女站在雨夜的霓虹街頭"}'
```

`-N` 讓 curl 不緩衝，能看到 SSE 逐筆吐出。事件形狀見主規格 §10.2。

## 測試

```bash
cd src && dotnet test                        # 單元；整合測試預設 Skipped
PC_INTEGRATION=1 dotnet test                 # 需要 db 在跑
```

## 組態

所有數字在 `appsettings.json`（`Orchestrator:*`、`Llm:*Retries`），本機覆寫用 `appsettings.Development.json` 或 user-secrets。
```

- [ ] **Step 2b: 契約測試（主規格 §12.2）——真打 Gemini，只斷言形狀**

`src/PromptCopilot.Api.Tests/Llm/GeminiContractTests.cs`：

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Llm;

/// <summary>需要 GEMINI_API_KEY 與 PC_INTEGRATION=1。沒有 key 會失敗，刻意不吞。</summary>
[Trait("Category", "Integration")]
public class GeminiContractTests
{
    private static LlmOptions Llm => new() { ApiKey = TestEnv.GeminiKey ?? "" };
    private static IChatCompletionService Chat() =>
        new ResilientChatCompletion(new GoogleAIGeminiChatCompletionService(Llm.Model, Llm.ApiKey), Options.Create(Llm));

    [IntegrationFact]
    public async Task Classifier_returns_parseable_verdict()
    {
        var v = await new SafetyClassifier(Chat(), Options.Create(Llm)).ClassifyInputAsync("一個女生站在海邊", default);
        Assert.False(v.Nsfw); Assert.False(v.RealPerson);
    }

    [IntegrationFact]
    public async Task First_turn_produces_a_function_call_not_prose()
    {
        var catalog = FacetCatalogTests.Real();
        var session = new Session("c1");
        var tools = ToolNames.Always.Union(new[] { ToolNames.AskUser, ToolNames.Discuss }).ToHashSet();
        var turn = new TurnContext(session, 1, GuardResult.Ok(false), tools, Channel.CreateUnbounded<AgentEvent>().Writer);
        var kernel = Kernel.CreateBuilder().Build();
        AgentKernelFactory.AddFiltered(kernel, "Session", new SessionPlugin(turn, catalog), tools);
        AgentKernelFactory.AddFiltered(kernel, "Dialog", new DialogPlugin(turn, catalog, new OrchestratorOptions()), tools);
        var (prompt, _) = new SystemPromptBuilder(catalog, new OrchestratorOptions(), Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md")).Build(session, tools);
        var history = new ChatHistory(prompt);
        history.AddUserMessage("一個銀髮少女站在雨夜的霓虹街頭");
        var settings = new GeminiPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: false) };
        var msg = (await Chat().GetChatMessageContentsAsync(history, settings, kernel))[0];
        Assert.NotEmpty(msg.Items.OfType<FunctionCallContent>());
    }

    [IntegrationFact]
    public async Task Embedding_is_768_dim_unit_vector()
    {
        var c = new GeminiEmbeddingClient(new HttpClient(), Options.Create(new EmbeddingOptions()), Options.Create(Llm));
        var v = (await c.EmbedAsync(new[] { "雨夜霓虹街頭" }, GeminiEmbeddingClient.RetrievalQuery, default))[0];
        Assert.Equal(768, v.Length);
        Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), 3);
    }
}
```

```bash
cd src && GEMINI_API_KEY=<key> PC_INTEGRATION=1 dotnet test --filter GeminiContractTests 2>&1 | tail -3
```

Expected: `Passed: 3`。第二個測試同時驗證了 U2／U4／U5（schema、settings、function calling）在你的 connector 版本上真的通。

- [ ] **Step 3: 驗收——跑真的**

```bash
docker compose up -d db
cd src/PromptCopilot.Api && dotnet run
```

另開終端，依序打（每一步看 SSE 輸出）：

```bash
SID=$(curl -s -X POST localhost:5000/api/sessions | jq -r .sessionId)
M() { curl -sN -X POST localhost:5000/api/sessions/$SID/messages -H 'content-type: application/json' -d "{\"text\":\"$1\"}"; echo; }

M "一個銀髮少女站在雨夜的霓虹街頭"      # 期望 final.kind=ask（案例 1/18）
M "寫實跟動漫差在哪？"                  # 期望 final.kind=message，儀表板不動（案例 14）
M "那就寫實。鏡頭低角度"                # 期望 ask 或 finalized
M "穿著隨便"                            # 期望 finalized
M "negative 裡的 blurry 是幹嘛的？"     # 期望 message，沒有新定稿卡（案例 15）
M "把背景改成黃昏"                      # 期望 finalized（案例 11）
M "一個裸體的女生"                      # 期望 blocked Blocked_NSFW（案例 9）
M "少女穿泳裝在海邊"                    # 可能 blocked Blocked_Upstream；再送一次或改寫（案例 23）
M "存起來"                              # 期望 final.kind=save_consent_requested
curl -s -X POST localhost:5000/api/sessions/$SID/save-to-shared -H 'content-type: application/json' \
  -d '{"intent":"一個銀髮少女站在雨夜的霓虹街頭，黃昏"}'
```

然後查 audit：

```bash
docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -c \
  "SELECT turn_index, event_type, prompt_version, latency_ms FROM audit_logs WHERE session_id='$SID' ORDER BY id"
```

期望看到 `Tool_Invoked` 多筆、每輪一筆 `Turn_Completed`、一筆 `Blocked_NSFW`、（若觸發）`Blocked_Upstream`、最後 `Saved_To_Shared`。

- [ ] **Step 4: 把結果填進 eval-cases.md**

至少填 1、9、11、14、15、18、23 這幾條的 `prompt_version`、結果、日期。**沒過的照實寫**，不要改成過。

- [ ] **Step 5: 全部測試最後跑一次**

```bash
cd src && dotnet test 2>&1 | tail -3
```

Expected: 全綠。

- [ ] **Step 6: Commit**

```bash
git add docs/eval-cases.md src/README.md
git commit -m "docs: eval cases and API readme; subproject 2 acceptance run

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## 執行時的已知不確定點

這些是計畫寫的時候查不到、要在執行時對照實際套件版本確認的地方。碰到就修，修完在本節記一筆（格式同子專案 1 計畫的「執行期修正紀錄」）。

| # | 位置 | 不確定的事 | 若不成立怎麼辦 |
| :--- | :--- | :--- | :--- |
| U1 | Task 7 | Google connector 被擋時丟的例外型別與訊息文字；`Metadata` 的 key 名（`FinishReason`、`PromptFeedbackBlockReason`） | 改 `LlmFailureClassifier.ContentBlockMarkers` 與 `Meta()` 的 key；測試不動 |
| U2 | Task 11 | Gemini function declaration 對 `long?`、巢狀陣列 record 的 schema 支援 | 把 `presetId` 改成 `string?`（伺服器再 parse）；把 `asks` 拆成三個平行陣列參數 |
| U3 | Task 14／15 | `FunctionResultContent` 建構子簽名；`FunctionCallContent.InvokeAsync` 對 `JsonElement` 參數的綁定 | 用該版本的公開建構子；綁定失敗改傳 `GetRawText()` 字串 |
| U4 | Task 15 | `GeminiPromptExecutionSettings` 是否需要 `ToolCallBehavior` 而非 `FunctionChoiceBehavior`（舊版 API） | 舊版用 `GeminiToolCallBehavior.EnableKernelFunctions`（不 auto-invoke）；語意相同 |
| U5 | Task 8 | `GeminiPromptExecutionSettings.ResponseSchema` 接受 `Type` 還是要 JSON schema 物件 | 改傳 `KernelJsonSchema` 或手寫 schema；測試用 fake，不受影響 |
| U6 | Task 16 | Gemini 是否接受 history 中途的 system message（純文字補救與強制定稿的提示） | 改用 `AddUserMessage` 加前綴「[系統]」；測試改斷言 `AuthorRole.User` |
| U7 | Task 12 | `AutoFunctionInvocationContext` 的公開建構子簽名（測試手工組 context 用） | 用你版本的建構子；`Arguments`／`Result`／`Terminate` 是可設定屬性，測試其餘不動 |
| U8 | Task 15 | Google connector 的 auto-invoke 對 tool 內部例外是「轉成錯誤結果回給 LLM」還是「往外丟」 | 往外丟的話，在 `AuditFilter`（最外層）包 try/catch，把例外轉成 `context.Result` 的錯誤字串，符合主規格 §4.6 |
| U9 | Task 15 | `Terminate` 之後 `GetChatMessageContentsAsync` 回的是哪一則訊息、SK 有沒有把它加進 history | orchestrator 不依賴回傳值（看 `turn.Outcome`）；`CallAsync` 只在回傳是純文字 assistant 訊息時才手動加進 history，若發現重複就拿掉那一行 |
