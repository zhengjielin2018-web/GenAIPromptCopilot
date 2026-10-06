# 檢索時機 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 讓 RAG 在每個寫入新內容的動手輪、以及要寫出使用者沒講內容的確認輪都檢索，並量出「借來／碰巧對上」，用改前改後的重播實驗判斷有沒有效。

**Architecture:** 先做量測：`PresetLedger` 多一本 `TagTimeline` 記每個 tag 第一次是模型寫的還是片段寫的，`RagSplit` 把定稿的 rag tag 分成借來與碰巧對上；`TurnContext` 記檢索次數與選項數，`Turn_Completed` 的 audit 帶上輪別、檢索計數、`ragSplit`；`adoption_report.py` 加「檢索時機」一節；`manual-tests/replay.py` 照劇本重播。用這套跑一次基準，再改行為：流程說明加兩段檢索規則（placeholder，關掉知識庫時整段消失）、擋只寫維度名稱的查詢、確認輪的檢索結果多留一輪；最後跑改後實驗。

**Tech Stack:** .NET 10 / ASP.NET Core minimal API、Semantic Kernel（Google connector）、xUnit、Python 3.12（`scripts/` 用 venv 與 pytest；`manual-tests/` 只用標準函式庫）、Docker Compose、PostgreSQL + pgvector。

**Spec:** `docs/superpowers/specs/2026-10-06-retrieval-timing-design.md`

## Global Constraints

- **在主目錄開分支 `feat/retrieval-timing`**（從已含 spec 與本計畫的 master），不要用 worktree：worktree 路徑下的 Debug 建置會被 Windows 應用程式控制擋下；實驗要用主目錄的 `.env` 與 compose 專案 `genaipromptcopilot`（容器名固定 `prompt-copilot-*`）。
- 測試指令：C# 一律 Release：`dotnet test src/PromptCopilot.Api.Tests -c Release`（Debug 會被擋）；單一類別加 `--filter "FullyQualifiedName~類別名"`。Python 一律用 `scripts/.venv/Scripts/python.exe`：`scripts/` 的測試在 `scripts/` 底下跑 `./.venv/Scripts/python.exe -m pytest tests/… -q`；`manual-tests/` 的測試在 repo 根目錄跑 `scripts/.venv/Scripts/python.exe -m pytest manual-tests/test_replay.py -q`。
- 註解、文件以繁體中文為主，密度與風格照周圍程式（解釋「為什麼」、引用「檢索時機設計 §x」）。commit 標題英文，照 repo 慣例 `feat(api): …`／`test: …`／`docs: …`。
- 每個 commit 訊息結尾加 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`（執行者若不是 Opus，改成當時 harness 給的那一行）。
- **tag 來源分類（rag／adopted／llm／base）、SSE 事件、前端一律不動**；借來／碰巧對上只進 audit 與報表（設計 §2）。
- 關掉知識庫的對照組（`retrieval: off`）：兩種輪組出來的 system prompt 都不可出現 `SearchPresets`。
- 「借來」的判定：timeline 裡這個 tag（`TagAttribution.Normalize` 後整段相等）第一次出現是片段；其餘 rag 都算碰巧對上（設計 §5.1）。第一次記下的不覆蓋。
- 維度名稱檢查的錯誤字串（測試比對）：`維度 {dimension} 的 query 只寫了維度名稱，請寫具體方向（例：寫實攝影、日系動漫插畫）`。
- 通過標準（設計 §6.3，原樣）：動手輪檢索率（採用除外）≥ 90%；交給模型決定／推薦的確認輪檢索率 ≥ 80%；每次定稿「借來」的 tag 平均 ≥ 2；rag（借來＋碰巧對上）佔非基礎詞 ≥ 50%；延遲中位數增加確認輪、動手輪各 ≤ 3 秒。
- 不動 `scripts/demo.py`、`SearchSimilarPrompts` 的使用時機、`ToolSetBuilder` 的工具清單。

## Review Focus

spec 沒逐條寫、但使用者一定會碰到的情況；每一條都在對應任務加了測試：

1. **還沒題材的確認輪（使用者第一句話）**：這一輪沒有檢索工具，新的確認輪段落卻叫模型檢索；照著叫就是 known-issues #13 的觸發條件。預期：段落明說工具清單裡沒有 `SearchPresets` 就不查。→ Task 7 `Propose_retrieval_rule_says_not_to_search_when_the_tool_is_not_listed`。
2. **知識庫關閉的對照組**：兩種輪都不能出現新段落或 `SearchPresets`，否則模型會去叫沒有的工具。→ Task 7 `Retrieval_off_drops_the_new_paragraphs_in_both_kinds_of_turn`。
3. **已壓過的檢索結果每輪又被掃一次**：預期原樣不動，Gemini 的工具訊息不整則重建（`thoughtSignature` 照帶）。→ Task 9 `Compressing_an_already_compressed_search_result_is_a_no_op`、`Kept_search_results_reach_the_next_request_and_are_compressed_at_its_end`。
4. **舊的 audit 資料（沒有 `kind`／`ragSplit`）與採用輪**：報表不壞、舊資料不混進比率、採用輪不算動手輪。→ Task 4 `test_retrieval_rates_exclude_adoption_and_old_rows`。
5. **含維度名稱的具體查詢（「寫實風格」）與題材專屬名稱（object 的「主體外觀」）**：前者不能誤擋，後者要擋。→ Task 8 `Dimension_item_with_only_the_dimension_name_is_an_item_error`、`Dimension_item_containing_the_name_in_a_concrete_query_still_runs`。

---

## 檔案結構

| 檔案 | 動作 | 責任 |
| :--- | :--- | :--- |
| `src/PromptCopilot.Api/Sessions/TagTimeline.cs` | 新增 | `TagTimeline`（tag 第一次出現的來源）、`RagSplit`／`RagSplitResult`（rag 分借來／碰巧對上） |
| `src/PromptCopilot.Api/Sessions/PresetLedger.cs` | 改 | 擁有 `Timeline`；`Record` 記片段 tag；`Clone` 一起複製 |
| `src/PromptCopilot.Api/Orchestration/TurnContext.cs` | 改 | 本輪檢索與選項計數 |
| `src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs` | 改 | 計數、記模型寫的 facet 項目 tags；Task 8 的維度名稱檢查 |
| `src/PromptCopilot.Api/Plugins/SessionPlugin.cs` | 改 | `Apply` 記模型寫的 tags |
| `src/PromptCopilot.Api/Plugins/DialogPlugin.cs` | 改 | `MarkOffered` 計數、記沒帶 presetId 的選項 tags |
| `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs` | 改 | audit 新欄位；Task 9 的壓縮呼叫 |
| `src/PromptCopilot.Api/Orchestration/HistoryTrimmer.cs` | 改 | `keepSearchResults`、`CompressSearchResultsBefore`、已壓過的不再壓 |
| `src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs` | 改 | 收緊 `RetrievalStepOn`、擴充 `RetrievalRuleOn`、新增 act／propose 兩組 on／off |
| `src/PromptCopilot.Api/Prompts/flow-act.md`、`flow-propose.md`、`system.md` | 改 | 新 placeholder；`Discuss` 那句 |
| `scripts/adoption_report.py` | 改 | `--sessions`、`latency_ms`、「檢索時機」一節 |
| `manual-tests/replay.py`、`replay_scenarios.json`、`test_replay.py` | 新增 | 重播腳本、劇本、純函式測試 |
| `docs/experiments/2026-10-06-retrieval-timing.md` | 新增 | 基準與改後的數字、結論 |
| `docs/eval-cases.md`、`docs/known-issues.md`、`docs/單輪流程說明.md`、`manual-tests/README.md` | 改 | 文件同步 |

---

### Task 1: `TagTimeline` 與 `RagSplit`

**Files:**
- Create: `src/PromptCopilot.Api/Sessions/TagTimeline.cs`
- Modify: `src/PromptCopilot.Api/Sessions/PresetLedger.cs`
- Test: `src/PromptCopilot.Api.Tests/Sessions/TagTimelineTests.cs`（新檔）

**Interfaces:**
- Consumes: `TagAttribution.Split(string?)`、`TagAttribution.Normalize(string)`、`TagAttribution.Rag`、`TagSource`（既有）。
- Produces:
  - `public sealed class TagTimeline { public enum Source { Model, Snippet } ; void SeeModel(string? tags); void SeeSnippet(string? snippet); Source? FirstSeen(string tag); TagTimeline Clone(); }`
  - `public sealed record RagSplitResult(IReadOnlyList<string> Borrowed, IReadOnlyList<string> Echo);`
  - `public static class RagSplit { public static RagSplitResult Classify(IReadOnlyList<TagSource>? sources, TagTimeline timeline); }`
  - `PresetLedger.Timeline`（`public TagTimeline Timeline { get; private set; }`）；`Record` 第一次加入某片段時呼叫 `Timeline.SeeSnippet(seed.PromptSnippet)`；`Clone` 複製 timeline。Session 的 `Snapshot`／`Restore` 本來就 clone ledger，timeline 跟著回滾。

- [ ] **Step 1: 開分支**

```bash
git checkout -b feat/retrieval-timing
```

- [ ] **Step 2: 寫失敗的測試**

新檔 `src/PromptCopilot.Api.Tests/Sessions/TagTimelineTests.cs`：

```csharp
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Tests.Sessions;

/// <summary>檢索時機設計 §5.1：tag 第一次出現的先後，與定稿 rag tag 的借來／碰巧對上。</summary>
public class TagTimelineTests
{
    private static LedgerEntry Entry(long id, string snippet) =>
        new() { Id = id, Title = $"t{id}", PromptSnippet = snippet, FacetIds = new[] { "scene.location" } };

    private static readonly LedgerHit AnyHit = new("scene", 0.2, true);

    [Fact]
    public void First_sighting_wins_and_matching_is_normalized()
    {
        var t = new TagTimeline();
        t.SeeModel("City_Street, (rain:1.2)");
        t.SeeSnippet("city street, rain, night");
        t.SeeModel("night");
        Assert.Equal(TagTimeline.Source.Model, t.FirstSeen("city street"));
        Assert.Equal(TagTimeline.Source.Model, t.FirstSeen("RAIN"));
        Assert.Equal(TagTimeline.Source.Snippet, t.FirstSeen("night"));         // 片段先到；模型後寫不覆蓋
        Assert.Null(t.FirstSeen("lamppost"));
    }

    [Fact]
    public void Blank_and_null_input_records_nothing()
    {
        var t = new TagTimeline();
        t.SeeModel(null); t.SeeModel(" , ,"); t.SeeSnippet("");
        Assert.Null(t.FirstSeen(""));
        Assert.Null(t.FirstSeen(" "));
    }

    [Fact]
    public void Ledger_records_snippet_tags_and_clone_is_independent()
    {
        var l = new PresetLedger();
        l.Record(Entry(4581, "night, cafe, neon lights, streetspace"), AnyHit);
        var c = l.Clone();
        l.Timeline.SeeModel("long coat");
        Assert.Equal(TagTimeline.Source.Snippet, c.Timeline.FirstSeen("streetspace"));
        Assert.Null(c.Timeline.FirstSeen("long coat"));
        Assert.Equal(TagTimeline.Source.Model, l.Timeline.FirstSeen("long coat"));
    }

    [Fact]
    public void Session_restore_rolls_the_timeline_back()
    {
        var s = new Session("s");
        var snap = s.Snapshot();
        s.Ledger.Timeline.SeeModel("hime cut");
        s.Restore(snap);
        Assert.Null(s.Ledger.Timeline.FirstSeen("hime cut"));
    }

    /// <summary>設計 §5.1 的三個例子，加上「模型看過片段之後才寫同一個 tag」仍算借來（Review Focus 的第一次出現不覆蓋）。</summary>
    [Fact]
    public void Rag_tags_split_into_borrowed_and_echo()
    {
        var l = new PresetLedger();
        l.Timeline.SeeModel("city street");                                         // 第一輪 SetFacetStates 先寫
        l.Record(Entry(13876, "city street, night, lamppost"), AnyHit);
        l.Record(Entry(7140, "smile, mature female, long hair, black hair"), AnyHit);
        l.Record(Entry(4581, "night, cafe, neon lights, streetspace"), AnyHit);
        l.Timeline.SeeModel("streetspace, hime cut");                               // 定稿的 facetStates 才寫：片段已先到

        var sources = TagAttribution.Attribute("masterpiece, long black hair, streetspace, city street, hime cut", l, negative: false);
        var split = RagSplit.Classify(sources, l.Timeline);

        Assert.Equal(new[] { "streetspace" }, split.Borrowed);
        Assert.Equal(new[] { "long black hair", "city street" }, split.Echo);       // 只靠字尾對上；模型先寫
    }

    [Fact]
    public void Only_rag_tags_are_split()
    {
        var l = new PresetLedger();
        l.Record(Entry(1, "sandals"), AnyHit);
        var sources = new[]
        {
            new TagSource("sandals", TagAttribution.Adopted, new long[] { 1 }, "t1"),
            new TagSource("masterpiece", TagAttribution.Base, Array.Empty<long>(), null),
            new TagSource("1girl", TagAttribution.Llm, Array.Empty<long>(), null),
        };
        var split = RagSplit.Classify(sources, l.Timeline);
        Assert.Empty(split.Borrowed);
        Assert.Empty(split.Echo);
        Assert.Empty(RagSplit.Classify(null, l.Timeline).Echo);
    }
}
```

- [ ] **Step 3: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~TagTimelineTests"`
Expected: 編譯失敗，`TagTimeline`、`RagSplit`、`PresetLedger.Timeline` 不存在。

- [ ] **Step 4: 實作 `TagTimeline.cs`**

新檔 `src/PromptCopilot.Api/Sessions/TagTimeline.cs`：

