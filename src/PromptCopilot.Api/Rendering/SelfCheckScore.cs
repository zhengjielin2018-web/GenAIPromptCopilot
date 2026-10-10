namespace PromptCopilot.Api.Rendering;

/// <summary>歸類、計分、說明文字（符合度設計 §5.2、§5.3）與 audit 計數（§10）。全部由程式算：
/// 分數拿來當閉環門檻時要穩定，不能讓模型自己給總分。</summary>
public static class SelfCheckScore
{
    /// <summary>prompt 漏了 → 改 prompt；prompt 有寫、沒畫出來 → 換 seed 或換寫法（§14）。met 而 tag 都空的仍是 none，前端另外標。</summary>
    public static string IssueOf(string verdict, IReadOnlyList<string> tags, IReadOnlyList<string> negativeTags) => verdict switch
    {
        "met" => "none",
        "unmet" => tags.Count + negativeTags.Count == 0 ? "prompt_missing" : "not_rendered",
        _ => "unclear",
    };

    /// <summary>只算使用者的要求；unclear 不進分母。分母是 0 時沒有分數。</summary>
    public static int? Score(IReadOnlyList<RequirementVerdict> items)
    {
        var user = items.Where(i => i.Source == RequirementSources.User).ToList();
        var met = user.Count(i => i.Verdict == "met");
        var judged = met + user.Count(i => i.Verdict == "unmet");
        return judged == 0 ? null : (int)Math.Round(100.0 * met / judged, MidpointRounding.AwayFromZero);
    }

    /// <summary>只列要求的文字，不附理由：理由在清單上看（§5.3）。</summary>
    public static string Summary(IReadOnlyList<RequirementVerdict> items)
    {
        var user = items.Where(i => i.Source == RequirementSources.User).ToList();
        if (user.Count == 0) return "沒有可以判斷的要求";
        var met = user.Count(i => i.Verdict == "met");
        var unmet = user.Where(i => i.Verdict == "unmet").Select(i => i.Text).ToList();
        var unclear = user.Count(i => i.Verdict == "unclear");
        if (met + unmet.Count == 0) return $"使用者要求 {user.Count} 條，都從圖上看不出來";
        var text = $"使用者要求 {user.Count} 條，{met} 條符合";
        if (unmet.Count > 0) text += $"；不符合：{string.Join("、", unmet)}";
        if (unclear > 0) text += $"；{unclear} 條看不出來";
        return text;
    }

    public static SelfCheckResult Build(IReadOnlyList<RequirementVerdict> items, string intentKey, bool reused) =>
        new(Score(items), Summary(items), items, intentKey[..Math.Min(8, intentKey.Length)], reused);

    /// <summary>audit 的 selfCheck（§10）：只記計數。沒有結果（unavailable、被擋）時只有 status，其餘 null。</summary>
    public static object Audit(string status, SelfCheckResult? result)
    {
        if (result is null)
            return new
            {
                status, score = (int?)null, user = (int?)null, met = (int?)null, unmet = (int?)null, unclear = (int?)null,
                promptMissing = (int?)null, notRendered = (int?)null, delegated = (int?)null, delegatedUnmet = (int?)null,
                listReused = (bool?)null, listKey = (string?)null,
            };
        var user = result.Items.Where(i => i.Source == RequirementSources.User).ToList();
        var delegated = result.Items.Where(i => i.Source == RequirementSources.Delegated).ToList();
        return new
        {
            status, score = result.Score, user = (int?)user.Count,
            met = (int?)user.Count(i => i.Verdict == "met"), unmet = (int?)user.Count(i => i.Verdict == "unmet"), unclear = (int?)user.Count(i => i.Verdict == "unclear"),
            promptMissing = (int?)user.Count(i => i.Issue == "prompt_missing"), notRendered = (int?)user.Count(i => i.Issue == "not_rendered"),
            delegated = (int?)delegated.Count, delegatedUnmet = (int?)delegated.Count(i => i.Verdict == "unmet"),
            listReused = (bool?)result.ListReused, listKey = result.ListKey,
        };
    }
}
