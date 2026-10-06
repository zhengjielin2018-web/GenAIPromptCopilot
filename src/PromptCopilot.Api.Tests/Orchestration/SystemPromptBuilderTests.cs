using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;
using PromptCopilot.Api.Tests.Configuration;

namespace PromptCopilot.Api.Tests.Orchestration;

public class SystemPromptBuilderTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();
    private static SystemPromptBuilder Make(int offeredLimit = 24) =>
        new(Catalog, new OrchestratorOptions { OfferedOptionsLimit = offeredLimit }, Path.Combine(AppContext.BaseDirectory, "Prompts"));

    [Fact]
    public void No_placeholder_survives_and_version_is_12_hex()
    {
        var (prompt, version) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act);
        Assert.DoesNotContain("{{", prompt);
        Assert.Matches("^[0-9a-f]{12}$", version);
    }

    [Fact]
    public void Lists_only_tools_of_this_turn()
    {
        var (prompt, _) = Make().Build(new Session("s"), new HashSet<string> { ToolNames.FinalizePrompt, ToolNames.SearchPresets }, TurnKind.Act);
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
        var (prompt, _) = Make().Build(s, ToolNames.Always, TurnKind.Act);
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
        Assert.DoesNotContain("## 你先前提供過的選項", Make().Build(s, ToolNames.Always, TurnKind.Act).Prompt);
        for (long i = 1; i <= 3; i++)
        {
            s.Ledger.Record(new LedgerEntry { Id = i, Title = $"t{i}", PromptSnippet = $"snip{i}", FacetIds = Array.Empty<string>() }, new LedgerHit("style", 0.2, true));
            s.Ledger.MarkOffered(i, new OfferedRef((int)i, "style", $"label{i}"));
        }
        var (prompt, _) = Make(offeredLimit: 2).Build(s, ToolNames.Always, TurnKind.Act);
        Assert.Contains("## 你先前提供過的選項", prompt);
        Assert.Contains("snip3", prompt); Assert.Contains("snip2", prompt); Assert.DoesNotContain("snip1", prompt);
    }

    /// <summary>版本 hash 是 eval 對得上 prompt 的鑰匙。樣板的換行在別台機器上可能被 git 轉成
    /// CRLF，組出來的 Facts 也用 Environment.NewLine——同一份 prompt 就會有兩個 hash。三個樣板檔都要顧到。</summary>
    [Fact]
    public void Version_is_stable_across_line_ending_styles()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Prompts");
        var crlfDir = Path.Combine(Path.GetTempPath(), $"prompts-crlf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(crlfDir);
        try
        {
            foreach (var f in new[] { SystemPromptBuilder.TemplateFile, SystemPromptBuilder.ProposeFlowFile, SystemPromptBuilder.ActFlowFile })
                File.WriteAllText(Path.Combine(crlfDir, f), File.ReadAllText(Path.Combine(src, f)).Replace("\r\n", "\n").Replace("\n", "\r\n"));
            var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
            foreach (var kind in new[] { TurnKind.Propose, TurnKind.Act })
            {
                var lfBuilt = Make().Build(s, ToolNames.Always, kind);
                var crlfBuilt = new SystemPromptBuilder(Catalog, new OrchestratorOptions(), crlfDir).Build(s, ToolNames.Always, kind);
                Assert.Equal(lfBuilt.Version, crlfBuilt.Version);
                Assert.DoesNotContain("\r\n", crlfBuilt.Prompt);
            }
        }
        finally { Directory.Delete(crlfDir, recursive: true); }
    }

    [Fact]
    public void Version_changes_when_facts_change()
    {
        var b = Make(); var s = new Session("s");
        var v1 = b.Build(s, ToolNames.Always, TurnKind.Act).Version;
        s.ApplyProfile("portrait", Catalog);
        Assert.NotEqual(v1, b.Build(s, ToolNames.Always, TurnKind.Act).Version);
    }

    /// <summary>2026-09-25：一個維度一句複合描述撈不到單品；使用者講到的每個 facet 各一項（facetId＋原話）。</summary>
    [Fact]
    public void Flow_rule_asks_for_one_batched_SearchPresets_call_with_one_item_per_stated_facet()
    {
        var s = new Session("s");
        var (prompt, _) = Make().Build(s, ToolNames.Always, TurnKind.Act);
        Assert.Contains("使用者講到的每個 facet 各一項，用 `facetId` 加上他描述那一項的原話", prompt);
        Assert.DoesNotContain("分兩次呼叫", prompt);
    }

    /// <summary>known-issues #9 與追問政策反轉：第一輪先標 covered 再檢索（grounded 才有值），
    /// 之後只要還有 missing 的維度就追問，不再「缺了無法定稿才問」。</summary>
    [Fact]
    public void Flow_rule_asks_for_every_missing_dimension_and_marks_covered_before_search()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act);
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
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act);
        Assert.Contains("標 `covered`，並在 `tags` 附上那一項的英文 tag（例：涼鞋 → `sandals`）", prompt);
    }

    /// <summary>2026-09-29 facet 向量：facet 項目要附英文 tag，查詢句才會是「原話（英文）」（設計 §5.1）。</summary>
    [Fact]
    public void Flow_rule_asks_for_english_tags_on_each_facet_item()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act);
        Assert.Contains("`clothing.footwear`＋「拖鞋」＋`slippers`", prompt);
        Assert.Contains("`appearance.hair`＋「銀色雙馬尾」＋`silver hair, twintails`", prompt);
        Assert.Contains("寫法跟 `SetFacetStates` 的 `tags` 一樣", prompt);
    }

    private static readonly IReadOnlySet<string> ToolsWithoutSearch =
        ToolNames.Always.Except(new[] { ToolNames.SearchPresets, ToolNames.SearchSimilarPrompts }).ToHashSet();

    /// <summary>計畫 §4.1：off 的 prompt 不能再要求檢索，也不能留下講片段可否借入的規則；on 的字句逐字不變。</summary>
    [Fact]
    public void Retrieval_off_prompt_drops_search_step_and_borrow_rule()
    {
        var on = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act);
        var off = Make().Build(new Session("s", retrievalEnabled: false), ToolsWithoutSearch, TurnKind.Act);

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
        var (prompt, _) = Make().Build(new Session("s", retrievalEnabled: false), ToolsWithoutSearch, TurnKind.Act);
        Assert.Contains("追問或定稿。然後：**只要還有 missing 的維度就 `AskUser`**", prompt);
    }

    /// <summary>設計 §6.4：採用句的處理規則。</summary>
    [Fact]
    public void Flow_rule_tells_the_model_how_to_handle_an_adoption_message()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act);
        Assert.Contains("5. 使用者訊息以「採用〈」開頭時", prompt);
        // 2026-10-05 起只有定稿卡推薦：採用一定在定稿之後，直接重新定稿（先確認再動手設計 §8）
        Assert.Contains("然後直接 `FinalizePrompt` 重新定稿", prompt);
        Assert.DoesNotContain("不要追問", prompt);
        // 「照它的」是取代：沒講明時模型把新 tag 加在舊的旁邊（2026-09-29 驗收 T5）
        Assert.Contains("「照它的」是**取代**", prompt);
        Assert.Contains("原本的 tag 全部拿掉", prompt);
        Assert.Contains("「取代原本的」後面列的", prompt);
        // off 模式也要有：規則無害，而且 off 的 session 根本不會收到採用句
        Assert.Contains("5. 使用者訊息以「採用〈」開頭時", Make().Build(new Session("s", retrievalEnabled: false), ToolsWithoutSearch, TurnKind.Act).Prompt);
    }

    // ---- 先確認再動手（2026-10-05）----

    [Fact]
    public void Propose_prompt_tells_the_model_to_confirm_and_gives_the_umbrella_example()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.ProposeAlways, TurnKind.Propose);
        Assert.Contains("確認輪", prompt);
        Assert.Contains("「換掉飲料，改拿雨傘」", prompt);
        Assert.Contains("不要列「算了不改」", prompt);
        Assert.Contains("他沒有按按鈕，這句不算確認", prompt);
        Assert.DoesNotContain("先 `SetFacetStates`", prompt);              // 動手輪的流程不在確認輪出現
        Assert.DoesNotContain("{{", prompt);
    }

    /// <summary>追問帶上限：只給已用次數時，模型看不出還能不能再問（Task 10 驗收修正輪）。</summary>
    [Fact]
    public void Facts_show_asks_used_against_the_limit()
    {
        var s = new Session("s"); s.RecordAsk();
        var builder = new SystemPromptBuilder(Catalog, new OrchestratorOptions { MaxAskCount = 3 }, Path.Combine(AppContext.BaseDirectory, "Prompts"));
        Assert.Contains("- 追問已用：1／上限 3", builder.Build(s, ToolNames.ProposeAlways, TurnKind.Propose).Prompt);
        Assert.Contains("- 追問已用：0／上限 2\n", Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act).Prompt);
    }

    /// <summary>「還有 missing facet 的維度」跟動手輪的判斷同一套：covered、waived、有委託 note 的 facet 都不算缺。</summary>
    [Fact]
    public void Facts_list_dimensions_that_still_have_missing_facets()
    {
        var s = new Session("s"); s.ApplyProfile("portrait", Catalog);
        Assert.Contains("- 還有 missing facet 的維度：" + string.Join("、", Catalog.Dimensions.Where(d => Catalog.FacetsOf("portrait", d).Count > 0).Select(d => Catalog.DimensionLabel(d, "portrait"))),
            Make().Build(s, ToolNames.ProposeAlways, TurnKind.Propose).Prompt);

        var style = Catalog.FacetsOf("portrait", "style");
        var scene = Catalog.FacetsOf("portrait", "scene");
        var states = style.ToDictionary(id => id, _ => FacetState.Covered);
        foreach (var id in scene.Skip(1)) states[id] = FacetState.Waived;
        s.ApplyFacetStates(states, Catalog);
        s.FacetNotes[scene[0]] = "使用者委託此項";                                   // 唯一還 missing 的 scene facet 有委託 note
        var line = Make().Build(s, ToolNames.ProposeAlways, TurnKind.Propose).Prompt.Split('\n').Single(l => l.StartsWith("- 還有 missing facet 的維度："));
        Assert.DoesNotContain(Catalog.DimensionLabel("style", "portrait"), line);
        Assert.DoesNotContain(Catalog.DimensionLabel("scene", "portrait"), line);
        Assert.Contains(Catalog.DimensionLabel("camera", "portrait"), line);

        var none = new Session("s"); none.ApplyProfile("portrait", Catalog);
        none.ApplyFacetStates(Catalog.Dimensions.SelectMany(d => Catalog.FacetsOf("portrait", d)).ToDictionary(id => id, _ => FacetState.Covered), Catalog);
        Assert.Contains("- 還有 missing facet 的維度：（無）", Make().Build(none, ToolNames.ProposeAlways, TurnKind.Propose).Prompt);
        Assert.DoesNotContain("- 還有 missing facet 的維度：", Make().Build(new Session("s"), ToolNames.ProposeAlways, TurnKind.Propose).Prompt);   // 還沒判定題材：沒有這行
    }

    /// <summary>Task 10 修正輪 2：確認輪猜不到一段自由回答會補齊哪些 facet，預告「接著問／直接定稿」連伺服器先算好的提示都常講錯，
    /// 所以確認卡只講要設什麼，不預告下一步；「隨便」例外（動手輪一律直接定稿），照舊逐維度列出補什麼。</summary>
    [Fact]
    public void Propose_prompt_does_not_announce_the_next_step_except_for_delegation()
    {
        var fresh = Make().Build(new Session("s"), ToolNames.ProposeAlways, TurnKind.Propose).Prompt;
        var asking = new Session("s"); asking.ApplyProfile("portrait", Catalog); asking.RecordAsk();
        var answered = Make().Build(asking, ToolNames.ProposeAlways, TurnKind.Propose).Prompt;
        foreach (var prompt in new[] { fresh, answered })
        {
            Assert.DoesNotContain("確認之後的下一步", prompt);
            Assert.DoesNotContain("「確認後我會接著問 X、Y」", prompt);
            Assert.Contains("不要預告確認之後是再追問還是定稿：按下按鈕後由系統決定", prompt);
            Assert.Contains("只有隨便／你決定／直接給我例外，那種一律直接定稿", prompt);
        }
        Assert.Contains("「Session 事實」的「還有 missing facet 的維度」", fresh);
        Assert.Contains("逐一寫出你要補的具體內容", fresh);
        Assert.Contains("說明確認後直接定稿", fresh);
        Assert.Contains("不能只寫「其他我來決定」", fresh);
        Assert.DoesNotContain("不要預告確認之後", Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act).Prompt);
    }

    /// <summary>Task 10 驗收：「其他你決定」確認後，「不要加入確認以外的改動」把 AutoFill 的補齊壓掉，定稿幾乎沒補。</summary>
    [Fact]
    public void Act_prompt_says_delegated_fill_ins_are_part_of_the_confirmation()
    {
        var pending = new PendingConfirmation(1, "風格補寫實攝影、鏡頭補半身平視，確認後直接定稿。", Array.Empty<string>(), true);
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act, new ConfirmedInput(pending, null));
        Assert.Contains("**補齊每一個 missing 的 facet**", prompt);
        Assert.Contains("卡上沒列到的 missing facet 也依畫面補上合理的 tag", prompt);
        Assert.Contains("不算「確認以外的改動」", prompt);
        Assert.Contains("使用者說隨便／你決定時的補齊不算，見第 4 條", prompt);
        // 伺服器組的確認區塊：「隨便」那張卡不再說「不要加入確認以外的改動」，改說補齊就是確認的內容
        Assert.Contains("使用者把沒講的交給你決定：補齊每一個 missing 的 facet 就是他確認的內容", prompt);
        Assert.DoesNotContain("這一輪照上面的內容動手，不要加入確認以外的改動。", prompt);
        Assert.DoesNotContain("補齊每一個 missing 的 facet", Make().Build(new Session("s"), ToolNames.ProposeAlways, TurnKind.Propose).Prompt);
        Assert.Contains("只列維度名稱（「補齊風格、鏡頭、穿著」）也不行", Make().Build(new Session("s"), ToolNames.ProposeAlways, TurnKind.Propose).Prompt);
    }

    /// <summary>Task 10 修正輪：模型偶爾只寫 `Confirm`（宣告名是 `Dialog_Confirm`），SK 回「function that wasn't defined」後它一直重叫到整輪逾時。
    /// 流程段寫明完整名稱；這裡確認寫的每個名稱都對得到 AgentKernelFactory 註冊的函式（外掛名 Session／Dialog 跟它一致）。</summary>
    [Fact]
    public void Flow_sections_name_tools_by_their_declared_names()
    {
        var propose = Make().Build(new Session("s"), ToolNames.ProposeAlways, TurnKind.Propose).Prompt;
        var act = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act).Prompt;
        Assert.Contains("`Dialog_Confirm`", propose);
        Assert.Contains("`Dialog_FinalizePrompt`", act);

        var all = ToolNames.Always.Union(ToolNames.ProposeAlways).Union(new[] { ToolNames.AskUser, ToolNames.Discuss, ToolNames.RequestSaveConsent, ToolNames.Confirm }).ToHashSet();
        var turn = new TurnContext(new Session("s"), 1, GuardResult.Ok(false), all, Channel.CreateUnbounded<AgentEvent>().Writer);
        var kernel = Kernel.CreateBuilder().Build();
        AgentKernelFactory.AddFiltered(kernel, "Session", new SessionPlugin(turn, Catalog), all);
        AgentKernelFactory.AddFiltered(kernel, "Dialog", new DialogPlugin(turn, Catalog, new OrchestratorOptions()), all);
        var named = Regex.Matches(propose + act, @"`(Session|Dialog)_(\w+)`").Select(m => (m.Groups[1].Value, m.Groups[2].Value)).Distinct().ToList();
        Assert.True(named.Count >= 7, $"只找到 {named.Count} 個完整名稱");
        foreach (var (plugin, fn) in named) Assert.True(kernel.Plugins.TryGetFunction(plugin, fn, out _), $"{plugin}_{fn} 沒有註冊");
    }

    [Fact]
    public void Propose_and_act_prompts_have_different_versions()
    {
        var s = new Session("s");
        Assert.NotEqual(Make().Build(s, ToolNames.Always, TurnKind.Propose).Version, Make().Build(s, ToolNames.Always, TurnKind.Act).Version);
    }

    [Fact]
    public void Act_prompt_starts_the_flow_with_the_confirmed_block()
    {
        var pending = new PendingConfirmation(1, "她兩手已經拿著相機和飲料，你想要哪一種？", new[] { "換掉相機，改拿雨傘", "換掉飲料，改拿雨傘" }, false);
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act, new ConfirmedInput(pending, 1));
        var flow = prompt[prompt.IndexOf("## 流程", StringComparison.Ordinal)..prompt.IndexOf("## Facet 四態", StringComparison.Ordinal)];
        Assert.Contains("### 使用者已確認", flow);
        Assert.Contains("她兩手已經拿著相機和飲料，你想要哪一種？", flow);
        Assert.Contains("使用者選的是：換掉飲料，改拿雨傘", flow);
        Assert.Contains("這一輪照上面的內容動手，不要加入確認以外的改動。", flow);
        Assert.Contains("動手輪", flow);
        Assert.DoesNotContain("{{", prompt);
    }

    [Fact]
    public void Act_prompt_without_a_confirmation_has_no_confirmed_block()
    {
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act);
        Assert.DoesNotContain("### 使用者已確認", prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    /// <summary>Review Focus 2：確認內容是模型與使用者的文字，最後才放進來，裡面的 {{…}} 不能再被展開。</summary>
    [Fact]
    public void Confirmed_text_is_inserted_last_and_never_expanded()
    {
        var pending = new PendingConfirmation(1, "我會把背景改成 {{TOOLS}} 與 {{FACETS}}", new[] { "選 {{SESSION_FACTS}}", "b" }, false);
        var (prompt, _) = Make().Build(new Session("s"), ToolNames.Always, TurnKind.Act, new ConfirmedInput(pending, 0));
        Assert.Contains("我會把背景改成 {{TOOLS}} 與 {{FACETS}}", prompt);
        Assert.Contains("使用者選的是：選 {{SESSION_FACTS}}", prompt);
    }

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
        Assert.Contains("（「衣服你幫我設計」；跟追問的回答寫在同一句裡也算，只查交給你的那一項）", prompt);
        Assert.Contains("使用者自己講清楚要改什麼時，這一輪不查，動手輪會查", prompt);
        Assert.DoesNotContain("第 2–4 條要寫入新內容前", prompt);
    }

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
}
