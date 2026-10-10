namespace PromptCopilot.Api.Rendering;

/// <summary>同一段對話裡另一張評分 ok 的圖：seed、當時的 prompt、評分結果。</summary>
public sealed record PastRender(long Seed, string Positive, string Negative, SelfCheckResult Result);

/// <summary>修正建議（修正建議設計 §4）：只看使用者的要求、unclear 不看，依序 prompt 漏了 → 改寫 tag → 換 seed → 沒事。
/// 先修 prompt 再換 seed：prompt 有缺漏時換 seed 也補不出那樣東西；seed 不變，前後兩張的差異才看得出是 prompt 造成的。
/// 這套規則之後閉環直接沿用（評分設計 §14），所以全部由程式決定、不問模型。</summary>
public static class FixAdvisor
{
    public static FixSuggestion Advise(SelfCheckResult current, long seed, string positive, string negative, IReadOnlyList<PastRender> others, int seedsBeforeRewrite)
    {
        var n = Math.Max(1, seedsBeforeRewrite);
        var user = current.Items.Where(i => i.Source == RequirementSources.User).ToList();
        var missing = user.Where(i => i.Issue == "prompt_missing").ToList();
        var notRendered = user.Where(i => i.Issue == "not_rendered").ToList();
        var lucky = user.Where(i => i.Verdict == "met" && i.Tags.Count + i.NegativeTags.Count == 0).ToList();

        string kind;
        string? text = null, message = null;
        var targets = new List<RequirementVerdict>();
        if (missing.Count > 0)
        {
            kind = SuggestionKinds.FixPrompt;
            targets.AddRange(missing);
            text = $"建議：請助理補上{Quote(missing)}，seed 不變再生一張";
            message = $"請修改 prompt：補上{Quote(missing)}。這是我要的，但目前的 prompt 沒寫進去。";
        }
        else
        {
            // 只數同一份清單的圖：使用者又開口、清單換了，舊清單下的失敗不算（含這一張）
            var history = others.Where(o => o.Result.ListKey == current.ListKey).Append(new PastRender(seed, positive, negative, current)).ToList();
            var stuck = notRendered.Select(i => (Item: i, Seeds: FailedSeeds(i, positive, negative, history))).Where(x => x.Seeds >= n).ToList();
            if (stuck.Count > 0)
            {
                kind = SuggestionKinds.RewriteTags;
                targets.AddRange(stuck.Select(x => x.Item));
                text = $"建議：請助理改寫{Quote(targets)}的寫法（換了 {stuck.Min(x => x.Seeds)} 個 seed 都沒畫出來），seed 不變再生一張";
                message = "請改寫 prompt 裡這些要求的寫法："
                    + string.Join("、", stuck.Select(x => $"「{x.Item.Text}」（目前是 {TagsText(x.Item)}，換了 {x.Seeds} 個 seed 都沒畫出來）"))
                    + "。可以換同義的 tag 或加權重。";
            }
            else if (notRendered.Count > 0)
            {
                kind = SuggestionKinds.Reroll;
                targets.AddRange(notRendered);
                text = $"建議：換一個 seed 重生（{Quote(notRendered)}這次沒畫出來）";
            }
            else kind = SuggestionKinds.None;
        }

        var notes = new List<string>();
        var delegatedMisses = current.Items.Where(i => i.Source == RequirementSources.Delegated && i.Verdict == "unmet").ToList();
        if (delegatedMisses.Count > 0) notes.Add($"另外：模型幫你挑的{Quote(delegatedMisses)}沒畫出來（不計分，不建議修）");
        if (message is not null)
        {
            // 剛好畫出來的條目只在本來就要改 prompt 時順便補，不單獨觸發一次修正
            if (lucky.Count > 0) { message += $"另外{Quote(lucky)}這次剛好畫出來，也請寫進 prompt。"; targets.AddRange(lucky); }
            message += "其他地方不要動。";
        }
        else notes.AddRange(lucky.Select(i => $"「{i.Text}」prompt 沒寫，這次剛好畫出來；換 seed 可能就不見"));
        if (others.Any(o => o.Seed == seed && o.Positive == positive && o.Negative == negative))
            notes.Add("這張的 prompt 和 seed 跟之前某張一樣，畫面不會變；上次的修正可能沒有改到 prompt");

        return new FixSuggestion(kind, text, message, targets.Select(i => i.Id).ToList(), notes);
    }

    /// <summary>這條要求（同 id）被判「沒畫出來」的不同 seed 數。同一個 seed 生兩張只算一次。
    /// 那張的 prompt 跟這張一樣，或這條對到的 tag 跟這張一樣，才算同一條：文字步每張重新對 tag，prompt 沒變也會對到不同的 tag
    /// （eval-cases R16 的 B），只比 tag 會被雜訊歸零；prompt 改過、tag 也改寫過的舊圖才不算（之後閉環在同一份清單下改 prompt 時用得到）。</summary>
    private static int FailedSeeds(RequirementVerdict item, string positive, string negative, IReadOnlyList<PastRender> history)
    {
        var tags = TagSet(item);
        return history
            .Where(h => h.Result.Items.FirstOrDefault(x => x.Id == item.Id) is { Issue: "not_rendered" } v
                && ((h.Positive == positive && h.Negative == negative) || TagSet(v).SetEquals(tags)))
            .Select(h => h.Seed).Distinct().Count();
    }

    private static HashSet<string> TagSet(RequirementVerdict v) => v.Tags.Select(t => "+" + t).Concat(v.NegativeTags.Select(t => "-" + t)).ToHashSet();

    private static string Quote(IEnumerable<RequirementVerdict> items) => string.Join("、", items.Select(i => $"「{i.Text}」"));

    private static string TagsText(RequirementVerdict i)
    {
        var parts = new List<string>();
        if (i.Tags.Count > 0) parts.Add(string.Join(", ", i.Tags));
        if (i.NegativeTags.Count > 0) parts.Add("負向：" + string.Join(", ", i.NegativeTags));
        return string.Join("；", parts);
    }
}