```csharp
namespace PromptCopilot.Api.Sessions;

/// <summary>檢索時機設計 §5.1：每個 tag（<see cref="TagAttribution.Normalize"/> 之後）第一次出現時，是模型寫的還是知識庫片段寫的。
/// 第一次記下的就定了，之後不覆蓋。只給報表分「借來／碰巧對上」用，不改定稿 tag 的來源分類。
/// 由 <see cref="PresetLedger"/> 擁有：ledger 進快照（Clone），這本跟著回滾。</summary>
public sealed class TagTimeline
{
    public enum Source { Model, Snippet }

    private readonly Dictionary<string, Source> _first = new();

    /// <summary>模型寫的 tag，逗號分隔：facet 狀態的 tags、檢索 facet 項目的 tags、沒帶 presetId 的選項 tags。</summary>
    public void SeeModel(string? tags) => See(tags, Source.Model);

    /// <summary>片段的 positive，逗號分隔：寫進 ledger 時記。</summary>
    public void SeeSnippet(string? snippet) => See(snippet, Source.Snippet);

    /// <summary>沒出現過回 null。</summary>
    public Source? FirstSeen(string tag) =>
        _first.TryGetValue(TagAttribution.Normalize(tag), out var s) ? s : null;

    private void See(string? text, Source source)
    {
        foreach (var key in TagAttribution.Split(text).Select(TagAttribution.Normalize))
            if (key.Length > 0) _first.TryAdd(key, source);
    }

    public TagTimeline Clone()
    {
        var c = new TagTimeline();
        foreach (var (k, v) in _first) c._first[k] = v;
        return c;
    }
}

/// <summary>定稿 positive 裡 origin 為 rag 的 tag（原文、依 prompt 順序）分成借來與碰巧對上。</summary>
public sealed record RagSplitResult(IReadOnlyList<string> Borrowed, IReadOnlyList<string> Echo);

public static class RagSplit
{
    /// <summary>借來：timeline 裡這個 tag（整段相等）第一次出現是片段。其餘都算碰巧對上：模型先寫的，
    /// 或 timeline 根本沒有、只靠字尾規則對上片段的（<c>long black hair</c> ↔ 片段的 <c>black hair</c>）。</summary>
    public static RagSplitResult Classify(IReadOnlyList<TagSource>? sources, TagTimeline timeline)
    {
        var borrowed = new List<string>();
        var echo = new List<string>();
        foreach (var s in sources ?? Array.Empty<TagSource>())
        {
            if (s.Origin != TagAttribution.Rag) continue;
            (timeline.FirstSeen(s.Tag) == TagTimeline.Source.Snippet ? borrowed : echo).Add(s.Tag);
        }
        return new RagSplitResult(borrowed, echo);
    }
}
```

- [ ] **Step 5: `PresetLedger` 擁有 timeline**

`src/PromptCopilot.Api/Sessions/PresetLedger.cs` 的 `PresetLedger` 類別：

```csharp
public sealed class PresetLedger
{
    private readonly Dictionary<long, LedgerEntry> _entries = new();

    /// <summary>檢索時機設計 §5.1：tag 第一次出現的先後。片段在這裡記；模型寫的由各 plugin 記。</summary>
    public TagTimeline Timeline { get; private set; } = new();

    public void Record(LedgerEntry seed, LedgerHit hit)
    {
        if (!_entries.TryGetValue(seed.Id, out var e))
        {
            e = seed; _entries[seed.Id] = e;
            Timeline.SeeSnippet(seed.PromptSnippet);
        }
        e.Hits.Add(hit);
    }
```

`Clone` 改成：

```csharp
    public PresetLedger Clone()
    {
        var c = new PresetLedger { Timeline = Timeline.Clone() };
        foreach (var (k, v) in _entries) c._entries[k] = v.Clone();
        return c;
    }
```

其餘不動。

- [ ] **Step 6: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~TagTimelineTests|FullyQualifiedName~PresetLedgerTests|FullyQualifiedName~TagAttributionTests|FullyQualifiedName~SessionTests"`
Expected: 全部 PASS。

- [ ] **Step 7: Commit**

```bash
git add src/PromptCopilot.Api/Sessions/TagTimeline.cs src/PromptCopilot.Api/Sessions/PresetLedger.cs src/PromptCopilot.Api.Tests/Sessions/TagTimelineTests.cs
git commit -m "feat(api): tag timeline and borrowed/echo split for rag tags

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: plugin 記帳：檢索次數、選項數、模型寫的 tag

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/TurnContext.cs`
- Modify: `src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs`（`SearchPresetsAsync`）
- Modify: `src/PromptCopilot.Api/Plugins/SessionPlugin.cs`（`Apply`）
- Modify: `src/PromptCopilot.Api/Plugins/DialogPlugin.cs`（`MarkOffered`）
- Test: `src/PromptCopilot.Api.Tests/Plugins/KnowledgePluginTests.cs`、`SessionPluginTests.cs`、`DialogPluginTests.cs`

**Interfaces:**
- Consumes: `PresetLedger.Timeline`、`TagTimeline.SeeModel`（Task 1）。
- Produces（Task 3 讀）：`TurnContext` 上的 `int Searches`、`int SearchItems`、`int SearchItemErrors`、`int OptionsTotal`、`int OptionsWithPreset`（都是 `{ get; set; }`）。
  - `Searches`：每次呼叫 `SearchPresets` 加 1，整次被擋（沒題材、空的、超過 24 項）也算——模型有照流程去查。
  - `SearchItems`：通過整次檢查後加上項目數；`SearchItemErrors`：其中逐項回錯誤的數量（Task 8 的維度名稱檢查也走這裡）。
  - `OptionsTotal`／`OptionsWithPreset`：`AskUser`／`Discuss` 成功時，清洗後攤出去的選項數與帶 presetId 的。

- [ ] **Step 1: 寫失敗的測試**

`KnowledgePluginTests.cs` 檔尾（類別結束前）加：

```csharp
    // ---- 檢索時機（2026-10-06）----

    /// <summary>設計 §5.2：每次呼叫都算一次檢索（整次被擋也算），項目與逐項錯誤分開數。</summary>
    [Fact]
    public async Task Counts_calls_items_and_item_errors_on_the_turn()
    {
        var (p, turn, _, _, _, _) = Make();
        await p.SearchPresetsAsync(Q(("hair", "捲髮"), ("style", "寫實")), default);
        await p.SearchPresetsAsync(Array.Empty<SearchQuery>(), default);           // 整次被擋：只算呼叫
        Assert.Equal(2, turn.Searches);
        Assert.Equal(2, turn.SearchItems);
        Assert.Equal(1, turn.SearchItemErrors);
    }

    /// <summary>設計 §5.1：facet 項目的 tags 是模型在看到命中之前寫的；命中的片段後到。</summary>
    [Fact]
    public async Task Facet_item_tags_are_seen_as_model_before_the_hits_are_recorded()
    {
        var (p, _, s, _, presets, _) = Make();
        presets.FacetPools["appearance.hair"] = 10;
        presets.FacetHits["appearance.hair"] = new[]
        {
            new PresetHit(1, "金短髮", "Appearance", new[] { "appearance.hair" }, "short hair, blonde hair", null, null, 0.18, null),
        };
        await p.SearchPresetsAsync(new[] { F("appearance.hair", "金色短髮", "blonde hair") }, default);
        Assert.Equal(TagTimeline.Source.Model, s.Ledger.Timeline.FirstSeen("blonde hair"));
        Assert.Equal(TagTimeline.Source.Snippet, s.Ledger.Timeline.FirstSeen("short hair"));
    }
```

`SessionPluginTests.cs` 檔尾加：

```csharp
    /// <summary>檢索時機設計 §5.1：facet 狀態帶的 tags 是模型寫的；狀態解析不了的整筆略過。</summary>
    [Fact]
    public void Apply_records_tags_as_written_by_the_model()
    {
        var (p, s, _) = Make();
        p.SetFacetStates(new[]
        {
            new FacetStateEntry("appearance.hair", "covered", Tags: "long black hair, hime cut"),
            new FacetStateEntry("pose.gaze", "bogus", Tags: "looking away"),
        });
        Assert.Equal(TagTimeline.Source.Model, s.Ledger.Timeline.FirstSeen("hime cut"));
        Assert.Null(s.Ledger.Timeline.FirstSeen("looking away"));
    }
```

`DialogPluginTests.cs` 檔尾加：

```csharp
    // ---- 檢索時機（2026-10-06）----

    /// <summary>設計 §5：選項數與帶 presetId 的進本輪計數；沒帶 presetId 的 tags 是模型寫的，帶的不記（片段那邊記過）。</summary>
    [Fact]
    public void AskUser_counts_options_and_records_tags_of_options_without_a_preset()
    {
        var (p, turn, s) = Make();
        var ask = new AskItem("style", "q?", new[] { "style.genre" },
            new[] { new OptionItem("寫實", "photorealistic", 5), new OptionItem("動漫", "anime style", null) });
        Assert.Equal("ok", p.AskUser("hi", new[] { ask }, Array.Empty<FacetStateEntry>()));
        Assert.Equal(2, turn.OptionsTotal);
        Assert.Equal(1, turn.OptionsWithPreset);
        Assert.Equal(TagTimeline.Source.Model, s.Ledger.Timeline.FirstSeen("anime style"));
        Assert.Null(s.Ledger.Timeline.FirstSeen("photorealistic"));
    }

    [Fact]
    public void Discuss_counts_options_too()
    {
        var (p, turn, _) = Make();
        var r = p.Discuss("可以參考這些方向", Array.Empty<FacetStateEntry>(),
            new[] { new OptionItem("雨夜咖啡廳", "night, cafe", 5), new OptionItem("書店", "bookstore", null) });
        Assert.Equal("ok", r);
        Assert.Equal(2, turn.OptionsTotal);
        Assert.Equal(1, turn.OptionsWithPreset);
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~KnowledgePluginTests|FullyQualifiedName~SessionPluginTests|FullyQualifiedName~DialogPluginTests"`
Expected: 編譯失敗，`TurnContext.Searches` 等屬性不存在。

- [ ] **Step 3: `TurnContext` 加計數**

`src/PromptCopilot.Api/Orchestration/TurnContext.cs`，`Rejections` 那行之後加：

```csharp
    /// <summary>檢索時機設計 §5.2：本輪 SearchPresets 呼叫次數（整次被擋也算）、送出的項目數、逐項回錯誤的項目數。</summary>
    public int Searches { get; set; }
    public int SearchItems { get; set; }
    public int SearchItemErrors { get; set; }
    /// <summary>本輪 AskUser／Discuss 清洗後攤出去的選項數，與其中帶 presetId 的。</summary>
    public int OptionsTotal { get; set; }
    public int OptionsWithPreset { get; set; }
```

- [ ] **Step 4: `KnowledgePlugin.SearchPresetsAsync` 計數並記模型寫的 tags**

方法開頭：

```csharp
        var s = turn.Session;
        turn.Searches++;                                                // 檢索時機設計 §5.2：整次被擋也算，模型有照流程去查
        if (s.Profile is null) return "錯誤：請先呼叫 SetProfile";
        if (queries is null || queries.Length == 0) return "錯誤：queries 不可為空，請一次帶上本輪所有要查的項目";
        if (queries.Length > MaxQueries) return $"錯誤：queries 最多 {MaxQueries} 個項目（使用者講到的每個 facet 各一項，加上沒講的維度各兩項），收到 {queries.Length} 個";
        turn.SearchItems += queries.Length;
```

驗證迴圈裡 `valid.Add(...)` 那行之後加：

```csharp
            // 模型送進來的翻譯，在看到任何命中之前記（檢索時機設計 §5.1）；維度項目沒有 tags
            s.Ledger.Timeline.SeeModel(valid[^1].tags);
```

驗證迴圈結束後（`var vectors = …` 之前）加：

```csharp
        turn.SearchItemErrors += errors.Count;
```

- [ ] **Step 5: `SessionPlugin.Apply` 記 tags**

把

```csharp
            if (!string.IsNullOrWhiteSpace(u.Tags)) tags[u.FacetId] = u.Tags!;
```

改成

```csharp
            if (!string.IsNullOrWhiteSpace(u.Tags))
            {
                tags[u.FacetId] = u.Tags!;
                turn.Session.Ledger.Timeline.SeeModel(u.Tags);           // 檢索時機設計 §5.1：模型寫的 tag
            }
```

（`Apply` 也被 `AskUser`／`FinalizePrompt` 的 `facetStates` 走到；第一次出現的不覆蓋，所以定稿時才寫、片段早就有的 tag 仍算片段。）

- [ ] **Step 6: `DialogPlugin.MarkOffered` 計數並記沒帶 presetId 的 tags**

整個方法換成：

```csharp
    /// <summary>攤出去的選項：帶 presetId 的記進 ledger 的 offered；沒帶的 tags 是模型自己寫的，記進 timeline（檢索時機設計 §5.1）。
    /// 順便數本輪的選項數與帶 presetId 的（§5.2）。清洗時 presetId 不在 ledger 而被降級的，這裡已經是 null。</summary>
    private void MarkOffered(IEnumerable<(string? dimension, OptionItem option)> offered)
    {
        foreach (var (dim, o) in offered)
        {
            turn.OptionsTotal++;
            if (o.PresetId is { } id)
            {
                turn.OptionsWithPreset++;
                S.Ledger.MarkOffered(id, new OfferedRef(turn.TurnIndex, dim, o.Label));
            }
            else S.Ledger.Timeline.SeeModel(o.Tags);
        }
    }
```

- [ ] **Step 7: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~KnowledgePluginTests|FullyQualifiedName~SessionPluginTests|FullyQualifiedName~DialogPluginTests"`
Expected: 全部 PASS。

- [ ] **Step 8: Commit**

```bash
git add src/PromptCopilot.Api/Orchestration/TurnContext.cs src/PromptCopilot.Api/Plugins src/PromptCopilot.Api.Tests/Plugins
git commit -m "feat(api): count searches and options per turn; record model-written tags

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `Turn_Completed` 的 audit 新欄位

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`（`Turn_Completed` 的 `Payload(...)`）
- Test: `src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs`

