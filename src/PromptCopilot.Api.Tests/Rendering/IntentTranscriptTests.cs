using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Rendering;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Tests.Configuration;
using PromptCopilot.Api.Tests.Fakes;

namespace PromptCopilot.Api.Tests.Rendering;

public class IntentTranscriptTests
{
    private static readonly FacetCatalog Catalog = FacetCatalogTests.Real();

    private static JsonElement J(object v) => JsonSerializer.SerializeToElement(v);

    private static FunctionCallContent Call(string name, KernelArguments args) => new(name, null, $"id-{name}", args);

    private static Session NewSession()
    {
        var s = new Session("s1");
        s.ApplyProfile("portrait", Catalog);
        s.ChatHistory.AddSystemMessage("系統提示");
        return s;
    }

    [Fact]
    public void Takes_user_words_assistant_text_and_the_dialog_tools_in_order()
    {
        var s = NewSession();
        s.ChatHistory.AddUserMessage("一個銀髮少女在海邊");
        s.ChatHistory.Add(FakeChatCompletion.WithCalls(Call(ToolNames.Confirm, new KernelArguments
            { ["message"] = J("銀髮少女站在傍晚的海邊"), ["choices"] = J(new[] { "雙馬尾", "短髮" }) })));
        s.ChatHistory.Add(new ChatMessageContent(AuthorRole.Tool, "確認卡已送出"));
        s.ChatHistory.AddUserMessage("雙馬尾");
        s.ChatHistory.Add(FakeChatCompletion.WithCalls(
            Call(ToolNames.SearchPresets, new KernelArguments { ["query"] = J("銀髮") }),
            Call(ToolNames.AskUser, new KernelArguments
            {
                ["preamble"] = J("還差幾項"),
                ["asks"] = J(new[] { new { dimension = "appearance", question = "表情？", missingFacetIds = new[] { "appearance.expression" },
                    options = new[] { new { label = "微笑", tags = "smile", presetId = (long?)null }, new { label = "冷淡", tags = "expressionless", presetId = (long?)null } } } }),
            })));
        s.ChatHistory.AddUserMessage("微笑");
        s.ChatHistory.Add(FakeChatCompletion.WithCalls(Call(ToolNames.Discuss, new KernelArguments
            { ["message"] = J("可以加夕陽"), ["options"] = J(new[] { new { label = "夕陽", tags = "sunset" }, new { label = "月光", tags = "moonlight" } }) })));
        s.ChatHistory.Add(FakeChatCompletion.WithCalls(Call(ToolNames.FinalizePrompt, new KernelArguments { ["positivePrompt"] = J("1girl") })));
        s.ChatHistory.AddAssistantMessage("好的");

        var input = IntentTranscript.Build(s, Catalog);

        Assert.Equal(
            "對話：\n"
            + "使用者：一個銀髮少女在海邊\n"
            + "助理（確認）：銀髮少女站在傍晚的海邊　選項：雙馬尾／短髮\n"
            + "使用者：雙馬尾\n"
            + "助理（追問）：還差幾項；表情？　選項：微笑／冷淡\n"
            + "使用者：微笑\n"
            + "助理（回應）：可以加夕陽　參考：夕陽／月光\n"
            + "助理：好的\n"
            + "\n交給模型決定的：（無）",
            input.Transcript);
    }

    /// <summary>Review Focus 1：HistoryTrimmer 壓過之後參數是 JSON 字串，不是 JsonElement。</summary>
    [Fact]
    public void Arguments_rewritten_as_json_strings_still_read()
    {
        var s = NewSession();
        s.ChatHistory.Add(FakeChatCompletion.WithCalls(Call(ToolNames.AskUser, new KernelArguments
        {
            ["preamble"] = "還差一項",
            ["asks"] = """[{"dimension":"appearance","question":"髮型？","options":[{"label":"雙馬尾"},{"label":"短髮"}]}]""",
        })));
        Assert.Contains("助理（追問）：還差一項；髮型？　選項：雙馬尾／短髮", IntentTranscript.Build(s, Catalog).Transcript);
    }

    [Fact]
    public void Assistant_parts_are_cut_and_user_words_are_not()
    {
        var s = NewSession();
        var longUser = new string('貓', 400);
        s.ChatHistory.AddUserMessage(longUser);
        s.ChatHistory.AddAssistantMessage(new string('解', 350));
        var t = IntentTranscript.Build(s, Catalog).Transcript;
        Assert.Contains($"使用者：{longUser}\n", t);
        Assert.Contains($"助理：{new string('解', IntentTranscript.AssistantMaxChars)}…\n", t);
        Assert.DoesNotContain(new string('解', IntentTranscript.AssistantMaxChars + 1), t);
    }

    [Fact]
    public void Delegations_list_notes_then_waived_then_autofill()
    {
        var s = NewSession();
        s.ChatHistory.AddUserMessage("其他隨便");
        s.ApplyFacetStates(new Dictionary<string, FacetState> { ["scene.weather"] = FacetState.Waived }, Catalog);
        s.FacetNotes["appearance.hair"] = "使用者委託此項";   // 在 ApplyFacetStates 之後設，免得被它清掉
        s.AutoFill = true;
        var t = IntentTranscript.Build(s, Catalog).Transcript;
        Assert.EndsWith(
            "\n交給模型決定的：\n"
            + $"- {Catalog.Facets["appearance.hair"].Label}（使用者委託此項）\n"
            + $"- 使用者明說不指定：{Catalog.Facets["scene.weather"].Label}\n"
            + "- 使用者要求其餘沒講的隨便補",
            t);
    }

    [Fact]
    public void The_key_follows_user_words_and_delegations_only()
    {
        Session Make(string user, string assistant, bool autoFill = false)
        {
            var s = NewSession();
            s.ChatHistory.AddUserMessage(user);
            s.ChatHistory.AddAssistantMessage(assistant);
            s.AutoFill = autoFill;
            return s;
        }
        var a = IntentTranscript.Build(Make("銀髮少女", "好"), Catalog).Key;
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.Equal(a, IntentTranscript.Build(Make("銀髮少女", "完全不同的助理回覆"), Catalog).Key);   // 閉環：使用者沒開口就共用清單
        Assert.NotEqual(a, IntentTranscript.Build(Make("黑髮少女", "好"), Catalog).Key);              // 回滾後重打不同的話：要重新整理
        Assert.NotEqual(a, IntentTranscript.Build(Make("銀髮少女", "好", autoFill: true), Catalog).Key);
    }
}
