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
        s.RecordFinalize(new FinalPrompt("mountain", "lowres", "tips", "山上的日出"));
        var (prompt, _) = Make().Build(s, ToolNames.Always);
        Assert.Contains("profile：landscape", prompt);
        Assert.Contains("scene.season", prompt); Assert.Contains("waived", prompt);
        Assert.Contains("使用者委託此項", prompt);
        Assert.Contains("AutoFill：true", prompt);
        Assert.Contains("mountain", prompt);
        // landscape 不列人物穿著。只看 facet 清單：流程說明的 SearchPresets 例子本來就寫了 clothing.footwear。
        var listing = prompt[prompt.IndexOf("## Facet 清單", StringComparison.Ordinal)..prompt.IndexOf("## Session 事實", StringComparison.Ordinal)];
        Assert.Contains("scene.season", listing);
        Assert.DoesNotContain("clothing.", listing);
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

    /// <summary>版本 hash 是 eval 對得上 prompt 的鑰匙。樣板的換行在別台機器上可能被 git 轉成
    /// CRLF，組出來的 Facts 也用 Environment.NewLine——同一份 prompt 就會有兩個 hash。</summary>
    [Fact]
    public void Version_is_stable_across_line_ending_styles()
    {
        var lf = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "system.md")).Replace("\r\n", "\n");
        var crlfPath = Path.Combine(Path.GetTempPath(), $"system-crlf-{Guid.NewGuid():N}.md");
        File.WriteAllText(crlfPath, lf.Replace("\n", "\r\n"));
        try
        {
            var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
            var lfBuilt = Make().Build(s, ToolNames.Always);
            var crlfBuilt = new SystemPromptBuilder(Catalog, new OrchestratorOptions(), crlfPath).Build(s, ToolNames.Always);

            Assert.Equal(lfBuilt.Version, crlfBuilt.Version);
            Assert.DoesNotContain("\r\n", crlfBuilt.Prompt);
        }
        finally { File.Delete(crlfPath); }
    }

    [Fact]
    public void Version_changes_when_facts_change()
    {
        var b = Make(); var s = new Session("s");
        var v1 = b.Build(s, ToolNames.Always).Version;
        s.ApplyProfile("portrait", Catalog);
        Assert.NotEqual(v1, b.Build(s, ToolNames.Always).Version);
    }

    /// <summary>2026-09-25：一個維度一句複合描述撈不到單品；使用者講到的每個 facet 各一項（facetId＋原話）。</summary>
    [Fact]
    public void Flow_rule_asks_for_one_batched_SearchPresets_call_with_one_item_per_stated_facet()
    {
        var s = new Session("s");
        var (prompt, _) = Make().Build(s, ToolNames.Always);
        Assert.Contains("使用者講到的每個 facet 各一項，用 `facetId` 加上他描述那一項的原話", prompt);
        Assert.DoesNotContain("分兩次呼叫", prompt);
    }

    /// <summary>known-issues #9 與追問政策反轉：第一輪先標 covered 再檢索（grounded 才有值），
    /// 之後只要還有 missing 的維度就追問，不再「缺了無法定稿才問」。</summary>
    [Fact]
    public void Flow_rule_asks_for_every_missing_dimension_and_marks_covered_before_search()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always);
        Assert.Contains("先 `SetFacetStates`", prompt);
        Assert.True(
            prompt.IndexOf("先 `SetFacetStates`", StringComparison.Ordinal) < prompt.IndexOf("用一次 `SearchPresets`", StringComparison.Ordinal),
            "第 1 條要先 SetFacetStates 標 covered，再 SearchPresets");
        Assert.Contains("只要還有 missing 的維度就 `AskUser`", prompt);
        Assert.Contains("使用者只講了一部分的維度也要問剩下的 facet", prompt);
        Assert.DoesNotContain("才 `AskUser`", prompt);
        Assert.DoesNotContain("底下的 facet 全是 missing", prompt);
    }

    /// <summary>設計 §5.5：第 1 條要模型把 covered facet 的英文 tag 一起給，推薦的錨從這裡來。</summary>
    [Fact]
    public void Flow_rule_asks_for_english_tags_on_covered_facets()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always);
        Assert.Contains("標 `covered`，並在 `tags` 附上那一項的英文 tag（例：涼鞋 → `sandals`）", prompt);
    }

    private static readonly IReadOnlySet<string> ToolsWithoutSearch =
        ToolNames.Always.Except(new[] { ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }).ToHashSet();

    /// <summary>計畫 §4.1：off 的 prompt 不能再要求檢索，也不能留下講片段可否借入的規則；on 的字句逐字不變。</summary>
    [Fact]
    public void Retrieval_off_prompt_drops_search_step_and_borrow_rule()
    {
        var on = Make().Build(new Session("s"), ToolNames.Always);
        var off = Make().Build(new Session("s", retrievalEnabled: false), ToolsWithoutSearch);

        Assert.Contains("用一次 `SearchPresets`", on.Prompt);
        Assert.Contains("「僅供建議」的片段任何詞都不可進提示詞", on.Prompt);
        Assert.Contains("知識庫：on", on.Prompt);

        Assert.DoesNotContain("SearchPresets", off.Prompt);
        Assert.DoesNotContain("SearchSimilarPrompts", off.Prompt);
        Assert.Contains("本段對話沒有知識庫：不做檢索，直接依 facet 狀態追問或定稿。", off.Prompt);
        Assert.Contains("- 本段對話沒有知識庫片段，所有 tag 由你自行產生。", off.Prompt);
        Assert.Contains("知識庫：off", off.Prompt);
        Assert.DoesNotContain("{{", off.Prompt);
        Assert.NotEqual(on.Version, off.Version);
    }

    /// <summary>off 的步驟 1 仍要接得上「然後：只要還有 missing 的維度就 AskUser」，不能因為換掉一段就斷句。</summary>
    [Fact]
    public void Retrieval_off_step_one_still_flows_into_ask_rule()
    {
        var (prompt, _) = Make().Build(new Session("s", retrievalEnabled: false), ToolsWithoutSearch);
        Assert.Contains("追問或定稿。然後：**只要還有 missing 的維度就 `AskUser`**", prompt);
    }

    /// <summary>設計 §6.4：採用句的處理規則。</summary>
    [Fact]
    public void Flow_rule_tells_the_model_how_to_handle_an_adoption_message()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always);
        Assert.Contains("6. 使用者訊息以「採用〈」開頭時", prompt);
        // 在追問卡上採用時 session 還在收集：照第 1 條走，定稿閘門才不會擋（全分支審查 #1）
        Assert.Contains("然後照第 1 條判斷", prompt);
        Assert.Contains("不要再問剛採用的那些 facet", prompt);
        Assert.Contains("否則直接 `FinalizePrompt`", prompt);
        Assert.DoesNotContain("不要追問", prompt);
        // off 模式也要有：規則無害，而且 off 的 session 根本不會收到採用句
        Assert.Contains("6. 使用者訊息以「採用〈」開頭時", Make().Build(new Session("s", retrievalEnabled: false), ToolsWithoutSearch).Prompt);
    }
}