**Interfaces:**
- Consumes: `TurnContext.Searches／SearchItems／SearchItemErrors／OptionsTotal／OptionsWithPreset`（Task 2）、`RagSplit.Classify`、`PresetLedger.Timeline`（Task 1）。
- Produces（Task 4 的報表讀）：`Turn_Completed` payload 多這些欄位（null 的照 `Fields` 規則不寫）：
  - `kind`：`"propose"`／`"act"`／`"adopt"`（有 `input.Adoption` 是 adopt）
  - `searches`、`searchItems`、`searchItemErrors`：整數，一律寫
  - `autoComplete`：確認輪且輸入分類器判定隨便時為 `true`，其他輪不寫
  - `options`：結局是 `AskOutcome` 或 `MessageOutcome` 時 `{"total":n,"withPreset":m}`
  - `ragSplit`：結局是 `FinalizedOutcome` 時 `{"borrowed":[…],"echo":[…]}`（tag 原文）

- [ ] **Step 1: 寫失敗的測試**

`AgenticOrchestratorTests.cs` 檔尾（類別結束前）加：

```csharp
    // ---- 檢索時機（2026-10-06）----

    /// <summary>設計 §5.2：每輪記輪別與檢索計數；確認輪說隨便時記 autoComplete。</summary>
    [Fact]
    public async Task Turn_completed_records_kind_search_counts_and_auto_complete()
    {
        var h = new Harness();
        h.GuardChat.Then(FakeChatCompletion.Text("""{"nsfw":false,"realPerson":false,"personName":null,"wantsAutoComplete":true,"reason":"ok"}"""));
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "我會直接定稿，風格補成寫實攝影。" }) });
        await h.RunAsync("隨便，直接給我");

        h.Chat.ThenAsync(async (hist, k) =>
        {
            var t = k!.Turn(); t.Searches = 1; t.SearchItems = 3; t.SearchItemErrors = 1;   // KnowledgePlugin 會累加；harness 沒掛它，直接寫
            await Invoke(hist, k, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k, "Dialog", "FinalizePrompt", FinalizeArgs()) };
        });
        await h.RunAsync(new TurnInput(ConfirmValidator.AcceptText, Confirmed: new ConfirmedInput(h.Session.PendingConfirmation!, null)));

        var completed = h.Audit.Entries.Where(a => a.EventType == "Turn_Completed").Select(a => a.PayloadJson!).ToList();
        Assert.Equal(2, completed.Count);
        Assert.Contains("\"kind\":\"propose\"", completed[0]);
        Assert.Contains("\"searches\":0", completed[0]);
        Assert.Contains("\"autoComplete\":true", completed[0]);
        Assert.Contains("\"kind\":\"act\"", completed[1]);
        Assert.Contains("\"searches\":1", completed[1]);
        Assert.Contains("\"searchItems\":3", completed[1]);
        Assert.Contains("\"searchItemErrors\":1", completed[1]);
        Assert.DoesNotContain("autoComplete", completed[1]);                        // 只有確認輪記
    }

    /// <summary>設計 §5.2：追問卡記選項數與帶 presetId 的；不是定稿就沒有 ragSplit。</summary>
    [Fact]
    public async Task Ask_turn_records_option_counts()
    {
        var h = new Harness();
        h.Chat.ThenAsync(async (hist, k) =>
        {
            await Invoke(hist, k!, "Session", "SetProfile", new { profile = "portrait" });
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        await h.ActAsync();
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("\"options\":{\"total\":2,\"withPreset\":0}", completed.PayloadJson!);
        Assert.DoesNotContain("ragSplit", completed.PayloadJson!);
    }

    /// <summary>設計 §5.1：定稿記 rag 的借來／碰巧對上（tag 原文）。</summary>
    [Fact]
    public async Task Finalized_turn_records_rag_split()
    {
        var h = new Harness();
        MakeFinalized(h.Session);
        h.Session.Ledger.Timeline.SeeModel("cafe");                                  // 模型先寫
        h.Session.Ledger.Record(new LedgerEntry { Id = 4581, Title = "夜間咖啡廳", PromptSnippet = "night, cafe, neon lights, streetspace", FacetIds = new[] { "scene.location" } },
            new LedgerHit("scene", 0.2, true));
        h.Chat.ThenAsync(async (hist, k) => new[] { await Invoke(hist, k!, "Dialog", "FinalizePrompt", new
        {
            positivePrompt = "masterpiece, cafe, streetspace, long coat", negativePrompt = "lowres", tips = "t", intentSummary = "雨夜咖啡廳前的女士",
            facetStates = Array.Empty<object>(),
        }) });
        await h.ActAsync("我會把背景改成深夜咖啡廳前。");
        var completed = Assert.Single(h.Audit.Entries, a => a.EventType == "Turn_Completed");
        Assert.Contains("\"ragSplit\":{\"borrowed\":[\"streetspace\"],\"echo\":[\"cafe\"]}", completed.PayloadJson!);
        Assert.Contains("\"kind\":\"act\"", completed.PayloadJson!);
    }
```

既有的 `Adoption_turn_records_adoption_marks_ledger_and_audits` 最後加兩行（採用寫進 ledger 時 `sandals` 已以片段身分出現）：

```csharp
        Assert.Contains("\"kind\":\"adopt\"", completed.PayloadJson!);
        Assert.Contains("\"ragSplit\":{\"borrowed\":[\"sandals\"],\"echo\":[]}", completed.PayloadJson!);
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~AgenticOrchestratorTests"`
Expected: 新的三個測試與採用那個 FAIL（payload 裡沒有 `kind` 等欄位）。

- [ ] **Step 3: 加欄位**

`AgenticOrchestrator.ExecuteTurnAsync` 寫 `Turn_Completed` 的 `Payload(...)` 裡，`("tagOrigins", …)` 那行之後加：

```csharp
                    // 檢索時機設計 §5.2：輪別與檢索計數；報表靠它們算檢索率與延遲
                    ("kind", input.Adoption is not null ? "adopt" : kind == TurnKind.Act ? "act" : "propose"),
                    ("searches", turn.Searches), ("searchItems", turn.SearchItems), ("searchItemErrors", turn.SearchItemErrors),
                    ("autoComplete", kind == TurnKind.Propose && g.WantsAutoComplete ? true : null),
                    ("options", turn.Outcome is AskOutcome or MessageOutcome ? (object)new { total = turn.OptionsTotal, withPreset = turn.OptionsWithPreset } : null),
                    ("ragSplit", turn.Outcome is FinalizedOutcome split ? (object)RagSplitPayload(RagSplit.Classify(split.Final.PositiveSources, session.Ledger.Timeline)) : null),
```

類別裡（`TagOrigins` 方法旁邊）加：

```csharp
    /// <summary>檢索時機設計 §5.1：rag 的借來／碰巧對上，tag 原文；只進 audit，不改來源分類。</summary>
    private static object RagSplitPayload(RagSplitResult r) => new { borrowed = r.Borrowed, echo = r.Echo };
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~AgenticOrchestratorTests"`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs src/PromptCopilot.Api.Tests/Orchestration/AgenticOrchestratorTests.cs
git commit -m "feat(api): audit turn kind, search counts, option counts and rag split

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: 報表「檢索時機」一節

**Files:**
- Modify: `scripts/adoption_report.py`
- Test: `scripts/tests/test_adoption_report.py`

**Interfaces:**
- Consumes: Task 3 的 payload 欄位；`audit_logs.latency_ms`。
- Produces: `Turn` 多 `latency_ms: int | None = None`（最後一個欄位，有預設值，舊測試照舊能建）；`fetch_turns(conn, since, sessions=None)`；`parse_sessions(text: str | None) -> list[str] | None`；`build_retrieval_section(completed: list[Turn]) -> list[str]`；CLI 多 `--sessions`。

- [ ] **Step 1: 寫失敗的測試**

`scripts/tests/test_adoption_report.py` 第一行的 import 改成：

```python
from adoption_report import Turn, build_report, build_retrieval_section, parse_sessions
```

檔尾加：

```python
# ---- 檢索時機（2026-10-06 設計 §5.3）----


def done(session, turn, kind, outcome, searches=0, latency=None, **extra):
    return Turn(session, turn, {"kind": kind, "outcome": outcome, "searches": searches, **extra}, latency_ms=latency)


def test_retrieval_rates_exclude_adoption_and_old_rows():
    turns = [
        done("a", 1, "propose", "ConfirmOutcome", latency=3000),
        done("a", 2, "act", "AskOutcome", searches=1, latency=8000, options={"total": 4, "withPreset": 3}),
        done("a", 3, "propose", "ConfirmOutcome", searches=1, autoComplete=True, latency=5000),
        done("a", 4, "act", "FinalizedOutcome", latency=6000,
             tagOrigins={"rag": 3, "adopted": 0, "llm": 4, "base": 3},
             ragSplit={"borrowed": ["streetspace", "night"], "echo": ["cafe"]}),
        done("a", 5, "adopt", "FinalizedOutcome", latency=6500,
             tagOrigins={"rag": 0, "adopted": 2, "llm": 1, "base": 3}, ragSplit={"borrowed": [], "echo": []}),
        Turn("old", 1, {"outcome": "FinalizedOutcome", "tagOrigins": {"rag": 9, "adopted": 0, "llm": 0, "base": 3}}),
    ]
    text = "\n".join(build_retrieval_section(turns))
    assert "動手輪檢索率（不含採用）：1/2（50.0%）" in text
    assert "「隨便」確認輪檢索率：1/1（100.0%）" in text
    assert "帶參考方向的 Discuss 輪檢索率：0/0（—）" in text
    assert "選項帶 presetId：3/4（75.0%）" in text
    assert "每次定稿平均（2 次）：借來 1.0、碰巧對上 0.5、llm 2.5、base 3.0、adopted 1.0" in text
    assert "rag（借來＋碰巧對上）佔非基礎詞：3/10（30.0%）" in text
    assert "確認輪 有檢索 5000 ms（1 輪）／沒檢索 3000 ms（1 輪）；動手輪 有檢索 8000 ms（1 輪）／沒檢索 6000 ms（1 輪）" in text
    assert "沒有 kind 欄位的舊資料：1 輪" in text


def test_discuss_with_options_counts_toward_its_rate_and_plain_replies_do_not():
    turns = [
        done("a", 1, "propose", "MessageOutcome", searches=1, options={"total": 3, "withPreset": 3}),
        done("a", 2, "propose", "MessageOutcome", options={"total": 0, "withPreset": 0}),
    ]
    assert "帶參考方向的 Discuss 輪檢索率：1/1（100.0%）" in "\n".join(build_retrieval_section(turns))


def test_no_new_rows_says_so():
    assert build_retrieval_section([Turn("old", 1, {"outcome": "AskOutcome"})]) == [
        "", "## 檢索時機", "", "尚無檢索時機資料（2026-10-06 之後的紀錄才有）。"]


def test_report_includes_the_retrieval_section():
    assert "## 檢索時機" in build_report([done("a", 1, "act", "AskOutcome", searches=1)])


def test_parse_sessions():
    assert parse_sessions(None) is None
    assert parse_sessions("") is None
    assert parse_sessions("a, b,,") == ["a", "b"]
```

- [ ] **Step 2: 跑測試確認失敗**

Run（在 `scripts/` 底下）：`./.venv/Scripts/python.exe -m pytest tests/test_adoption_report.py -q`
Expected: ImportError（`build_retrieval_section`、`parse_sessions` 不存在）。

- [ ] **Step 3: 實作**

`scripts/adoption_report.py`：

1. 模組 docstring 最後加一段：

```
檢索時機（2026-10-06 設計 2026-10-06-retrieval-timing-design.md §5.3）：動手輪（不含採用）、「隨便」確認輪、帶參考方向的
Discuss 輪的檢索率；選項帶 presetId 的比例；每次定稿的借來／碰巧對上／llm／base／adopted；兩種輪有無檢索的延遲中位數。
只算帶 kind 欄位的新資料。重播腳本跑出來的 session 用 --sessions 篩：

    python adoption_report.py --sessions id1,id2,id3
```

2. import 區加 `import statistics`。

3. `SQL` 換成：

```python
SQL = """
SELECT session_id, turn_index, payload, event_type, latency_ms
FROM audit_logs
WHERE event_type IN ('Turn_Completed', 'Recommendations_Next') AND session_id IS NOT NULL AND payload IS NOT NULL
  AND (%(since)s::date IS NULL OR created_at >= %(since)s::date)
  AND (%(sessions)s::text[] IS NULL OR session_id = ANY(%(sessions)s::text[]))
ORDER BY session_id, turn_index, id
"""
```

4. `Turn` 加最後一個欄位，`fetch_turns` 帶 sessions：

```python
@dataclass
class Turn:
    session_id: str
    turn_index: int
    payload: dict
    event_type: str = "Turn_Completed"
    latency_ms: int | None = None


def fetch_turns(conn, since: date | None, sessions: list[str] | None = None) -> list[Turn]:
    rows = conn.execute(SQL, {"since": since, "sessions": sessions}).fetchall()
    return [Turn(r[0], r[1], r[2] if isinstance(r[2], dict) else json.loads(r[2]), r[3], r[4]) for r in rows]


def parse_sessions(text: str | None) -> list[str] | None:
    """--sessions 的值：逗號分隔，去空白、去空項；什麼都沒有回 None（不篩）。"""
    ids = [s.strip() for s in (text or "").split(",") if s.strip()]
    return ids or None
```

5. `build_report` 前面加：

