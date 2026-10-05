using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Tests.Sessions;

/// <summary>先確認再動手設計 §3.5：按確認的請求怎麼驗。</summary>
public class ConfirmationTests
{
    private static Session WithPending(int turn, params string[] choices)
    {
        var s = new Session("s");
        s.SetPendingConfirmation(new PendingConfirmation(turn, "我理解的畫面：一位女士站在雨夜街頭。", choices, false));
        return s;
    }

    [Fact]
    public void Proposal_without_choices_takes_a_null_choice_and_says_ok()
    {
        var c = ConfirmValidator.Validate(WithPending(3), new ConfirmRequest(3, null));
        Assert.Null(c.Choice);
        Assert.Null(c.ChosenText);
        Assert.Equal("對，就這樣", c.Text);
        Assert.Equal(ConfirmValidator.AcceptText, c.Text);
    }

    [Fact]
    public void Choice_picks_the_interpretation_text()
    {
        var c = ConfirmValidator.Validate(WithPending(3, "換掉飲料，改拿雨傘", "換掉相機，改拿雨傘"), new ConfirmRequest(3, 1));
        Assert.Equal(1, c.Choice);
        Assert.Equal("換掉相機，改拿雨傘", c.ChosenText);
        Assert.Equal("換掉相機，改拿雨傘", c.Text);
    }

    [Fact]
    public void No_pending_is_409()
    {
        var e = Assert.Throws<ConfirmValidationException>(() => ConfirmValidator.Validate(new Session("s"), new ConfirmRequest(1, null)));
        Assert.Equal(409, e.Status);
        Assert.Equal("沒有待確認的內容", e.Message);
    }

    /// <summary>Review Focus 3：另一個分頁、或已經動過手的舊卡。</summary>
    [Fact]
    public void Older_card_is_409()
    {
        var e = Assert.Throws<ConfirmValidationException>(() => ConfirmValidator.Validate(WithPending(5), new ConfirmRequest(3, null)));
        Assert.Equal(409, e.Status);
        Assert.Equal("只有最新一張確認卡可以按", e.Message);
    }

    [Theory]
    [InlineData(new string[0], 0)]                // 沒有選項卻帶 choice
    [InlineData(new[] { "a", "b" }, null)]        // 有選項卻沒選
    [InlineData(new[] { "a", "b" }, 2)]
    [InlineData(new[] { "a", "b" }, -1)]
    public void Choice_that_does_not_fit_the_card_is_400(string[] choices, int? choice)
    {
        var e = Assert.Throws<ConfirmValidationException>(() => ConfirmValidator.Validate(WithPending(3, choices), new ConfirmRequest(3, choice)));
        Assert.Equal(400, e.Status);
    }
}
