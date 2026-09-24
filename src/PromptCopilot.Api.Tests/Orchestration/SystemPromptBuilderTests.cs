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

    [Fact]
    public void Flow_rule_asks_for_one_batched_SearchPresets_call()
    {
        var s = new Session("s");
        var (prompt, _) = Make().Build(s, ToolNames.Always);
        Assert.Contains("用一次 `SearchPresets` 帶上所有適用的維度", prompt);
        Assert.DoesNotContain("分兩次呼叫", prompt);
    }
}