```python
def build_retrieval_section(completed: list[Turn]) -> list[str]:
    """檢索時機（設計 §5.3）。只算帶 kind 的新資料；舊資料只列筆數。採用輪（kind=adopt）不算動手輪，但算進定稿平均。"""
    lines = ["", "## 檢索時機", ""]
    new = [t for t in completed if "kind" in t.payload]
    if not new:
        return lines + ["尚無檢索時機資料（2026-10-06 之後的紀錄才有）。"]

    def searched(t: Turn) -> bool:
        return (t.payload.get("searches") or 0) > 0

    def rate(ts: list[Turn]) -> str:
        return _pct(sum(searched(t) for t in ts), len(ts))

    act = [t for t in new if t.payload["kind"] == "act"]
    auto = [t for t in new if t.payload["kind"] == "propose" and t.payload.get("autoComplete")]
    discuss = [t for t in new if t.payload.get("outcome") == "MessageOutcome"
               and (t.payload.get("options") or {}).get("total", 0) > 0]
    opts = [t.payload["options"] for t in new if isinstance(t.payload.get("options"), dict)]
    lines.append(f"- 動手輪檢索率（不含採用）：{rate(act)}")
    lines.append(f"- 「隨便」確認輪檢索率：{rate(auto)}")
    lines.append(f"- 帶參考方向的 Discuss 輪檢索率：{rate(discuss)}")
    lines.append(f"- 選項帶 presetId：{_pct(sum(o.get('withPreset', 0) for o in opts), sum(o.get('total', 0) for o in opts))}")

    finals = [t.payload for t in new
              if isinstance(t.payload.get("ragSplit"), dict) and isinstance(t.payload.get("tagOrigins"), dict)]
    if finals:
        n = len(finals)
        borrowed = sum(len(p["ragSplit"].get("borrowed", [])) for p in finals)
        echo = sum(len(p["ragSplit"].get("echo", [])) for p in finals)

        def avg(key: str) -> float:
            return sum(p["tagOrigins"].get(key, 0) for p in finals) / n

        nonbase = sum(sum(p["tagOrigins"].values()) - p["tagOrigins"].get("base", 0) for p in finals)
        lines.append(f"- 每次定稿平均（{n} 次）：借來 {borrowed / n:.1f}、碰巧對上 {echo / n:.1f}、"
                     f"llm {avg('llm'):.1f}、base {avg('base'):.1f}、adopted {avg('adopted'):.1f}")
        lines.append(f"- rag（借來＋碰巧對上）佔非基礎詞：{_pct(borrowed + echo, nonbase)}")
    else:
        lines.append("- 尚無定稿")

    def med(kind: str, with_search: bool) -> str:
        xs = [t.latency_ms for t in new
              if t.payload["kind"] == kind and searched(t) == with_search and t.latency_ms is not None]
        return f"{statistics.median(xs):.0f} ms（{len(xs)} 輪）" if xs else "—（0 輪）"

    lines.append(f"- 延遲中位數：確認輪 有檢索 {med('propose', True)}／沒檢索 {med('propose', False)}；"
                 f"動手輪 有檢索 {med('act', True)}／沒檢索 {med('act', False)}")
    old = len(completed) - len(new)
    if old:
        lines.append(f"- 沒有 kind 欄位的舊資料：{old} 輪，不計入上面各項")
    return lines
```

6. `build_report` 裡 `slate = build_slate_section(completed, nexts)` 下一行加 `retrieval = build_retrieval_section(completed)`，兩個 `return "\n".join(lines + slate)` 都改成 `return "\n".join(lines + slate + retrieval)`。

7. `main` 加參數並傳下去：

```python
    ap.add_argument("--sessions", default=None,
                    help="只算這些 session（逗號分隔）；manual-tests/replay.py 最後一行會印")
    args = ap.parse_args(argv)
    from pipeline.db import connect

    with connect() as conn:
        turns = fetch_turns(conn, args.since, parse_sessions(args.sessions))
```

- [ ] **Step 4: 跑測試確認通過**

Run（在 `scripts/` 底下）：`./.venv/Scripts/python.exe -m pytest tests/test_adoption_report.py -q`
Expected: 全部 PASS（舊的 18 個加新的 5 個）。

- [ ] **Step 5: 對開發庫實跑一次，確認 SQL 能跑**

Run（在 `scripts/` 底下）：`./.venv/Scripts/python.exe adoption_report.py --sessions ddaf211568b84438a6f84f18327e1cb6`
Expected: 印出報表；「檢索時機」一節是「尚無檢索時機資料（2026-10-06 之後的紀錄才有）。」（那個 session 是舊資料）。不可有 SQL 錯誤。

- [ ] **Step 6: Commit**

```bash
git add scripts/adoption_report.py scripts/tests/test_adoption_report.py
git commit -m "feat(scripts): retrieval timing section and --sessions filter in adoption report

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: 重播腳本與劇本

**Files:**
- Create: `manual-tests/replay.py`、`manual-tests/replay_scenarios.json`、`manual-tests/test_replay.py`
- Modify: `manual-tests/README.md`、`docs/eval-cases.md`

**Interfaces:**
- Consumes: `manual-tests/chat.py` 的 `http_json(method, url, body=None, timeout=30)`、`sse_events(url, body, timeout=180)`；API：`POST /api/sessions` → `{"sessionId"}`；`GET /api/config/facets` → `dimensions[{key,label}]`；`POST /api/sessions/{id}/messages` 收 `{"text"}` 或 `{"confirm":{"turnIndex","choice"}}`，SSE 事件 `session`（`turnIndex`）、`tool_call`（`name`）、`final`（`kind`、`choices`、`asks`、`positive`）、`blocked`、`error`。
- Produces: `python manual-tests/replay.py --scenario Q1|Q2|Q3|all --runs N [--base URL]`，每次一張 Markdown 表與最後定稿，最後一行 `sessions: id1,id2,…`（Task 6、Task 10 交給報表）。

- [ ] **Step 1: 寫失敗的測試**

新檔 `manual-tests/test_replay.py`：

```python
"""replay.py 的純函式（不打 API）。從 repo 根目錄跑：scripts/.venv/Scripts/python.exe -m pytest manual-tests/test_replay.py -q"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from replay import TurnResult, compose_pick, judge  # noqa: E402

LABELS = {"style": "風格", "pose": "人物動作"}


def test_compose_pick_takes_the_first_option_of_each_ask_and_appends_extra():
    asks = [{"dimension": "style", "options": [{"label": "寫實攝影"}, {"label": "動漫"}]},
            {"dimension": "pose", "options": [{"label": "漫步雨中"}]}]
    assert compose_pick(asks, LABELS, "衣服你幫我設計") == "[風格] 寫實攝影\n[人物動作] 漫步雨中\n衣服你幫我設計"


def test_compose_pick_without_asks_sends_only_extra_or_skips():
    assert compose_pick([], LABELS, "衣服你幫我設計") == "衣服你幫我設計"
    assert compose_pick([], LABELS, None) is None


def test_judge():
    searched, idle = TurnResult("propose", searched=True), TurnResult("act")
    assert judge("act", idle, TurnResult("act", searched=True)) == "OK"
    assert judge("act", searched, idle) == "MISS"
    assert judge("act", TurnResult("propose", outcome="message", searched=True), None) == "NO-TURN"
    assert judge("both", searched, TurnResult("act", searched=True)) == "OK"
    assert judge("both", idle, TurnResult("act", searched=True)) == "MISS"
    assert judge("propose", searched, None) == "OK"
    assert judge("none", idle, None) == "OK"
```

- [ ] **Step 2: 跑測試確認失敗**

Run（repo 根目錄）：`scripts/.venv/Scripts/python.exe -m pytest manual-tests/test_replay.py -q`
Expected: ModuleNotFoundError（`replay` 不存在）。

- [ ] **Step 3: 寫劇本**

新檔 `manual-tests/replay_scenarios.json`（`expect`：該步哪一輪應該檢索——`propose`／`act`／`both`／`none`）：

```json
{
  "Q1": [
    {"say": "一位金色短髮的中年女士站在雨夜的霓虹街頭", "expect": "act"},
    {"pick": true, "expect": "act"},
    {"pick": true, "extra": "衣服你幫我設計", "expect": "both"},
    {"say": "家居感的衣褲", "expect": "act"},
    {"say": "你推薦一些場景設計讓我參考", "expect": "propose"},
    {"say": "深夜咖啡廳前（帶溫暖燈光與招牌）", "expect": "act"},
    {"say": "改成黑長直髮的上班族女士", "expect": "act"},
    {"say": "穿回家居服，頭髮要齊劉海，年齡是年輕上班族", "expect": "act"}
  ],
  "Q2": [
    {"say": "一隻在森林裡的狐狸", "expect": "act"},
    {"say": "其他隨便，你決定", "expect": "both"}
  ],
  "Q3": [
    {"say": "穿和服的少女在神社前", "expect": "act"},
    {"pick": true, "expect": "act"},
    {"pick": true, "expect": "act"},
    {"say": "讓她更有氣質", "expect": "both"}
  ]
}
```

- [ ] **Step 4: 寫腳本**

新檔 `manual-tests/replay.py`：

```python
"""檢索時機驗收的重播腳本（docs/superpowers/specs/2026-10-06-retrieval-timing-design.md §6.2）：
照劇本送話、自動按確認卡（有解讀選項就選第一個），記下每一步的確認輪與動手輪有沒有叫 SearchPresets。

只用標準函式庫，沿用 chat.py 的 HTTP／SSE 函式。API 要先在跑（docker compose 的 api 容器，預設 http://localhost:5000）。
用法：python manual-tests/replay.py --scenario Q1 --runs 3
      python manual-tests/replay.py --scenario all --runs 3
劇本在 replay_scenarios.json：say 是直接送的話；pick 照前端的格式用最近一張追問卡每個維度的第一個選項回答，可附 extra。
最後一行印出這次跑出來的 session id（逗號分隔），交給 scripts/adoption_report.py --sessions 算報表。
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from chat import http_json, sse_events  # noqa: E402

SCENARIOS = Path(__file__).resolve().parent / "replay_scenarios.json"


@dataclass
class TurnResult:
    kind: str                     # propose | act
    outcome: str | None = None    # final 的 kind（confirm／ask／message／finalized）；被攔、出錯是 blocked／error／http 4xx
    searched: bool = False
    ms: int = 0
    turn_index: int | None = None
    choices: list[str] = field(default_factory=list)
    asks: list[dict] = field(default_factory=list)
    positive: str | None = None


def compose_pick(asks: list[dict], dim_labels: dict[str, str], extra: str | None) -> str | None:
    """照前端 composer 的格式回答追問卡：每個維度一行「[維度] 第一個選項」，再接 extra。都沒有就回 None（這步跳過）。"""
    lines = [f"[{dim_labels.get(a['dimension'], a['dimension'])}] {a['options'][0]['label']}"
             for a in asks if a.get("options")]
    if extra:
        lines.append(extra)
    return "\n".join(lines) or None


def judge(expect: str, propose: TurnResult | None, act: TurnResult | None) -> str:
    """expect：propose／act／both／none。要求的那一輪沒發生記 NO-TURN；發生了但沒檢索記 MISS。"""
    need = {"propose": ["propose"], "act": ["act"], "both": ["propose", "act"], "none": []}[expect]
    got = {"propose": propose, "act": act}
    for k in need:
        if got[k] is None:
            return "NO-TURN"
        if not got[k].searched:
            return "MISS"
    return "OK"


def run_turn(base: str, sid: str, body: dict, kind: str) -> TurnResult:
    r = TurnResult(kind)
    start = time.monotonic()
    try:
        for name, ev in sse_events(f"{base}/api/sessions/{sid}/messages", body):
            if name == "session":
                r.turn_index = ev["turnIndex"]
            elif name == "tool_call" and ev.get("name") == "SearchPresets":
                r.searched = True
            elif name == "final":
                r.outcome = ev["kind"]
                r.choices = ev.get("choices") or []
                r.asks = ev.get("asks") or []
                r.positive = ev.get("positive")
            elif name in ("blocked", "error"):
                r.outcome = name
    except urllib.error.HTTPError as e:
        r.outcome = f"http {e.code}"
    r.ms = int((time.monotonic() - start) * 1000)
    return r


def run_scenario(base: str, steps: list[dict], dim_labels: dict[str, str]) -> tuple[str, list[dict], str | None]:
    _, body = http_json("POST", f"{base}/api/sessions")
    sid = body["sessionId"]
    asks: list[dict] = []
    rows: list[dict] = []
    last_positive: str | None = None
    for i, step in enumerate(steps, 1):
        text = step.get("say") or (compose_pick(asks, dim_labels, step.get("extra")) if step.get("pick") else None)
        if text is None:
            rows.append({"step": i, "text": "（跳過：沒有追問卡）", "propose": None, "act": None, "verdict": "SKIP"})
            continue
        propose = run_turn(base, sid, {"text": text}, "propose")
        act = None
        if propose.outcome == "confirm":
            choice = 0 if propose.choices else None
            act = run_turn(base, sid, {"confirm": {"turnIndex": propose.turn_index, "choice": choice}}, "act")
            asks = act.asks if act.outcome == "ask" else []        # 確認輪結束在 Discuss 時追問卡還在，不清
            if act.outcome == "finalized":
                last_positive = act.positive
        rows.append({"step": i, "text": text.replace("\n", " / "), "propose": propose, "act": act,
                     "verdict": judge(step.get("expect", "none"), propose, act)})
    return sid, rows, last_positive


def fmt(r: TurnResult | None) -> str:
    if r is None:
        return "—"
    return f"{r.outcome or '?'}{'（查）' if r.searched else ''} {r.ms / 1000:.1f}s"


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", default="http://localhost:5000")
    ap.add_argument("--scenario", default="all", help="Q1／Q2／Q3 或 all")
    ap.add_argument("--runs", type=int, default=1)
    args = ap.parse_args(argv)
    base = args.base.rstrip("/")
    scenarios = json.loads(SCENARIOS.read_text(encoding="utf-8"))
    names = list(scenarios) if args.scenario == "all" else [args.scenario]
    _, cfg = http_json("GET", f"{base}/api/config/facets")
    dim_labels = {d["key"]: d["label"] for d in cfg["dimensions"]}
    sessions: list[str] = []
    for run in range(1, args.runs + 1):
        for name in names:
            sid, rows, positive = run_scenario(base, scenarios[name], dim_labels)
            sessions.append(sid)
            print(f"\n## {name} 第 {run} 次（session {sid}）\n")
            print("| 步 | 送出 | 確認輪 | 動手輪 | 檢索判定 |")
            print("| :--- | :--- | :--- | :--- | :--- |")
            for row in rows:
                print(f"| {row['step']} | {row['text'][:40]} | {fmt(row['propose'])} | {fmt(row['act'])} | {row['verdict']} |")
            print(f"\n最後定稿 positive：{positive or '（沒有定稿）'}")
            sys.stdout.flush()
    print("\nsessions: " + ",".join(sessions))
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 5: 跑測試確認通過**

Run（repo 根目錄）：`scripts/.venv/Scripts/python.exe -m pytest manual-tests/test_replay.py -q`
Expected: 3 passed。

- [ ] **Step 6: 文件**

`manual-tests/README.md` 的檔案表加一列：

```
| `replay.py` | 檢索時機驗收的重播腳本：照 `replay_scenarios.json` 的劇本送話、自動按確認，印出每步有沒有檢索 |
```

並在第 2 節（對話試用）之後加一節：

````markdown
## 2.5 重播檢索時機劇本

```bash
python manual-tests/replay.py --scenario all --runs 3     # Q1～Q3 各跑 3 次；--scenario Q1 只跑一個
```

每跑一次印一張表：每一步的確認輪、動手輪結局，有叫 `SearchPresets` 的標「（查）」，最後一欄是對照劇本預期的判定
（`OK`／`MISS` 沒查／`NO-TURN` 預期要查的那一輪沒發生／`SKIP` 沒有追問卡可選）。最後一行是 session id，
交給報表：`cd scripts && ./.venv/Scripts/python.exe adoption_report.py --sessions <那一行>`。
劇本與預期見 `docs/eval-cases.md` 的「2026-10-06 檢索時機」。
````

`docs/eval-cases.md` 檔尾加：

```markdown
## 2026-10-06 檢索時機（Q1–Q3）

設計見 [檢索時機設計](superpowers/specs/2026-10-06-retrieval-timing-design.md) §6。用 `manual-tests/replay.py` 照劇本跑（`manual-tests/replay_scenarios.json`），改前、改後各 3 次；看每步的「檢索判定」與 `scripts/adoption_report.py --sessions …` 的「檢索時機」一節。

| 編號 | 劇本 | 預期 |
| :--- | :--- | :--- |
| Q1 | 2026-10-05 session `ddaf2115` 的原話：第一句描述 → 追問選第一個 → 追問選第一個＋「衣服你幫我設計」→「家居感的衣褲」→「你推薦一些場景設計讓我參考」→「深夜咖啡廳前（帶溫暖燈光與招牌）」→「改成黑長直髮的上班族女士」→「穿回家居服，頭髮要齊劉海，年齡是年輕上班族」 | 每個動手輪都檢索；「衣服你幫我設計」與「推薦」的確認輪也檢索；定稿不再出現 `comfortable lounge wear top` 這類知識庫沒有、不像 SD tag 的寫法 |
| Q2 | 「一隻在森林裡的狐狸」→「其他隨便，你決定」 | 「隨便」的確認輪先檢索，卡上列的內容來自片段；動手輪檢索並補齊 |
| Q3 | 「穿和服的少女在神社前」→ 追問選第一個 ×2 →「讓她更有氣質」 | 模糊要求的確認輪先檢索再給解讀；動手輪檢索 |

結果：基準見 [實驗紀錄](experiments/2026-10-06-retrieval-timing.md) §3，改後見 §4。
```

- [ ] **Step 7: Commit**

```bash
git add manual-tests/replay.py manual-tests/replay_scenarios.json manual-tests/test_replay.py manual-tests/README.md docs/eval-cases.md
git commit -m "feat(manual-tests): replay script and Q1-Q3 scenarios for retrieval timing

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: 跑基準（流程說明還沒改）

這一步不改程式，只跑實驗、寫紀錄。要用 Gemini 額度（約 9 個 session、150 輪）。

**Files:**
- Create: `docs/experiments/2026-10-06-retrieval-timing.md`

**Interfaces:**
- Consumes: Task 1–5 的程式（audit 帶新欄位）、`replay.py`、`adoption_report.py --sessions`。
- Produces: 實驗紀錄 §1–§3（Task 10 接著寫 §4–§5）。

- [ ] **Step 1: 全套測試先綠**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release`
Run（`scripts/` 底下）：`./.venv/Scripts/python.exe -m pytest -q`
Expected: 全部 PASS（integration 照預設跳過）。

- [ ] **Step 2: 用目前分支重建 API 容器**

Run（repo 根目錄）：`docker compose up -d --build --no-deps api`
接著等到健康：`docker inspect -f '{{.State.Health.Status}}' prompt-copilot-api` 回 `healthy`（用 until 迴圈或 Monitor，不要 sleep）。

- [ ] **Step 3: 確認容器跑的是新程式**

Run（repo 根目錄）：`scripts/.venv/Scripts/python.exe manual-tests/replay.py --scenario Q2 --runs 1`
再查：`docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -At -c "select payload->>'kind', payload->>'searches' from audit_logs where session_id='<剛印出的 id>' and event_type='Turn_Completed' order by id;"`
Expected: 每列都有 `propose`／`act` 與數字。沒有的話容器沒重建成功，回 Step 2。這個 session 不算進基準。

- [ ] **Step 4: 跑基準**

Run（repo 根目錄，背景執行、逾時 30 分鐘）：
`scripts/.venv/Scripts/python.exe manual-tests/replay.py --scenario all --runs 3 > "$SCRATCH/baseline.md"`
（`$SCRATCH` 是 session 的 scratchpad 目錄。）完成後讀檔，取最後一行的 session id。

- [ ] **Step 5: 報表與檢索耗時**

Run（`scripts/` 底下）：`./.venv/Scripts/python.exe adoption_report.py --sessions <ids>`，留「## 檢索時機」一節。
Run：
```bash
docker exec prompt-copilot-db psql -U postgres -d prompt_copilot -At -c "select count(*), percentile_cont(0.5) within group (order by latency_ms), max(latency_ms) from audit_logs where event_type='Tool_Invoked' and payload->>'name'='SearchPresets' and session_id = any(string_to_array('<ids>', ','));"
```

- [ ] **Step 6: 寫實驗紀錄 §1–§3**

新檔 `docs/experiments/2026-10-06-retrieval-timing.md`，結構：

```markdown
# 檢索時機實驗紀錄（2026-10-06）

設計：[檢索時機設計](../superpowers/specs/2026-10-06-retrieval-timing-design.md)

## 1. 問題

（兩三句：session `ddaf2115` 15 輪只檢索 1 次、rag 4 個全是直譯碰巧對上；原因是流程說明只在動手輪第 1 條要求檢索。細節見設計 §1。）

## 2. 方法

- 重播：`manual-tests/replay.py`，劇本 Q1–Q3（`docs/eval-cases.md`），各 3 次。自動按確認，有解讀選項選第一個；追問一律選每個維度的第一個選項。
- API：Docker `api` 容器，分支 `feat/retrieval-timing` 的 commit `<hash>`；模型與 embedding 照 `appsettings.json`。
- 指標與通過標準（設計 §6.3）：（照抄設計的表）

## 3. 基準（流程說明未改，commit `<hash>`，<日期時間>）

session：`<ids>`

### 3.1 報表

（貼「## 檢索時機」一節原文）

### 3.2 每步檢索判定

| 劇本步 | 預期 | OK | MISS | NO-TURN | SKIP |
| :--- | :--- | ---: | ---: | ---: | ---: |
（Q1-1 … Q3-4，每列三次的合計，從 baseline.md 數）

### 3.3 檢索耗時

`SearchPresets` 工具呼叫 n 次，中位數 x ms，最大 y ms。

### 3.4 Q1 最後定稿

（三次的 positive 原文）

### 3.5 觀察

（列出跟 2026-10-05 那次 session 一致或不一致的地方，例如是否仍只在第一個動手輪檢索、穿著是否仍出現知識庫沒有的寫法。）
```

`<…>` 全部換成實際值，不可留角括號。

- [ ] **Step 7: Commit**

```bash
git add docs/experiments/2026-10-06-retrieval-timing.md
git commit -m "docs: retrieval timing baseline run

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: 流程說明加檢索規則

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs`
- Modify: `src/PromptCopilot.Api/Prompts/flow-act.md`、`flow-propose.md`、`system.md`
- Modify: `docs/單輪流程說明.md`、`docs/known-issues.md`
- Test: `src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs`

**Interfaces:**
- Consumes: 既有 `{{RETRIEVAL_STEP}}`／`{{RETRIEVAL_RULE}}` 的替換機制。
- Produces: 新 placeholder `{{RETRIEVAL_ACT}}`（只在 `flow-act.md`）、`{{RETRIEVAL_PROPOSE}}`（只在 `flow-propose.md`）；`SystemPromptBuilder` 的 `internal const string RetrievalActOn／RetrievalActOff／RetrievalProposeOn／RetrievalProposeOff`（off 都是 `""`）。

- [ ] **Step 1: 寫失敗的測試**

`SystemPromptBuilderTests.cs` 檔尾（類別結束前）加：

```csharp
    // ---- 檢索時機（2026-10-06）----

    /// <summary>設計 §3.1：第 2–4 條寫入前先檢索、借用優先片段寫法；採用不查。確認輪的段落不出現在動手輪。</summary>
    [Fact]
    public void Act_prompt_asks_to_search_before_writing_in_rules_2_to_4_and_to_borrow()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act);
        Assert.Contains("**檢索**：第 2–4 條要寫入新內容前（`SetFacetStates` 或 `FinalizePrompt` 之前），先用一次 `SearchPresets`", prompt);
        Assert.Contains("第 5 條（採用）不查", prompt);
        Assert.Contains("**借用**：結果裡標「可借入提示詞」、而且跟確認內容相符的片段，寫 tag 時優先用片段的寫法", prompt);
        Assert.DoesNotContain("卡片或回答要寫出使用者沒講的具體內容時", prompt);
    }

    /// <summary>設計 §3.2：卡片要寫出使用者沒講的具體內容時先檢索；使用者自己講清楚時不查。</summary>
    [Fact]
    public void Propose_prompt_asks_to_search_when_the_card_must_invent_content()
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        var (prompt, _) = Make().Build(s, ToolNames.ProposeAlways, TurnKind.Propose);
        Assert.Contains("**檢索**：卡片或回答要寫出使用者沒講的具體內容時，先用一次 `SearchPresets`", prompt);
        Assert.Contains("（「衣服你幫我設計」）", prompt);
        Assert.Contains("使用者自己講清楚要改什麼時，這一輪不查，動手輪會查", prompt);
        Assert.DoesNotContain("第 2–4 條要寫入新內容前", prompt);
    }

    /// <summary>Review Focus 1：還沒題材的確認輪沒有檢索工具；照著叫就是 known-issues #13。</summary>
    [Fact]
    public void Propose_retrieval_rule_says_not_to_search_when_the_tool_is_not_listed()
    {
        var (prompt, _) = Make().Build(new Session("s"), new HashSet<string> { ToolNames.Confirm }, TurnKind.Propose);
        Assert.Contains("本輪工具清單裡沒有 `SearchPresets` 時（還沒判定題材）就不查，照常確認", prompt);
    }

    /// <summary>設計 §3.1、§3.3：維度項目不可只寫維度名稱；選項優先從片段挑；不再鼓勵空 presetId。</summary>
    [Fact]
    public void Step_one_forbids_bare_dimension_names_and_options_prefer_presets()
    {
        var (act, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act);
        Assert.Contains("`query` 寫具體方向（例：「寫實攝影」與「日系動漫插畫」），不可只寫維度名稱（「風格」「鏡頭」）", act);
        var (propose, _) = Make().Build(new Session("s"), ToolNames.ProposeAlways, TurnKind.Propose);
        foreach (var p in new[] { act, propose })
        {
            Assert.Contains("- `AskUser` 的選項與 `Discuss` 的參考方向優先從檢索到的片段挑：label 寫片段的內容、tags 用片段的寫法、帶 presetId", p);
            Assert.Contains("`options` 是參考方向；知識庫沒有的方向 `presetId` 留空。", p);
            Assert.DoesNotContain("可以是知識庫沒有的方向", p);
        }
    }

    /// <summary>Review Focus 2：對照組兩種輪都不能出現新段落或 SearchPresets。</summary>
    [Fact]
    public void Retrieval_off_drops_the_new_paragraphs_in_both_kinds_of_turn()
    {
        var s = new Session("s", retrievalEnabled: false); s.ApplyProfile("portrait", Catalog);
        var proposeTools = ToolNames.ProposeAlways.Except(new[] { ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }).ToHashSet();
        var act = Make().Build(s, ToolsWithoutSearch, TurnKind.Act).Prompt;
        var propose = Make().Build(s, proposeTools, TurnKind.Propose).Prompt;
        foreach (var p in new[] { act, propose })
        {
            Assert.DoesNotContain("SearchPresets", p);
            Assert.DoesNotContain("**檢索**", p);
            Assert.DoesNotContain("**借用**", p);
            Assert.DoesNotContain("優先從檢索到的片段挑", p);
            Assert.DoesNotContain("{{", p);
        }
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~SystemPromptBuilderTests"`
Expected: 新的 5 個 FAIL。

- [ ] **Step 3: 改 `SystemPromptBuilder` 常數與替換**

`RetrievalStepOn` 換成（只改「沒講的維度」那段，其餘逐字不變，既有測試比對的字串都還在）：

```csharp
    internal const string RetrievalStepOn =
        "再**用一次 `SearchPresets`**：使用者講到的每個 facet 各一項，用 `facetId` 加上他描述那一項的原話，並附上翻成英文 SD tag 的 `tags`（例：`clothing.footwear`＋「拖鞋」＋`slippers`、`appearance.hair`＋「銀色雙馬尾」＋`silver hair, twintails`；寫法跟 `SetFacetStates` 的 `tags` 一樣）；使用者沒講的維度每個用 `dimension` 給兩個對比方向的項目，`query` 寫具體方向（例：「寫實攝影」與「日系動漫插畫」），不可只寫維度名稱（「風格」「鏡頭」）。不要把整句描述丟給一個維度，也不要一個項目一次呼叫。需要風格參考時呼叫 `SearchSimilarPrompts`。";
```

`RetrievalRuleOn` 換成（多一條選項規則）：

```csharp
    internal const string RetrievalRuleOn =
        "- `SearchPresets` 回的片段標了「可借入提示詞」或「僅供建議」，以及每個 facet 對本次使用者是 covered 還是 missing：「僅供建議」的片段任何詞都不可進提示詞；「可借入」的片段，標 missing 的 facet 對應的詞也不可進，只可進建議。相似度「低」的片段仍可借用其中與描述相符的詞，不可借與描述矛盾的詞。\n" +
        "- `AskUser` 的選項與 `Discuss` 的參考方向優先從檢索到的片段挑：label 寫片段的內容、tags 用片段的寫法、帶 presetId；知識庫沒有合適的方向才自己提（presetId 留空）。";
```

在 `RetrievalRuleOff` 之後加：

```csharp
    /// <summary>檢索時機設計 §3.1：動手輪第 2–4 條的檢索與借用。flow-act.md 清單之後的獨立段落，off 時整段消失、不留空號。</summary>
    internal const string RetrievalActOn =
        "**檢索**：第 2–4 條要寫入新內容前（`SetFacetStates` 或 `FinalizePrompt` 之前），先用一次 `SearchPresets` 查這一輪要寫的 facet：每個 facet 一項，`facetId` 加上確認內容裡那一項的說法，附上你翻的英文 `tags`。使用者選的選項帶 presetId（見「你先前提供過的選項」），或確認卡的內容是從上一輪檢索結果挑的，就直接用那個片段的 tag，不用再查那一項。隨便的確認卡沒列到的 missing facet，配合目前的畫面寫具體查詢（例：「雨夜街頭的外套」）。第 5 條（採用）不查。\n\n" +
        "**借用**：結果裡標「可借入提示詞」、而且跟確認內容相符的片段，寫 tag 時優先用片段的寫法，只借相符的詞；都不相符才用你自己翻的。";
    internal const string RetrievalActOff = "";
    /// <summary>檢索時機設計 §3.2：確認輪要寫出使用者沒講的具體內容時先檢索。flow-propose.md 清單之後的獨立段落。
    /// 還沒題材的確認輪沒有檢索工具（ToolSetBuilder），段落要明說那時不查，否則就是 known-issues #13 的觸發條件。</summary>
    internal const string RetrievalProposeOn =
        "**檢索**：卡片或回答要寫出使用者沒講的具體內容時，先用一次 `SearchPresets`，再從結果挑：說隨便／你決定（每個 missing 維度要列出補什麼）、把單一項目交給你（「衣服你幫我設計」）、要求太模糊要給 2–4 個解讀、問你推薦或還有什麼方向（`Discuss` 的參考方向）。查詢配合目前的畫面寫具體方向（例：「雨夜街頭的外套」「寫實攝影」）：整個維度用 `dimension` 項目，單一 facet 用 `facetId` 項目。卡片正文用中文描述你挑的片段內容，不寫英文 tag；`Discuss` 的參考方向帶片段的 presetId。使用者自己講清楚要改什麼時，這一輪不查，動手輪會查。本輪工具清單裡沒有 `SearchPresets` 時（還沒判定題材）就不查，照常確認。";
    internal const string RetrievalProposeOff = "";
```

`Build` 裡 `{{RETRIEVAL_RULE}}` 那行之後加：

```csharp
            .Replace("{{RETRIEVAL_ACT}}", s.RetrievalEnabled ? RetrievalActOn : RetrievalActOff)
            .Replace("{{RETRIEVAL_PROPOSE}}", s.RetrievalEnabled ? RetrievalProposeOn : RetrievalProposeOff)
```

類別上方那段 `<summary>`（「樣板 {{RETRIEVAL_STEP}}／{{RETRIEVAL_RULE}} 的內容…」）改成「樣板 {{RETRIEVAL_STEP}}／{{RETRIEVAL_RULE}}／{{RETRIEVAL_ACT}}／{{RETRIEVAL_PROPOSE}} 的內容。on 的前兩個是 2026-09-25 之前樣板裡的原文，搬進來是為了 off 時能整段換掉；後兩個是 2026-10-06 檢索時機設計加的。」

- [ ] **Step 4: 改三個樣板**

`src/PromptCopilot.Api/Prompts/flow-act.md`：檔尾（第 6 條之後）加一個空行與一行：

```
{{RETRIEVAL_ACT}}
```

`src/PromptCopilot.Api/Prompts/flow-propose.md`：檔尾（第 5 條之後）加一個空行與一行：

```
{{RETRIEVAL_PROPOSE}}
```

`src/PromptCopilot.Api/Prompts/system.md` 第 31 行：

```
- `Discuss` 是**回應**：這是我對你問題的回答，你可以無視它繼續講別的。`options` 是參考方向，可以是知識庫沒有的方向（`presetId` 留空）。`Discuss` 不能改 facet 狀態。
```

改成

```
- `Discuss` 是**回應**：這是我對你問題的回答，你可以無視它繼續講別的。`options` 是參考方向；知識庫沒有的方向 `presetId` 留空。`Discuss` 不能改 facet 狀態。
```

（三個檔案保留原本的換行格式；版本 hash 統一成 `\n` 算，不受影響。）

- [ ] **Step 5: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~SystemPromptBuilderTests"`
Expected: 全部 PASS（含既有的 `Retrieval_off_prompt_drops_search_step_and_borrow_rule`、`Flow_rule_asks_for_one_batched_SearchPresets_call_with_one_item_per_stated_facet`、`Flow_sections_name_tools_by_their_declared_names`）。

- [ ] **Step 6: 文件**

`docs/單輪流程說明.md` 第 0 節，「…第 8 節末尾另記 C# 端才有的參考組合與採用。」那段之後加一段：

```markdown
C# 端什麼時候檢索由它的流程說明決定，不是這裡的固定管線：2026-10-06 起，每個寫入新內容的動手輪都先檢索，
要寫出使用者沒講的內容的確認輪（隨便、單項委託、模糊要求、要求推薦）也先檢索。見
[檢索時機設計](superpowers/specs/2026-10-06-retrieval-timing-design.md)。
```

`docs/known-issues.md`：在「## 已修正」那行之前加：

```markdown
## 14. 檢索幾乎只在第一輪發生，RAG 對定稿的貢獻接近 0（效果，中）

**現象**（2026-10-05，session `ddaf2115…`）：15 輪、定稿 5 次，`SearchPresets` 只在第 2 輪叫 1 次。之後「衣服你幫我設計」「家居感的衣褲」「推薦一些場景」「深夜咖啡廳前」全由模型自己寫，例如 `comfortable lounge wear top`（知識庫 0 筆）；知識庫其實有 70 筆睡衣類穿著、15 筆咖啡廳場景。最後定稿標 rag 的 4 個 tag 全是使用者原話直譯、碰巧對上片段。

**原因**：流程說明只在 `flow-act.md` 第 1 條要求檢索（「用一次」）；回答追問、定稿後修改、隨便與確認輪都沒提。2026-09-24 改成批次檢索之後，每個 session 平均約 1 次。

**修正**（分支 `feat/retrieval-timing`，設計見 [檢索時機設計](superpowers/specs/2026-10-06-retrieval-timing-design.md)）：每個寫入新內容的動手輪都檢索；要寫出使用者沒講內容的確認輪先檢索；只寫維度名稱的查詢擋掉；確認輪檢索結果多留一輪；量測分「借來／碰巧對上」。結果見 [實驗紀錄](experiments/2026-10-06-retrieval-timing.md)。
```

- [ ] **Step 7: Commit**（流程說明與 `單輪流程說明.md` 同一個 commit）

```bash
git add src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs src/PromptCopilot.Api/Prompts src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs docs/單輪流程說明.md docs/known-issues.md
git commit -m "feat(prompt): search before writing in every act turn and before inventing content in propose turns

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: 擋掉只寫維度名稱的維度項目

**Files:**
- Modify: `src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs`
- Test: `src/PromptCopilot.Api.Tests/Plugins/KnowledgePluginTests.cs`

**Interfaces:**
- Consumes: `FacetCatalog.DimensionLabels`、`FacetCatalog.DimensionLabel(dimension, profile)`；Task 2 的 `SearchItemErrors`（錯誤進 `errors` 字典就會被數到）。
- Produces: 維度項目（沒有 `facetId`）的 `query` 去空白後等於維度代號（不分大小寫）、通用名稱或題材專屬名稱時，該項回錯誤 `維度 {dimension} 的 query 只寫了維度名稱，請寫具體方向（例：寫實攝影、日系動漫插畫）`，不進 embedding；其他項目照常。

- [ ] **Step 1: 寫失敗的測試**

`KnowledgePluginTests.cs` 的「檢索時機」區塊加：

```csharp
    /// <summary>設計 §3.5：維度項目只寫維度名稱，撈回的是全維度最近的隨機片段；該項回錯誤，其他項照常。</summary>
    [Theory]
    [InlineData("style", "風格")]
    [InlineData("style", " Style ")]
    [InlineData("camera", "鏡頭")]
    public async Task Dimension_item_with_only_the_dimension_name_is_an_item_error(string dimension, string query)
    {
        var (p, turn, _, embed, _, _) = Make();
        var results = Results(await p.SearchPresetsAsync(Q((dimension, query), ("scene", "雨夜街頭")), default));
        Assert.Equal($"維度 {dimension} 的 query 只寫了維度名稱，請寫具體方向（例：寫實攝影、日系動漫插畫）", results[0].GetProperty("error").GetString());
        Assert.False(results[1].TryGetProperty("error", out _));
        Assert.Equal(new[] { "雨夜街頭" }, Assert.Single(embed.Calls));
        Assert.Equal(1, turn.SearchItemErrors);
    }

    /// <summary>題材專屬名稱也擋：object 的 appearance 叫「主體外觀」。</summary>
    [Fact]
    public async Task Profile_specific_dimension_label_is_also_rejected()
    {
        var (p, _, s, _, _, _) = Make(profile: false);
        s.ApplyProfile("object", Catalog);
        var results = Results(await p.SearchPresetsAsync(Q(("appearance", "主體外觀")), default));
        Assert.Contains("只寫了維度名稱", results[0].GetProperty("error").GetString());
    }

    /// <summary>Review Focus 5：含維度名稱的具體查詢不能誤擋；facet 項目的 query 是使用者原話，不檢查。</summary>
    [Fact]
    public async Task Dimension_item_containing_the_name_in_a_concrete_query_still_runs()
    {
        var (p, _, _, embed, _, _) = Make();
        var results = Results(await p.SearchPresetsAsync(new[] { new SearchQuery("style", "寫實風格"), F("style.genre", "風格") }, default));
        Assert.All(results, r => Assert.False(r.TryGetProperty("error", out _)));
        Assert.Equal(2, Assert.Single(embed.Calls).Count);
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~KnowledgePluginTests"`
Expected: 前兩個 FAIL（沒有錯誤），第三個 PASS。

- [ ] **Step 3: 實作**

`SearchPresetsAsync` 驗證迴圈裡，`query 空白` 那行之後、`valid.Add(...)` 之前加：

```csharp
            // 檢索時機設計 §3.5：維度項目只寫維度名稱（「風格」），撈回的是全維度最近的隨機片段，沒有用
            if (facetId is null && IsBareDimensionName(q.Query, dimension, s.Profile))
            { errors[i] = $"維度 {dimension} 的 query 只寫了維度名稱，請寫具體方向（例：寫實攝影、日系動漫插畫）"; continue; }
```

類別裡（`CleanTags` 旁邊）加：

```csharp
    /// <summary>維度代號（不分大小寫）、通用名稱、題材專屬名稱（object 的 appearance 叫「主體外觀」）。只看整段相等，「寫實風格」不算。</summary>
    private bool IsBareDimensionName(string query, string dimension, string profile)
    {
        var q = query.Trim();
        return string.Equals(q, dimension, StringComparison.OrdinalIgnoreCase)
            || q == catalog.DimensionLabels.GetValueOrDefault(dimension)
            || q == catalog.DimensionLabel(dimension, profile);
    }
```

`ItemsHelp` 裡「使用者沒講的維度用 dimension 給兩個對比方向。」改成「使用者沒講的維度用 dimension 給兩個對比方向，query 寫具體方向（例：寫實攝影、日系動漫插畫），不可只寫維度名稱。」

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~KnowledgePluginTests|FullyQualifiedName~GeminiToolDeclarationTests"`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Plugins/KnowledgePlugin.cs src/PromptCopilot.Api.Tests/Plugins/KnowledgePluginTests.cs
git commit -m "feat(api): reject dimension items whose query is just the dimension name

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: 確認輪的檢索結果多留一輪

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/HistoryTrimmer.cs`
- Modify: `src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs`（收尾的壓縮呼叫）
- Test: `src/PromptCopilot.Api.Tests/Orchestration/HistoryTrimmerTests.cs`、`HistoryTrimmerGeminiTests.cs`、`AgenticOrchestratorTests.cs`

**Interfaces:**
- Consumes: 既有 `CompressGemini`、`CompressResult`。
- Produces:
  - `HistoryTrimmer.CompressTurn(ChatHistory h, int fromIndex, bool keepSearchResults = false)`：true 時 `SearchPresets` 結果不壓，其餘（含 `AskUser`／`Discuss` 選項的 tags）照舊。
  - `HistoryTrimmer.CompressSearchResultsBefore(ChatHistory h, int endIndex)`：壓 `endIndex` 之前的 `SearchPresets` 結果。
  - `CompressResult` 遇到已壓過的 `SearchPresets` 結果（沒有任何一項帶 `query`）回 null：原樣不動，Gemini 訊息不重建。
  - orchestrator 每輪收尾：`CompressTurn(history, startIdx, keepSearchResults: kind == TurnKind.Propose)` 之後呼叫 `CompressSearchResultsBefore(history, startIdx)`。

- [ ] **Step 1: 寫失敗的測試**

`HistoryTrimmerTests.cs` 檔尾加：

```csharp
    // ---- 檢索時機（2026-10-06 設計 §4）----

    private const string FullSearch = """{"results":[{"dimension":"clothing","facetId":"clothing.upper","query":"家居服","grounded":true,"poolSize":70,"hits":[{"id":9726,"title":"粉紅睡衣","positive":"pink pajamas"}]}]}""";

    [Fact]
    public void Keep_search_results_leaves_SearchPresets_whole_but_still_strips_option_tags()
    {
        var h = new ChatHistory();
        h.Add(ToolResult("SearchPresets", FullSearch));
        h.Add(Call("Discuss", new { message = "m", options = new[] { new { label = "睡衣", tags = "pink pajamas", presetId = 9726 } } }));

        HistoryTrimmer.CompressTurn(h, 0, keepSearchResults: true);

        Assert.Contains("pink pajamas", h[0].Items.OfType<FunctionResultContent>().Single().Result!.ToString());
        Assert.DoesNotContain("pink pajamas", h[1].Items.OfType<FunctionCallContent>().Single().Arguments!["options"]!.ToString());
    }

    [Fact]
    public void Compress_search_results_before_only_touches_earlier_search_results()
    {
        var h = new ChatHistory();
        h.Add(ToolResult("SearchPresets", FullSearch));                               // 上一個確認輪留下的
        h.Add(ToolResult("SearchSimilarPrompts", """[{"intent":"短","positive":"x"}]"""));
        h.AddUserMessage("對，就這樣");
        var end = h.Count;
        h.Add(ToolResult("SearchPresets", FullSearch));                               // 這一輪的

        HistoryTrimmer.CompressSearchResultsBefore(h, end);

        Assert.DoesNotContain("pink pajamas", h[0].Items.OfType<FunctionResultContent>().Single().Result!.ToString());
        Assert.Contains("\"positive\":\"x\"", h[1].Items.OfType<FunctionResultContent>().Single().Result!.ToString());   // 只壓 SearchPresets
        Assert.Contains("pink pajamas", h[end].Items.OfType<FunctionResultContent>().Single().Result!.ToString());
    }

    /// <summary>Review Focus 3：每輪都掃一次，已壓過的必須原樣不動。</summary>
    [Fact]
    public void Compressing_an_already_compressed_search_result_is_a_no_op()
    {
        var h = new ChatHistory();
        h.Add(ToolResult("SearchPresets", FullSearch));
        HistoryTrimmer.CompressTurn(h, 0);
        var once = h[0].Items.OfType<FunctionResultContent>().Single();
        HistoryTrimmer.CompressSearchResultsBefore(h, h.Count);
        Assert.Same(once, h[0].Items.OfType<FunctionResultContent>().Single());
    }
```

`HistoryTrimmerGeminiTests.cs` 檔尾加：

```csharp
    /// <summary>檢索時機設計 §4：確認輪留著的結果下一輪照樣帶片段；下一輪收尾壓掉之後才不帶；已壓過的 Gemini 訊息不重建（Review Focus 3）。</summary>
    [Fact]
    public async Task Kept_search_results_reach_the_next_request_and_are_compressed_at_its_end()
    {
        var run = await FirstTurnAsync(CallSearch, Text, Text);
        HistoryTrimmer.CompressTurn(run.History, run.StartIdx, keepSearchResults: true);

        await NextRequestAsync(run);
        Assert.Contains("silver hair", run.Http.Bodies[^1]);                          // 下一輪看得到完整片段

        var secondStart = run.History.ToList().FindIndex(m => m.Role == AuthorRole.User && m.Content == "第二輪") + 1;
        HistoryTrimmer.CompressSearchResultsBefore(run.History, secondStart);         // 下一輪收尾
        var tool = run.History.Single(m => m.Role == AuthorRole.Tool);
        await NextRequestAsync(run);
        Assert.DoesNotContain("silver hair", run.Http.Bodies[^1]);
        Assert.Contains("\"thoughtSignature\":\"SIG-1\"", run.Http.Bodies[^1]);

        HistoryTrimmer.CompressSearchResultsBefore(run.History, run.History.Count);  // 已壓過：不重建
        Assert.Same(tool, run.History.Single(m => m.Role == AuthorRole.Tool));
    }
```

`AgenticOrchestratorTests.cs` 的「檢索時機」區塊加：

```csharp
    /// <summary>設計 §4：確認輪的 SearchPresets 結果，動手輪看得到完整片段，動手輪收尾才壓掉。</summary>
    [Fact]
    public async Task Propose_turn_search_results_survive_until_the_next_turn_ends()
    {
        var h = new Harness();
        h.Session.ApplyProfile("portrait", Catalog);
        const string search = """{"results":[{"dimension":"clothing","facetId":"clothing.upper","query":"家居服","grounded":false,"poolSize":70,"hits":[{"id":9726,"title":"粉紅睡衣","positive":"pink pajamas"}]}]}""";
        static bool HasSnippet(ChatHistory hist) =>
            hist.Any(m => m.Items.OfType<FunctionResultContent>().Any(r => r.Result?.ToString()?.Contains("pink pajamas") == true));
        h.Chat.ThenAsync(async (hist, k) =>
        {
            // 模擬 connector 已跑完一次 SearchPresets（harness 沒掛 KnowledgePlugin）
            var call = new FunctionCallContent(ToolNames.SearchPresets, "Knowledge", "c-search");
            var callMsg = new ChatMessageContent(AuthorRole.Assistant, content: null); callMsg.Items.Add(call); hist.Add(callMsg);
            var toolMsg = new ChatMessageContent(AuthorRole.Tool, content: null); toolMsg.Items.Add(new FunctionResultContent(call, search)); hist.Add(toolMsg);
            return new[] { await Invoke(hist, k!, "Dialog", "Confirm", new { message = "穿著我會從知識庫挑粉紅睡衣。" }) };
        });
        await h.RunAsync("衣服你幫我設計");
        Assert.True(HasSnippet(h.Session.ChatHistory));                               // 確認輪收尾沒壓

        h.Chat.ThenAsync(async (hist, k) =>
        {
            Assert.True(HasSnippet(hist));                                            // 動手輪看得到
            return new[] { await Invoke(hist, k!, "Dialog", "AskUser", AskArgs()) };
        });
        await h.RunAsync(new TurnInput(ConfirmValidator.AcceptText, Confirmed: new ConfirmedInput(h.Session.PendingConfirmation!, null)));
        Assert.False(HasSnippet(h.Session.ChatHistory));                              // 動手輪收尾壓掉
    }
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~HistoryTrimmer|FullyQualifiedName~AgenticOrchestratorTests"`
Expected: 編譯失敗（`keepSearchResults`、`CompressSearchResultsBefore` 不存在）。

- [ ] **Step 3: 實作 `HistoryTrimmer`**

`CompressTurn` 換成以下三個方法（`OptionCarriers` 的處理照舊）：

```csharp
    /// <summary>keepSearchResults：確認輪收尾時 SearchPresets 結果先不壓，留給下一輪用完整片段（檢索時機設計 §4）；
    /// 下一輪收尾由 <see cref="CompressSearchResultsBefore"/> 壓掉。選項的 tags 照舊剝掉。</summary>
    public static void CompressTurn(ChatHistory h, int fromIndex, bool keepSearchResults = false)
    {
        Func<string, bool> include = keepSearchResults ? name => name != ToolNames.SearchPresets : _ => true;
        for (var i = fromIndex; i < h.Count; i++)
        {
            if (CompressResults(h, i, include)) continue;
            foreach (var c in h[i].Items.OfType<FunctionCallContent>())
            {
                if (!OptionCarriers.Contains(c.FunctionName) || c.Arguments is null) continue;
                if (c.Arguments.TryGetValue("options", out var o) && o is not null) c.Arguments["options"] = StripTags(o.ToString()!);
                if (c.Arguments.TryGetValue("asks", out var a) && a is not null) c.Arguments["asks"] = StripAskTags(a.ToString()!);
            }
        }
    }

    /// <summary>檢索時機設計 §4：把 endIndex 之前還沒壓的 SearchPresets 結果壓掉。每輪收尾都呼叫，
    /// 效果是確認輪留下的結果留到下一輪收尾。已壓過的 <see cref="CompressResult"/> 認得出來、原樣不動。</summary>
    public static void CompressSearchResultsBefore(ChatHistory h, int endIndex)
    {
        for (var i = 0; i < Math.Min(endIndex, h.Count); i++)
            CompressResults(h, i, name => name == ToolNames.SearchPresets);
    }

    /// <summary>第 i 則訊息裡 include 認可的工具結果換成壓縮版。回 true：這則是 Gemini 的工具訊息，已整則換掉。</summary>
    private static bool CompressResults(ChatHistory h, int i, Func<string, bool> include)
    {
        if (h[i] is GeminiChatMessageContent { CalledToolResults.Count: > 0 } g && CompressGemini(h, i, g, include) is { } rebuilt)
        {
            h[i] = rebuilt;
            return true;
        }
        foreach (var r in h[i].Items.OfType<FunctionResultContent>().ToList())
        {
            if (!include(r.FunctionName ?? "")) continue;
            var compressed = CompressResult(r.FunctionName ?? "", r.Result?.ToString() ?? "");
            if (compressed is null) continue;
            h[i].Items.Remove(r);
            h[i].Items.Add(new FunctionResultContent(r.FunctionName, r.PluginName, r.CallId, compressed));
        }
        return false;
    }
```

`CompressGemini` 簽章加 `Func<string, bool> include`，迴圈裡算 `compressed` 那行換成：

```csharp
                var name = r.FunctionResult.Function?.Name ?? "";
                var compressed = include(name) ? CompressResult(name, r.FunctionResult.GetValue<object>()?.ToString() ?? "") : null;
```

`CompressResult` 的 `case ToolNames.SearchPresets:` 裡，`if (JsonNode.Parse(json) is not JsonObject root …) return null;` 之後加：

```csharp
                    // 已壓過的形狀沒有 query（原始結果每一項都有，含錯誤項）：原樣不動，Gemini 的訊息就不必整則重建（檢索時機設計 §4）
                    if (!results.OfType<JsonObject>().Any(r => r.ContainsKey("query"))) return null;
```

- [ ] **Step 4: orchestrator 收尾**

`AgenticOrchestrator.ExecuteTurnAsync` 的

```csharp
            HistoryTrimmer.CompressTurn(session.ChatHistory, startIdx);
```

換成

```csharp
            HistoryTrimmer.CompressTurn(session.ChatHistory, startIdx, keepSearchResults: kind == TurnKind.Propose);
            // 檢索時機設計 §4：上一個確認輪留下的檢索結果，到這一輪收尾才壓。這裡之後不會再回滾，改到前面的訊息不會跟快照衝突
            HistoryTrimmer.CompressSearchResultsBefore(session.ChatHistory, startIdx);
```

- [ ] **Step 5: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release`
Expected: 全部 PASS。

- [ ] **Step 6: Commit**

```bash
git add src/PromptCopilot.Api/Orchestration/HistoryTrimmer.cs src/PromptCopilot.Api/Orchestration/AgenticOrchestrator.cs src/PromptCopilot.Api.Tests/Orchestration
git commit -m "feat(api): keep propose-turn search results for one more turn

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: 跑改後實驗、對照結論、收尾文件

**Files:**
- Modify: `docs/experiments/2026-10-06-retrieval-timing.md`（§4、§5）
- Modify: `docs/eval-cases.md`（Q1–Q3 結果）、`docs/known-issues.md`（#14）

**Interfaces:**
- Consumes: Task 6 的基準、Task 7–9 的程式。
- Produces: 通過標準逐項判定；沒過的列退路判斷，交給使用者決定（使用者交代：實驗結果不好要回頭再看怎麼改）。

- [ ] **Step 1: 全套測試**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release`
Run（`scripts/` 底下）：`./.venv/Scripts/python.exe -m pytest -q`
Run（repo 根目錄）：`scripts/.venv/Scripts/python.exe -m pytest manual-tests/test_replay.py -q`
Expected: 全部 PASS。

- [ ] **Step 2: 重建 API 容器並確認是新 prompt**

Run（repo 根目錄）：`docker compose up -d --build --no-deps api`，等到 `healthy`。
確認：跑 `replay.py --scenario Q2 --runs 1`，查該 session 的 `Turn_Completed` 的 `prompt_version` 跟基準不同（`select distinct prompt_version from audit_logs where session_id = …`）。這個 session 不算進結果。

- [ ] **Step 3: 跑改後**

Run（背景執行、逾時 30 分鐘）：`scripts/.venv/Scripts/python.exe manual-tests/replay.py --scenario all --runs 3 > "$SCRATCH/after.md"`

- [ ] **Step 4: 報表與檢索耗時**

同 Task 6 Step 5，換成改後的 session id。

- [ ] **Step 5: 寫實驗紀錄 §4–§5**

`docs/experiments/2026-10-06-retrieval-timing.md` 接著寫：

```markdown
## 4. 改後（commit `<hash>`，<日期時間>）

session：`<ids>`

### 4.1 報表
### 4.2 每步檢索判定
### 4.3 檢索耗時
### 4.4 Q1 最後定稿

（結構同 §3）

## 5. 對照與結論

| 指標 | 基準 | 改後 | 目標 | 判定 |
| :--- | :--- | :--- | :--- | :--- |
| 動手輪檢索率（採用除外） | | | ≥ 90% | |
| 交給模型決定／推薦的確認輪檢索率 | | | ≥ 80% | |
| 每次定稿「借來」的 tag | | | 平均 ≥ 2 | |
| rag 佔非基礎詞 | | | ≥ 50% | |
| 確認輪延遲中位數增加 | — | | ≤ 3 秒 | |
| 動手輪延遲中位數增加 | — | | ≤ 3 秒 | |

「交給模型決定／推薦的確認輪」= 報表的「隨便」確認輪＋帶參考方向的 Discuss 輪，加上重播表裡預期 `propose`／`both` 的步驟（單項委託與模糊解讀 audit 分不出來）；三者合併算一個比率，寫出分子分母。延遲增加 = 改後中位數減基準中位數，確認輪、動手輪各自算（不分有無檢索）。

人工檢查：Q1 定稿裡是否還有 `comfortable lounge wear top` 這類知識庫沒有、不像 SD tag 的寫法（逐一列出）。

**結論**：（逐項過／沒過；沒過的照設計 §6.4 寫退路判斷——檢索率不夠 → 程式把關；有查但借來的少 → 先看是片段不相符還是相符沒借，附例子；延遲超標 → 伺服器代查。）
```

`<…>` 全部換成實際值。

- [ ] **Step 6: eval-cases 與 known-issues**

`docs/eval-cases.md` 的「2026-10-06 檢索時機」一節最後一行改成實際結果摘要（每個劇本一句：過／沒過、哪一步 MISS 最多）。

`docs/known-issues.md` #14：
- **全部通過**：整條移到「## 已修正」底下（照該節其他條目的格式，標題改成 `### 14. …`），「修正」段補一句結果摘要。merge 後的 hash 由收尾（finishing-a-development-branch）補。
- **有沒過的**：留在原處，「修正」段後加「**結果**（2026-10-06）：…；下一步待與使用者討論」。

- [ ] **Step 7: Commit**

```bash
git add docs/experiments/2026-10-06-retrieval-timing.md docs/eval-cases.md docs/known-issues.md
git commit -m "docs: retrieval timing after-change run and conclusion

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 8: 回報使用者**

把 §5 的對照表與結論貼給使用者。有沒過的指標時**停下來討論**，不要自行加做退路（設計 §6.4）。全部通過才進 finishing-a-development-branch。

---

## 第二輪（2026-10-06，使用者在看完 Task 10 結果後決定「改一輪」）

Task 10 的結果：動手輪檢索率 34/39（5 個沒查的有 4 個沿用前一個確認輪的檢索結果）；要寫出使用者沒講內容的確認輪 6/12（單項委託夾在追問回答裡、模糊要求兩種 0/3）；確認輪協定違規 1 → 4 次、1 輪失敗。這一輪只改確認輪的檢索段落與量測，再重跑一次。

### Task 11: 確認輪段落補例子與收尾規則；量測認「沿用確認輪的檢索」

**Files:**
- Modify: `src/PromptCopilot.Api/Orchestration/SystemPromptBuilder.cs`（`RetrievalProposeOn`）
- Modify: `scripts/adoption_report.py`（`build_retrieval_section`）
- Modify: `manual-tests/replay.py`（`judge`）
- Modify: `docs/superpowers/specs/2026-10-06-retrieval-timing-design.md`（加 §10）、`manual-tests/README.md`（判定值說明）
- Test: `src/PromptCopilot.Api.Tests/Orchestration/SystemPromptBuilderTests.cs`、`scripts/tests/test_adoption_report.py`、`manual-tests/test_replay.py`

**Interfaces:**
- Consumes: Task 7 的 `RetrievalProposeOn`；Task 4 的 `build_retrieval_section`；Task 5 的 `judge`、`TurnResult`。
- Produces:
  - 報表多一行 `- 動手輪檢索率（含沿用前一個確認輪的檢索）：x/y`，排在原本「動手輪檢索率（不含採用）」那行之後：動手輪本身有查，或同一個 session 裡它前一筆 `Turn_Completed`（依 turn_index）是有查的確認輪，就算有查。原本那行不動。
  - `judge` 多一個結果 `CARRY`：要求動手輪檢索、動手輪沒查、但同一步的確認輪有查時，回 `CARRY`（算有查，內容沿用確認輪的檢索）。其餘規則不變。

- [ ] **Step 1: 寫失敗的測試**

`SystemPromptBuilderTests.cs`：既有的 `Propose_prompt_asks_to_search_when_the_card_must_invent_content` 裡

```csharp
        Assert.Contains("（「衣服你幫我設計」）", prompt);
```

改成

```csharp
        Assert.Contains("（「衣服你幫我設計」；跟追問的回答寫在同一句裡也算，只查交給你的那一項）", prompt);
```

並在「檢索時機」區塊加：

```csharp
    /// <summary>第二輪（2026-10-06 實驗 §5）：單項委託夾在追問回答裡、模糊要求兩種情況確認輪 0/3，補明確例子。</summary>
    [Fact]
    public void Propose_retrieval_rule_names_mixed_delegation_and_vague_requests()
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        var (prompt, _) = Make().Build(s, ToolNames.ProposeAlways, TurnKind.Propose);
        Assert.Contains("跟追問的回答寫在同一句裡也算，只查交給你的那一項", prompt);
        Assert.Contains("要求太模糊要給 2–4 個解讀（「更有氣質」「換個感覺」：從片段挑不同方向當 `choices`）", prompt);
    }

    /// <summary>第二輪：確認輪查完之後常直接叫動手輪才有的工具（協定違規 1 → 4 次）。</summary>
    [Fact]
    public void Propose_retrieval_rule_says_searching_does_not_unlock_act_tools()
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        var (prompt, _) = Make().Build(s, ToolNames.ProposeAlways, TurnKind.Propose);
        Assert.Contains("查完之後這一輪照樣以本輪工具清單裡的 `Confirm` 結束（清單裡有 `Discuss` 才能用 `Discuss`）", prompt);
        Assert.Contains("`SetFacetStates`、`FinalizePrompt` 要等使用者按下確認卡的下一輪才有，這一輪不能叫", prompt);
    }
```

`scripts/tests/test_adoption_report.py` 檔尾加：

```python
def test_act_rate_counting_a_preceding_searched_propose_turn():
    turns = [
        done("a", 1, "propose", "ConfirmOutcome", searches=1),
        done("a", 2, "act", "FinalizedOutcome"),                  # 沿用確認輪的檢索
        done("a", 3, "propose", "ConfirmOutcome"),
        done("a", 4, "act", "FinalizedOutcome"),                  # 兩輪都沒查
        done("a", 5, "propose", "ConfirmOutcome"),
        done("a", 6, "act", "FinalizedOutcome", searches=1),
        done("b", 1, "propose", "ConfirmOutcome", searches=1),
        done("c", 1, "act", "AskOutcome"),                        # 不同 session，不能沿用 b 的
    ]
    text = "\n".join(build_retrieval_section(turns))
    assert "動手輪檢索率（不含採用）：1/4（25.0%）" in text
    assert "動手輪檢索率（含沿用前一個確認輪的檢索）：2/4（50.0%）" in text
```

`manual-tests/test_replay.py` 的 `test_judge` 換成：

```python
def test_judge():
    searched, idle = TurnResult("propose", searched=True), TurnResult("act")
    assert judge("act", idle, TurnResult("act", searched=True)) == "OK"
    assert judge("act", searched, idle) == "CARRY"                       # 動手輪沒查，沿用確認輪的檢索
    assert judge("act", idle, idle) == "MISS"
    assert judge("act", TurnResult("propose", outcome="message", searched=True), None) == "NO-TURN"
    assert judge("both", searched, TurnResult("act", searched=True)) == "OK"
    assert judge("both", searched, idle) == "CARRY"
    assert judge("both", idle, TurnResult("act", searched=True)) == "MISS"
    assert judge("propose", searched, None) == "OK"
    assert judge("none", idle, None) == "OK"
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~SystemPromptBuilderTests"` → 3 個 FAIL。
Run（`scripts/` 底下）：`./.venv/Scripts/python.exe -m pytest tests/test_adoption_report.py -q` → 新測試 FAIL。
Run（repo 根目錄）：`scripts/.venv/Scripts/python.exe -m pytest manual-tests/test_replay.py -q` → `test_judge` FAIL。

- [ ] **Step 3: 實作**

`SystemPromptBuilder.RetrievalProposeOn` 換成：

```csharp
    internal const string RetrievalProposeOn =
        "**檢索**：卡片或回答要寫出使用者沒講的具體內容時，先用一次 `SearchPresets`，再從結果挑：說隨便／你決定（每個 missing 維度要列出補什麼）、把單一項目交給你（「衣服你幫我設計」；跟追問的回答寫在同一句裡也算，只查交給你的那一項）、要求太模糊要給 2–4 個解讀（「更有氣質」「換個感覺」：從片段挑不同方向當 `choices`）、問你推薦或還有什麼方向（`Discuss` 的參考方向）。查詢配合目前的畫面寫具體方向（例：「雨夜街頭的外套」「寫實攝影」）：整個維度用 `dimension` 項目，單一 facet 用 `facetId` 項目。卡片正文用中文描述你挑的片段內容，不寫英文 tag；`Discuss` 的參考方向帶片段的 presetId。查完之後這一輪照樣以本輪工具清單裡的 `Confirm` 結束（清單裡有 `Discuss` 才能用 `Discuss`）；`SetFacetStates`、`FinalizePrompt` 要等使用者按下確認卡的下一輪才有，這一輪不能叫。使用者自己講清楚要改什麼時，這一輪不查，動手輪會查。本輪工具清單裡沒有 `SearchPresets` 時（還沒判定題材）就不查，照常確認。";
```

`adoption_report.build_retrieval_section`：在 `act = [...]` 之後加

```python
    # 第二輪（2026-10-06）：動手輪沒查、但同一個 session 的前一筆是有查的確認輪，內容沿用那次檢索，也算有查
    prev: dict[tuple[str, int], Turn] = {}
    last: dict[str, Turn] = {}
    for t in sorted(new, key=lambda x: (x.session_id, x.turn_index)):
        if t.session_id in last:
            prev[(t.session_id, t.turn_index)] = last[t.session_id]
        last[t.session_id] = t

    def covered(t: Turn) -> bool:
        p = prev.get((t.session_id, t.turn_index))
        return searched(t) or (p is not None and p.payload["kind"] == "propose" and searched(p))
```

並在 `lines.append(f"- 動手輪檢索率（不含採用）：{rate(act)}")` 之後加

```python
    lines.append(f"- 動手輪檢索率（含沿用前一個確認輪的檢索）：{_pct(sum(covered(t) for t in act), len(act))}")
```

`replay.judge` 換成：

```python
def judge(expect: str, propose: TurnResult | None, act: TurnResult | None) -> str:
    """expect：propose／act／both／none。要求的那一輪沒發生記 NO-TURN；發生了但沒檢索記 MISS。
    要求動手輪檢索、動手輪沒查但同一步的確認輪有查時記 CARRY：內容沿用確認輪的檢索，流程說明允許（算有查）。"""
    need = {"propose": ["propose"], "act": ["act"], "both": ["propose", "act"], "none": []}[expect]
    got = {"propose": propose, "act": act}
    carried = False
    for k in need:
        if got[k] is None:
            return "NO-TURN"
        if not got[k].searched:
            if k == "act" and propose is not None and propose.searched:
                carried = True
                continue
            return "MISS"
    return "CARRY" if carried else "OK"
```

`manual-tests/README.md` 2.5 節判定值那句改成：「（`OK`／`CARRY` 動手輪沿用確認輪的檢索，算有查／`MISS` 沒查／`NO-TURN` 預期要查的那一輪沒發生／`SKIP` 沒有追問卡可選）」。

設計檔檔尾加：

```markdown
## 10. 第二輪修正（2026-10-06）

第一輪實驗（實驗紀錄 §4–§5）之後，使用者決定再改一輪：

- **確認輪段落**（§3.2）補兩個例子：單項委託跟追問的回答寫在同一句裡也算，只查交給模型的那一項；模糊要求（「更有氣質」「換個感覺」）從片段挑不同方向當 `choices`。再補收尾規則：查完照樣以 `Confirm` 結束（清單裡有 `Discuss` 才能用），`SetFacetStates`、`FinalizePrompt` 要等下一輪才有。第一輪確認輪協定違規 1 → 4 次，都是查完就想直接動手或叫清單裡沒有的 `Discuss`。
- **量測**（§5.3、§6.3）：動手輪檢索率多算一個「含沿用前一個確認輪的檢索」版本。§3.1 第 4 條本來就允許動手輪直接用確認輪挑好的片段；第一輪 5 個沒查的動手輪有 4 個是這種情況。通過標準的「動手輪檢索率 ≥ 90%」改看這個版本，原本的照列。重播腳本的判定多一個 `CARRY`。
```

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release`
Run（`scripts/` 底下）：`./.venv/Scripts/python.exe -m pytest tests/test_adoption_report.py -q`
Run（repo 根目錄）：`scripts/.venv/Scripts/python.exe -m pytest manual-tests/test_replay.py -q`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api src/PromptCopilot.Api.Tests scripts/adoption_report.py scripts/tests/test_adoption_report.py manual-tests docs/superpowers/specs/2026-10-06-retrieval-timing-design.md
git commit -m "feat(prompt): propose-turn retrieval examples and closing rule; count act turns carried by a searched propose turn

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 12: 跑第二輪實驗、收尾文件（主 session 跑）

同 Task 10 的 Step 1–8，差別：

- 結果寫在實驗紀錄 §6「第二輪」：§6.1 報表（含新的一行）、§6.2 每步判定（多一欄 CARRY）、§6.3 檢索耗時、§6.4 Q1 最後定稿、§6.5 協定違規、§6.6 對照（基準／第一輪／第二輪三欄）與結論。
- 「動手輪檢索率」以「含沿用前一個確認輪的檢索」那行判定（設計 §10）。
- 全部通過：known-issues #14 移到已修正，進最終整枝審查與 finishing-a-development-branch。仍有沒過的：停下來跟使用者討論。
