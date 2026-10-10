# 預覽圖的修正建議（手動輔助） Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 每張評完分的預覽圖給一個由程式決定的下一步（補 prompt／改寫 tag／換 seed），使用者按一下就照做；「換 seed 重生」變成真的做得到。

**Architecture:** 後端純函式 `FixAdvisor` 在評分完成時，用這張圖的評分加上同一段對話其他圖的紀錄（`Owner.Renders`）算出 `FixSuggestion`，存進 `SelfCheckResult`，跟分數一起在 `done` 時回給前端。`Session.RenderSeed` 改成「目前的 seed」：`POST /renders` 帶 `reroll: true` 時端點挑一個沒用過的 seed，收件成功才寫回。前端顯示建議那一行、按鈕與註記，按鈕走現有的 `runTurn`（改 prompt）或 `requestRender(..., { reroll: true })`（換 seed）。

**Tech Stack:** .NET 10 / ASP.NET Core minimal API、xUnit、Nuxt 3 + Pinia + Tailwind、Vitest、vue-tsc。

**Spec:** `docs/superpowers/specs/2026-10-10-fix-suggestions-design.md`（以下稱「修正建議設計」）；前情 `docs/superpowers/specs/2026-10-10-intent-fit-scoring-design.md`（「評分設計」）。

## Global Constraints

- **在主目錄的 `intent-fit-scoring` 分支上做**（接在評分案之後，已含本 spec 與計畫），不要用 worktree：worktree 路徑下的 Debug 建置會被 Windows 應用程式控制擋下。兩案做完一起 merge，這份計畫不 merge。
- C# 測試一律 Release：`dotnet test src/PromptCopilot.Api.Tests -c Release`；單一類別加 `--filter "FullyQualifiedName~類別名"`。
- 前端指令前先把 Node 加進 PATH（bash）：`export PATH="$LOCALAPPDATA/Microsoft/WinGet/Packages/OpenJS.NodeJS.22_Microsoft.Winget.Source_8wekyb3d8bbwe/node-v22.23.2-win-x64:$PATH"`，再 `cd src/PromptCopilot.Frontend && npx nuxi typecheck && npm test`。
- 註解、文件以繁體中文為主，密度與風格照周圍程式（解釋「為什麼」、引用「修正建議設計 §x」）。commit 標題英文，照 repo 慣例 `feat(api): …`／`feat(frontend): …`／`docs: …`。
- 每個 commit 訊息結尾加 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`（執行者若不是 Opus，改成當時 harness 給的那一行）。
- **RunPod key／endpoint id／磁碟 id 不進任何檔案與 commit**（只在 `.env`）。
- **不自動生圖、不自動改 prompt**：建議只顯示，按下按鈕才做。
- 線上名稱（原樣）：`suggestion.kind` `fix_prompt`／`rewrite_tags`／`reroll`／`none`；欄位 `text`、`message`、`itemIds`、`notes`；最外層 `seed`；POST body `reroll`；audit `seed`、`reroll`、`suggestion`、`suggestionItems`；設定 `Render:SeedsBeforeRewrite`（預設 2）。
- 給使用者看的字（原樣，測試比對）：
  - 建議：`建議：請助理補上「{text}」，seed 不變再生一張`、`建議：請助理改寫「{text}」的寫法（換了 {k} 個 seed 都沒畫出來），seed 不變再生一張`、`建議：換一個 seed 重生（「{text}」這次沒畫出來）`；多條用「、」串接，例：`「抱著貓」、「戴眼鏡」`。
  - 註記：`另外：模型幫你挑的「{text}」沒畫出來（不計分，不建議修）`、`「{text}」prompt 沒寫，這次剛好畫出來；換 seed 可能就不見`、`這張的 prompt 和 seed 跟之前某張一樣，畫面不會變；上次的修正可能沒有改到 prompt`。
  - 送給助理：`請修改 prompt：補上{quoted}。這是我要的，但目前的 prompt 沒寫進去。`、`請改寫 prompt 裡這些要求的寫法：{each}。可以換同義的 tag 或加權重。`（`{each}` 是 `「{text}」（目前是 {tags}，換了 {k} 個 seed 都沒畫出來）` 以「、」串接）、`另外{quoted}這次剛好畫出來，也請寫進 prompt。`、`其他地方不要動。`
  - 前端：`請助理修改`、`換 seed 重生`、`已送出修正`、`上一張還在生成`、`seed {seed}`。

## Review Focus

1. **換 seed 的請求收了件、卻在補審 prompt 時就被擋下**（`RenderService` 回 `Accepted`，紀錄是 `Blocked`）：圖沒生出來，目前的 seed 不能換掉。Task 2 的 `Reroll_blocked_at_prompt_review_keeps_the_current_seed` 釘住。
2. **使用者後來又開口、清單換了一份**（`listKey` 不同）：舊清單下的失敗次數不能算進來。Task 1 的 `Other_lists_do_not_count` 釘住。
3. **同一個 seed 生了兩張**（使用者在同一張卡按兩次生成預覽，或補 prompt 後再生）：只算一個 seed，不能提早叫人改寫。Task 1 的 `Seeds_are_counted_once_and_reset_when_tags_change` 釘住。
4. **其他圖的紀錄壞掉讓 `FixAdvisor` 丟例外**：分數與圖照給，只是沒有建議。Task 2 的 `Advice_failure_leaves_the_score_and_image` 釘住。
5. **「請助理修改」連按兩次，或對話輪正在跑時按**：不能重複送出。Task 3 的 `suggestionButton` 測試釘住。

---

## File Structure

| 檔案 | 動作 | 職責 |
| :--- | :--- | :--- |
| `src/PromptCopilot.Api/Rendering/Requirements.cs` | 改 | 加 `SuggestionKinds`、`FixSuggestion`；`SelfCheckResult` 多 `Suggestion` |
| `src/PromptCopilot.Api/Rendering/FixAdvisor.cs` | 新增 | 純函式：規則、seed 計數、組 `text`／`message`／`notes`；`PastRender` |
| `src/PromptCopilot.Api/Configuration/Options.cs` | 改 | `RenderOptions.SeedsBeforeRewrite` |
| `src/PromptCopilot.Api/Rendering/RenderRecord.cs` | 改 | `RenderRequest.Reroll`；view 的 `Seed`、`SelfCheckView.Suggestion` |
| `src/PromptCopilot.Api/Rendering/RenderPipeline.cs` | 改 | 評分完成後算建議；audit 欄位 |
| `src/PromptCopilot.Api/Sessions/Session.cs` | 改 | `RenderSeed` 可寫 |
| `src/PromptCopilot.Api/Endpoints/RenderEndpoints.cs` | 改 | `reroll`、挑 seed、收件成功才寫回；Swagger 說明 |
| `src/PromptCopilot.Frontend/types/api.ts` | 改 | `seed`、`SuggestionView` |
| `src/PromptCopilot.Frontend/lib/render.ts` | 改 | `suggestionButton` |
| `src/PromptCopilot.Frontend/lib/renderSlot.ts` | 改 | `fixSent`、`markFixSent` |
| `src/PromptCopilot.Frontend/composables/useApi.ts` | 改 | `requestRender` body 可帶 `reroll` |
| `src/PromptCopilot.Frontend/stores/session.ts` | 改 | `requestRender(turnIndex, opts)`、`followSuggestion` |
| `src/PromptCopilot.Frontend/components/RenderPreview.vue` | 改 | 建議那一行、按鈕、註記、seed |
| 文件 | 改 | Task 2 Step 11（單輪流程說明）、Task 4、Task 5 |

---

### Task 1: `FixAdvisor` 與設定

**Files:**
- Modify: `src/PromptCopilot.Api/Rendering/Requirements.cs`
- Create: `src/PromptCopilot.Api/Rendering/FixAdvisor.cs`
- Modify: `src/PromptCopilot.Api/Configuration/Options.cs`（`RenderOptions`）
- Test: `src/PromptCopilot.Api.Tests/Rendering/FixAdvisorTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Endpoints/RenderConfigTests.cs`

**Interfaces:**
- Consumes: `RequirementVerdict`、`SelfCheckResult`、`RequirementSources`、`SelfCheckScore.Build`（評分案）。
- Produces:
  - `public static class SuggestionKinds { public const string FixPrompt = "fix_prompt"; public const string RewriteTags = "rewrite_tags"; public const string Reroll = "reroll"; public const string None = "none"; }`
  - `public sealed record FixSuggestion(string Kind, string? Text, string? Message, IReadOnlyList<string> ItemIds, IReadOnlyList<string> Notes);`
  - `public sealed record SelfCheckResult(int? Score, string Summary, IReadOnlyList<RequirementVerdict> Items, string ListKey, bool ListReused, FixSuggestion? Suggestion = null);`
  - `public sealed record PastRender(long Seed, string Positive, string Negative, SelfCheckResult Result);`
  - `FixAdvisor.Advise(SelfCheckResult current, long seed, string positive, string negative, IReadOnlyList<PastRender> others, int seedsBeforeRewrite) → FixSuggestion`
  - `RenderOptions.SeedsBeforeRewrite`（int，預設 2）

- [ ] **Step 1: Write the failing tests**

`src/PromptCopilot.Api.Tests/Rendering/FixAdvisorTests.cs`：

```csharp
using PromptCopilot.Api.Rendering;

namespace PromptCopilot.Api.Tests.Rendering;

public class FixAdvisorTests
{
    private static readonly string[] None = Array.Empty<string>();
    private const string Pos = "1girl, silver hair, twintails, beach";
    private const string Neg = "lowres";

    private static RequirementVerdict V(string id, string text, string verdict, string[]? tags = null, string[]? neg = null, string source = RequirementSources.User)
    {
        var t = tags ?? new[] { "x" };
        var n = neg ?? None;
        return new RequirementVerdict(id, text, source, t, n, verdict, SelfCheckScore.IssueOf(verdict, t, n), "理由");
    }

    private static SelfCheckResult R(params RequirementVerdict[] items) => SelfCheckScore.Build(items, "key1", reused: false);

    private static FixSuggestion Advise(SelfCheckResult current, long seed = 1, IReadOnlyList<PastRender>? others = null, int n = 2, string pos = Pos) =>
        FixAdvisor.Advise(current, seed, pos, Neg, others ?? Array.Empty<PastRender>(), n);

    private static readonly RequirementVerdict HairUnmet = V("r1", "銀色雙馬尾", "unmet", new[] { "silver hair", "twintails" });

    [Fact]
    public void Prompt_missing_comes_first_even_with_not_rendered()
    {
        var s = Advise(R(HairUnmet, V("r2", "抱著貓", "unmet", None), V("r3", "戴眼鏡", "unmet", None), V("r4", "海邊", "met")));
        Assert.Equal(SuggestionKinds.FixPrompt, s.Kind);
        Assert.Equal("建議：請助理補上「抱著貓」、「戴眼鏡」，seed 不變再生一張", s.Text);
        Assert.Equal("請修改 prompt：補上「抱著貓」、「戴眼鏡」。這是我要的，但目前的 prompt 沒寫進去。其他地方不要動。", s.Message);
        Assert.Equal(new[] { "r2", "r3" }, s.ItemIds);
    }

    [Fact]
    public void Only_not_rendered_suggests_a_reroll()
    {
        var s = Advise(R(HairUnmet, V("r2", "海邊", "met")));
        Assert.Equal((SuggestionKinds.Reroll, "建議：換一個 seed 重生（「銀色雙馬尾」這次沒畫出來）", (string?)null), (s.Kind, s.Text, s.Message));
        Assert.Equal(new[] { "r1" }, s.ItemIds);
    }

    [Fact]
    public void Nothing_wrong_is_none()
    {
        var s = Advise(R(V("r1", "海邊", "met"), V("r2", "某畫師", "unclear")));
        Assert.Equal((SuggestionKinds.None, (string?)null, (string?)null), (s.Kind, s.Text, s.Message));
        Assert.Empty(s.ItemIds);
        Assert.Empty(s.Notes);
    }

    [Fact]
    public void Failing_on_enough_seeds_suggests_rewriting_the_tags()
    {
        var earlier = new PastRender(7, Pos, Neg, R(HairUnmet));
        var s = Advise(R(HairUnmet, V("r2", "海邊", "met")), seed: 8, others: new[] { earlier });
        Assert.Equal(SuggestionKinds.RewriteTags, s.Kind);
        Assert.Equal("建議：請助理改寫「銀色雙馬尾」的寫法（換了 2 個 seed 都沒畫出來），seed 不變再生一張", s.Text);
        Assert.Equal("請改寫 prompt 裡這些要求的寫法：「銀色雙馬尾」（目前是 silver hair, twintails，換了 2 個 seed 都沒畫出來）。可以換同義的 tag 或加權重。其他地方不要動。", s.Message);
        Assert.Equal(new[] { "r1" }, s.ItemIds);
    }

    /// <summary>Review Focus 3：同一個 seed 生兩張只算一次（改 prompt 時 seed 不變）；tag 改寫過就從頭算。</summary>
    [Fact]
    public void Seeds_are_counted_once_and_reset_when_tags_change()
    {
        var sameSeed = new PastRender(7, Pos, Neg, R(HairUnmet));
        Assert.Equal(SuggestionKinds.Reroll, Advise(R(HairUnmet), seed: 7, others: new[] { sameSeed }).Kind);

        var otherTags = new PastRender(5, Pos, Neg, R(V("r1", "銀色雙馬尾", "unmet", new[] { "silver hair", "twin tails" })));
        Assert.Equal(SuggestionKinds.Reroll, Advise(R(HairUnmet), seed: 8, others: new[] { otherTags }).Kind);
    }

    /// <summary>Review Focus 2：使用者又開口、清單換了（listKey 不同），舊清單的失敗不算。</summary>
    [Fact]
    public void Other_lists_do_not_count()
    {
        var oldList = new PastRender(7, Pos, Neg, SelfCheckScore.Build(new[] { HairUnmet }, "key0", false));
        Assert.Equal(SuggestionKinds.Reroll, Advise(R(HairUnmet), seed: 8, others: new[] { oldList }).Kind);
    }

    [Fact]
    public void Threshold_comes_from_the_setting_and_at_least_one()
    {
        Assert.Equal(SuggestionKinds.RewriteTags, Advise(R(HairUnmet), n: 1).Kind);
        Assert.Equal(SuggestionKinds.RewriteTags, Advise(R(HairUnmet), n: 0).Kind);
        var earlier = new PastRender(7, Pos, Neg, R(HairUnmet));
        Assert.Equal(SuggestionKinds.Reroll, Advise(R(HairUnmet), seed: 8, others: new[] { earlier }, n: 3).Kind);
    }

    [Fact]
    public void Prompt_fix_beats_rewrite()
    {
        var earlier = new PastRender(7, Pos, Neg, R(HairUnmet));
        Assert.Equal(SuggestionKinds.FixPrompt, Advise(R(HairUnmet, V("r2", "抱著貓", "unmet", None)), seed: 8, others: new[] { earlier }).Kind);
    }

    [Fact]
    public void Lucky_items_ride_along_with_a_prompt_change()
    {
        var lucky = V("r5", "不要帽子", "met", None);
        var fix = Advise(R(V("r2", "抱著貓", "unmet", None), lucky));
        Assert.Equal("請修改 prompt：補上「抱著貓」。這是我要的，但目前的 prompt 沒寫進去。另外「不要帽子」這次剛好畫出來，也請寫進 prompt。其他地方不要動。", fix.Message);
        Assert.Equal(new[] { "r2", "r5" }, fix.ItemIds);
        Assert.Empty(fix.Notes);

        var reroll = Advise(R(HairUnmet, lucky));
        Assert.Equal(new[] { "r1" }, reroll.ItemIds);
        Assert.Equal(new[] { "「不要帽子」prompt 沒寫，這次剛好畫出來；換 seed 可能就不見" }, reroll.Notes);

        Assert.Equal(new[] { "「不要帽子」prompt 沒寫，這次剛好畫出來；換 seed 可能就不見" }, Advise(R(lucky)).Notes);
    }

    [Fact]
    public void Delegated_misses_are_noted_not_fixed()
    {
        var s = Advise(R(V("r1", "海邊", "met"),
            V("r6", "白色洋裝", "unmet", source: RequirementSources.Delegated), V("r7", "微笑", "unmet", None, source: RequirementSources.Delegated)));
        Assert.Equal(SuggestionKinds.None, s.Kind);
        Assert.Equal(new[] { "另外：模型幫你挑的「白色洋裝」、「微笑」沒畫出來（不計分，不建議修）" }, s.Notes);
    }

    [Fact]
    public void Unclear_items_are_ignored()
    {
        Assert.Equal(SuggestionKinds.None, Advise(R(V("r1", "某畫師", "unclear", None))).Kind);
    }

    [Fact]
    public void Same_prompt_and_seed_as_before_is_noted_after_other_notes()
    {
        var before = new PastRender(7, Pos, Neg, R(V("r2", "抱著貓", "unmet", None)));
        var s = Advise(R(V("r2", "抱著貓", "unmet", None), V("r6", "白色洋裝", "unmet", source: RequirementSources.Delegated)), seed: 7, others: new[] { before });
        Assert.Equal(new[]
        {
            "另外：模型幫你挑的「白色洋裝」沒畫出來（不計分，不建議修）",
            "這張的 prompt 和 seed 跟之前某張一樣，畫面不會變；上次的修正可能沒有改到 prompt",
        }, s.Notes);
        Assert.Empty(Advise(R(V("r1", "海邊", "met")), seed: 7, others: new[] { before }, pos: Pos + ", cat").Notes);
    }

    [Fact]
    public void Rewrite_lists_negative_tags()
    {
        var noHat = V("r3", "不要帽子", "unmet", None, new[] { "hat" });
        var s = Advise(R(noHat), n: 1);
        Assert.Contains("「不要帽子」（目前是 負向：hat，換了 1 個 seed 都沒畫出來）", s.Message);
    }
}
```

`src/PromptCopilot.Api.Tests/Endpoints/RenderConfigTests.cs` 的 `Defaults_match_the_spec` 最後加一行：

```csharp
        Assert.Equal(2, o.SeedsBeforeRewrite);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~FixAdvisorTests|FullyQualifiedName~RenderConfigTests"`
Expected: 編譯失敗，`FixAdvisor`、`SuggestionKinds`、`PastRender`、`SeedsBeforeRewrite` 不存在。

- [ ] **Step 3: Write minimal implementation**

`src/PromptCopilot.Api/Rendering/Requirements.cs`：把 `SelfCheckResult` 那一行（含註解）換成：

```csharp
/// <summary>一張圖的評分。ListKey：IntentKey 前 8 碼，audit 用來看哪幾張是對同一份清單判的。
/// Suggestion：修正建議（修正建議設計 §4），評分完成時由 FixAdvisor 算好；算不出來是 null。</summary>
public sealed record SelfCheckResult(int? Score, string Summary, IReadOnlyList<RequirementVerdict> Items, string ListKey, bool ListReused,
    FixSuggestion? Suggestion = null);

public static class SuggestionKinds
{
    public const string FixPrompt = "fix_prompt";
    public const string RewriteTags = "rewrite_tags";
    public const string Reroll = "reroll";
    public const string None = "none";
}

/// <summary>對外格式的 selfCheck.suggestion（修正建議設計 §6）。Text：給使用者看的一行（none 時 null）；Message：按下按鈕送給助理的話
/// （只有 fix_prompt、rewrite_tags 有）；ItemIds：針對哪幾條；Notes：委託畫錯、剛好畫出來、prompt 與 seed 都沒變的註記。</summary>
public sealed record FixSuggestion(string Kind, string? Text, string? Message, IReadOnlyList<string> ItemIds, IReadOnlyList<string> Notes);
```

`src/PromptCopilot.Api/Rendering/FixAdvisor.cs`：

```csharp
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
            var stuck = notRendered.Select(i => (Item: i, Seeds: FailedSeeds(i, history))).Where(x => x.Seeds >= n).ToList();
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

    /// <summary>這條要求（同 id、同一組 tag）被判「沒畫出來」的不同 seed 數。同一個 seed 生兩張只算一次；tag 改寫過就不算舊的。</summary>
    private static int FailedSeeds(RequirementVerdict item, IReadOnlyList<PastRender> history)
    {
        var tags = TagSet(item);
        return history
            .Where(h => h.Result.Items.FirstOrDefault(x => x.Id == item.Id) is { Issue: "not_rendered" } v && TagSet(v).SetEquals(tags))
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
```

`src/PromptCopilot.Api/Configuration/Options.cs`，`RenderOptions` 的 `DefaultImageSeconds` 下面加：

```csharp
    /// <summary>同一條要求在幾個不同 seed 下都沒畫出來，修正建議就從「換 seed」改成「改寫 tag」（修正建議設計 §4）。
    /// 先用 2：額度有限（每段對話 10 張）；之後從 audit 看實際換第幾個 seed 才畫出來再調。小於 1 時 FixAdvisor 當 1。</summary>
    public int SeedsBeforeRewrite { get; set; } = 2;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release --filter "FullyQualifiedName~FixAdvisorTests|FullyQualifiedName~RenderConfigTests|FullyQualifiedName~SelfCheckScoreTests"`
Expected: PASS（全部）。

- [ ] **Step 5: Commit**

```bash
git add src/PromptCopilot.Api/Rendering/Requirements.cs src/PromptCopilot.Api/Rendering/FixAdvisor.cs src/PromptCopilot.Api/Configuration/Options.cs src/PromptCopilot.Api.Tests/Rendering/FixAdvisorTests.cs src/PromptCopilot.Api.Tests/Endpoints/RenderConfigTests.cs
git commit -m "feat(api): FixAdvisor decides the next fix for a scored preview"
```

---

### Task 2: 後端接上建議與換 seed

**Files:**
- Modify: `src/PromptCopilot.Api/Sessions/Session.cs:62-63`
- Modify: `src/PromptCopilot.Api/Rendering/RenderRecord.cs`
- Modify: `src/PromptCopilot.Api/Rendering/RenderPipeline.cs`
- Modify: `src/PromptCopilot.Api/Endpoints/RenderEndpoints.cs`
- Modify: `docs/單輪流程說明.md:452`
- Test: `src/PromptCopilot.Api.Tests/Sessions/SessionTests.cs:216-222`
- Test: `src/PromptCopilot.Api.Tests/Rendering/RenderRecordTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Rendering/RenderPipelineTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Endpoints/RenderEndpointTests.cs`
- Test: `src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `FixAdvisor.Advise`、`PastRender`、`FixSuggestion`、`SelfCheckResult.Suggestion`、`RenderOptions.SeedsBeforeRewrite`。
- Produces（Task 3 依賴的對外格式）：
  - `RenderRequest(..., IReadOnlyList<Requirement>? ReusedRequirements = null, bool Reroll = false)`
  - `RenderView(..., RenderTimingsView Timings, long Seed)`；`SelfCheckView(string Status, int? Score, string? Summary, IReadOnlyList<RequirementVerdict> Items, FixSuggestion? Suggestion)`
  - `RenderPostRequest(int TurnIndex, string? Safety = null, bool Reroll = false)`
  - `Session.RenderSeed { get; set; }`

- [ ] **Step 1: Write the failing tests**

`src/PromptCopilot.Api.Tests/Sessions/SessionTests.cs` 的 `Render_seed_is_fixed_for_the_session` 換成：

```csharp
    /// <summary>修正建議設計 §5：建 session 時隨機一次；之後只有「換 seed 重生」收件成功時才被寫成新的。</summary>
    [Fact]
    public void Render_seed_starts_random_and_changes_only_when_set()
    {
        var s = New();
        var first = s.RenderSeed;
        Assert.True(first > 0);
        Assert.Equal(first, s.RenderSeed);
        s.RenderSeed = first + 1;
        Assert.Equal(first + 1, s.RenderSeed);
    }
```

`src/PromptCopilot.Api.Tests/Rendering/RenderRecordTests.cs`：
- `View_uses_the_wire_names` 的 `"\"selfCheck\":{\"status\":\"pending\",\"score\":null,\"summary\":null,\"items\":[]}"` 改成 `"\"selfCheck\":{\"status\":\"pending\",\"score\":null,\"summary\":null,\"items\":[],\"suggestion\":null}"`，並加 `Assert.Contains("\"seed\":42", json);`。
- 檔尾加：

```csharp
    [Fact]
    public void Suggestion_is_shown_only_when_done()
    {
        var advised = Hair with { Suggestion = new FixSuggestion(SuggestionKinds.None, null, null, Array.Empty<string>(), new[] { "註記" }) };
        var r = New();
        r.MarkGenerating(0); r.ImageArrived(Png, 1, 1);
        r.SelfCheckFinished(advised, 900);
        Assert.Null(r.View(null).SelfCheck.Suggestion);                 // 還在 reviewing
        r.ReviewPassed(1);
        Assert.Equal(new[] { "註記" }, r.View(null).SelfCheck.Suggestion!.Notes);
    }
```

`src/PromptCopilot.Api.Tests/Rendering/RenderPipelineTests.cs`：
- 在 `Hair` 欄位下面加：

```csharp
    private static readonly RequirementVerdict HairUnmet = new("r1", "銀色長髮", RequirementSources.User, new[] { "silver hair" }, Array.Empty<string>(), "unmet", "not_rendered", "短髮");
```

- `Review_on_runs_generating_reviewing_self_checking_done` 加三行斷言（放在 audit 斷言旁）：

```csharp
        Assert.Contains("\"seed\":42", e.PayloadJson);
        Assert.Contains("\"reroll\":false", e.PayloadJson);
        Assert.Contains("\"suggestion\":\"none\"", e.PayloadJson);
```

- 檔尾加：

```csharp
    /// <summary>修正建議設計 §4：用同一段對話其他圖的紀錄算建議。先前另一個 seed 也沒畫出來 → 第二個 seed 就建議改寫。</summary>
    [Fact]
    public async Task Suggestion_uses_earlier_renders_of_the_session()
    {
        var earlier = new RenderRecord("old", RenderRecordTests.Request() with { Seed = 7 }, _queue.TakeDaily(), _owner);
        earlier.MarkGenerating(0); earlier.ImageArrived(Png, 1, 1); earlier.ReviewPassed(1);
        earlier.SelfCheckFinished(SelfCheckScore.Build(new[] { HairUnmet }, "k1", false), 1);
        _owner.Renders[earlier.Id] = earlier;
        _reviewer.Gate.SetResult(Clean);
        _checker.Gate.SetResult(new[] { HairUnmet });
        var r = await Dequeued();
        await Pipeline().ProcessAsync(r, default);
        var s = r.SelfCheck!.Suggestion!;
        Assert.Equal((SuggestionKinds.RewriteTags, new[] { "r1" }), (s.Kind, s.ItemIds.ToArray()));
        var e = Assert.Single(_audit.Entries);
        Assert.Contains("\"suggestion\":\"rewrite_tags\"", e.PayloadJson);
        Assert.Contains("\"suggestionItems\":1", e.PayloadJson);
    }

    [Fact]
    public async Task Unavailable_scoring_has_no_suggestion()
    {
        _extractor.Open = false;
        _extractor.Gate.SetException(new InvalidOperationException("bad json"));
        _reviewer.Gate.SetResult(Clean);
        var r = await Dequeued();
        await Pipeline().ProcessAsync(r, default);
        Assert.Null(r.SelfCheck);
        Assert.Contains("\"suggestion\":null", Assert.Single(_audit.Entries).PayloadJson);
    }

    /// <summary>Review Focus 4：其他圖的紀錄壞掉讓 FixAdvisor 丟例外：分數與圖照給，只是沒有建議。</summary>
    [Fact]
    public async Task Advice_failure_leaves_the_score_and_image()
    {
        var broken = new RenderRecord("old", RenderRecordTests.Request() with { Seed = 7 }, _queue.TakeDaily(), _owner);
        broken.MarkGenerating(0); broken.ImageArrived(Png, 1, 1); broken.ReviewPassed(1);
        broken.SelfCheckFinished(new SelfCheckResult(0, "壞的", null!, "k1", false), 1);
        _owner.Renders[broken.Id] = broken;
        _reviewer.Gate.SetResult(Clean);
        _checker.Gate.SetResult(new[] { HairUnmet });
        var r = await Dequeued();
        await Pipeline().ProcessAsync(r, default);
        Assert.Equal((RenderStatus.Done, SelfCheckStatus.Ok, (int?)0), (r.Status, r.SelfCheckState, r.SelfCheck!.Score));
        Assert.Null(r.SelfCheck.Suggestion);
        Assert.Equal(Png, r.Image);
    }
```

`src/PromptCopilot.Api.Tests/Endpoints/RenderEndpointTests.cs` 檔尾加：

```csharp
    [Fact]
    public async Task Normal_render_uses_the_current_seed_and_shows_seed_and_suggestion()
    {
        await using var f = Factory();
        var c = f.CreateClient();
        var s = Finalized(f);
        s.RenderSeed = 1234;
        var post = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4 });
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("renderId").GetString();
        var done = await WaitFor(c, $"/api/sessions/{s.Id}/renders/{id}", "done");
        Assert.Equal(1234, done.GetProperty("seed").GetInt64());
        Assert.Equal("none", done.GetProperty("selfCheck").GetProperty("suggestion").GetProperty("kind").GetString());
        Assert.Equal(1234, s.RenderSeed);
    }

    [Fact]
    public async Task Reroll_uses_an_unused_seed_and_keeps_it_once_accepted()
    {
        await using var f = Factory();
        var c = f.CreateClient();
        var s = Finalized(f);
        var before = s.RenderSeed;
        var post = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4, reroll = true });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("renderId").GetString()!;
        var done = await WaitFor(c, $"/api/sessions/{s.Id}/renders/{id}", "done");
        var seed = done.GetProperty("seed").GetInt64();
        Assert.NotEqual(before, seed);
        Assert.Equal(seed, s.RenderSeed);
        Assert.True(s.Renders[id].Request.Reroll);
    }

    [Fact]
    public async Task Refused_reroll_keeps_the_current_seed()
    {
        await using var f = Factory(new() { ["Render:PerSessionLimit"] = "1" });
        var c = f.CreateClient();
        var s = Finalized(f);
        s.Renders["used"] = RenderRecordTests.New(id: "used");
        s.Renders["used"].MarkSubmitted("j"); s.Renders["used"].Fail(RenderMessages.Failed, "runpod", null);
        var before = s.RenderSeed;
        var r = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4, reroll = true });
        Assert.Equal((HttpStatusCode)429, r.StatusCode);
        Assert.Equal(before, s.RenderSeed);
    }

    /// <summary>Review Focus 1：收了件、卻在補審 prompt 時就被擋下——圖沒生出來，目前的 seed 不能換掉。</summary>
    [Fact]
    public async Task Reroll_blocked_at_prompt_review_keeps_the_current_seed()
    {
        await using var f = Factory();
        var c = f.CreateClient();
        var s = Finalized(f, reviewed: false);
        ((FakeChatCompletion)f.Services.GetRequiredService<IChatCompletionService>())
            .Then(FakeChatCompletion.Text("""{"nsfw":true,"realPerson":false,"personName":null,"wantsAutoComplete":false,"reason":"裸露"}"""));
        var before = s.RenderSeed;
        var post = await c.PostAsJsonAsync($"/api/sessions/{s.Id}/renders", new { turnIndex = 4, reroll = true });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("renderId").GetString()!;
        Assert.Equal(RenderStatus.Blocked, s.Renders[id].Status);
        Assert.Equal(before, s.RenderSeed);
    }
```

`src/PromptCopilot.Api.Tests/Endpoints/EndpointTests.cs`：在 `Render_status_description_documents_the_intent_fit_format` 下面加：

```csharp
    /// <summary>修正建議設計 §5、§6：POST 多了 reroll，GET 多了 seed 與 selfCheck.suggestion。</summary>
    [Fact]
    public async Task Render_descriptions_document_reroll_seed_and_suggestion()
    {
        var doc = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/swagger/v1/swagger.json");
        var paths = doc.GetProperty("paths");
        var post = paths.GetProperty("/api/sessions/{id}/renders").GetProperty("post").GetProperty("description").GetString();
        Assert.Contains("reroll", post);
        var get = paths.GetProperty("/api/sessions/{id}/renders/{renderId}").GetProperty("get").GetProperty("description").GetString();
        foreach (var field in new[] { "`seed`", "suggestion", "fix_prompt", "rewrite_tags", "reroll", "itemIds", "notes" })
            Assert.Contains(field, get);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release`
Expected: 編譯失敗（`RenderSeed` 唯讀、`Request.Reroll`、`SelfCheckView.Suggestion`、`RenderRequest with { Seed = … }` 等）。

- [ ] **Step 3: `Session.RenderSeed` 可寫**

`src/PromptCopilot.Api/Sessions/Session.cs` 第 62–63 行換成：

```csharp
    /// <summary>生成預覽「目前的 seed」：建 session 時隨機一次；一般生圖（含改完 prompt 再生）都用它，前後兩張的差異才看得出是 prompt 造成的。
    /// 只有「換 seed 重生」收件成功時，端點才把它換成新的（修正建議設計 §5）。只有生圖端點讀寫；同一段對話一次只有一張預覽在跑，不會搶寫。
    /// 不進 SessionSnapshot：對話回滾碰不到它。</summary>
    public long RenderSeed { get; set; } = Random.Shared.NextInt64(1, int.MaxValue);
```

- [ ] **Step 4: `RenderRecord.cs`**

1. `RenderRequest` 換成：

```csharp
/// <summary>收件時的快照（預覽設計 §4、§5.1）：之後使用者再改設定也不影響這張圖。TurnIndex 是哪張定稿卡；
/// 之後自主閉環是哪一輪（設計 §12）。PromptReviewed：定稿當時有沒有經過輸出審查（FinalPrompt.Reviewed）。
/// Intent：對話整理與快照的鍵；ReusedRequirements：鍵跟 session 上的快照相同時，那份清單（符合度設計 §4.3）。
/// Reroll：使用者按「換 seed 重生」生的（修正建議設計 §5），只進 audit。</summary>
public sealed record RenderRequest(string SessionId, int TurnIndex, string Positive, string Negative, long Seed,
    IntentInput Intent, bool SafetyOn, bool PromptReviewed, IReadOnlyList<Requirement>? ReusedRequirements = null, bool Reroll = false);
```

2. view 的 record 換成：

```csharp
public sealed record SelfCheckView(string Status, int? Score, string? Summary, IReadOnlyList<RequirementVerdict> Items, FixSuggestion? Suggestion);
public sealed record RenderTimingsView(int? QueueMs, int? DelayMs, int? ExecutionMs, int? ReviewMs, int? RequirementsMs, int? SelfCheckMs);
public sealed record RenderView(string RenderId, int TurnIndex, string Status, int? Position, string Safety, string? Message,
    SelfCheckView SelfCheck, RenderTimingsView Timings, long Seed);
```

3. `View` 方法裡建 `SelfCheckView` 與 `RenderView` 的那段換成：

```csharp
            return new RenderView(id, request.TurnIndex, RenderWire.Status(status), status == RenderStatus.Queued ? position : null,
                request.SafetyOn ? "on" : "off", _message,
                new SelfCheckView(RenderWire.SelfCheck(_selfCheckState), sc?.Score, sc?.Summary, sc?.Items ?? Array.Empty<RequirementVerdict>(), sc?.Suggestion),
                new RenderTimingsView(QueueMs, DelayMs, ExecutionMs, ReviewMs, RequirementsMs, SelfCheckMs), request.Seed);
```

（上一行註解「分數、說明、清單都等 done 才給」改成「分數、說明、清單、建議都等 done 才給」。）

- [ ] **Step 5: `RenderPipeline.cs`**

1. `SelfCheckAsync` 裡：

```csharp
            r.SelfCheckFinished(SelfCheckScore.Build(verdicts, r.Request.Intent.Key, outcome.Reused), Ms(t));
```

換成：

```csharp
            var result = SelfCheckScore.Build(verdicts, r.Request.Intent.Key, outcome.Reused);
            r.SelfCheckFinished(result with { Suggestion = Advise(r, result) }, Ms(t));
```

2. 在 `SelfCheckAsync` 下面加：

```csharp
    /// <summary>修正建議（修正建議設計 §4、§7）：用同一段對話其他評分 ok 的圖算「換了幾個 seed」。只是建議：算不出來就沒有，分數與圖照給。</summary>
    private FixSuggestion? Advise(RenderRecord r, SelfCheckResult result)
    {
        try
        {
            var others = (r.Owner?.Renders.Values ?? Enumerable.Empty<RenderRecord>())
                .Where(o => o.Id != r.Id)
                .Select(o => (o, sc: o.SelfCheck))
                .Where(x => x.sc is not null)
                .Select(x => new PastRender(x.o.Request.Seed, x.o.Request.Positive, x.o.Request.Negative, x.sc!))
                .ToList();
            return FixAdvisor.Advise(result, r.Request.Seed, r.Request.Positive, r.Request.Negative, others, options.Value.SeedsBeforeRewrite);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "fix advice failed for render {RenderId}", r.Id);
            return null;
        }
    }
```

3. `WrapUpAsync` 的 payload：`renderId = r.Id, …` 那一段在 `jobId = r.RunPodJobId,` 後面加 `seed = r.Request.Seed, reroll = r.Request.Reroll,`；在 `selfCheck = …,` 後面加一行：

```csharp
            suggestion = r.SelfCheck?.Suggestion?.Kind, suggestionItems = r.SelfCheck?.Suggestion?.ItemIds.Count,
```

- [ ] **Step 6: `RenderEndpoints.cs`**

1. `RenderPostRequest` 換成：

```csharp
public sealed record RenderPostRequest(int TurnIndex, string? Safety = null, bool Reroll = false);
```

2. POST handler：在 `RenderRequest request;` 下面加 `long seed;`；鎖裡建 request 的那段換成：

```csharp
                // 換 seed 重生：挑一個這段對話沒用過的 seed；一般生圖（含改完 prompt 再生）用目前的 seed（修正建議設計 §5）
                seed = req.Reroll ? NewSeed(s) : s.RenderSeed;
                request = new RenderRequest(s.Id, req.TurnIndex, s.LastFinal.Positive, s.LastFinal.Negative, seed,
                    intent, safetyOn.Value, s.LastFinal.Reviewed, reused, req.Reroll);
```

3. 收件那段換成：

```csharp
            var admission = await renders.RequestAsync(s, request, ct);
            // 收件成功、而且不是補審就被擋下（圖根本沒生），才把新 seed 寫回成目前的 seed；被擋下時維持原本的
            if (req.Reroll && admission is RenderAdmission.Accepted { Record.Status: not RenderStatus.Blocked }) s.RenderSeed = seed;
            return admission switch
            {
                RenderAdmission.Accepted a => Results.Accepted($"/api/sessions/{s.Id}/renders/{a.Record.Id}", new RenderCreated(a.Record.Id)),
                RenderAdmission.Rejected r => Results.Json(new ErrorBody(r.Error), statusCode: r.StatusCode),
                _ => throw new InvalidOperationException("unknown admission"),
            };
```

4. class 裡（`Map` 方法外）加：

```csharp
    /// <summary>這段對話還沒用過的 seed（含目前的那個），換了才看得到不同的畫面。</summary>
    private static long NewSeed(Session s)
    {
        var used = s.Renders.Values.Select(r => r.Request.Seed).Append(s.RenderSeed).ToHashSet();
        long seed;
        do seed = Random.Shared.NextInt64(1, int.MaxValue); while (used.Contains(seed));
        return seed;
    }
```

5. POST 的 `WithDescription` 第一段換成：

```
            body：`{"turnIndex": 4, "safety": "on" | "off", "reroll": false}`。`turnIndex` 必須是最後一次定稿的那輪（`GET /api/sessions/{id}` 的 `lastFinal.turnIndex`）；`safety` 可省略（預設 `on`），`off` 要後端開 `Safety:AllowDisable`；`reroll` 可省略（預設 `false`），`true` 是「換 seed 重生」：用一個這段對話沒用過的 seed，收件成功才成為之後生圖用的 seed（docs/superpowers/specs/2026-10-10-fix-suggestions-design.md §5）。一般生圖用目前的 seed。
```

6. GET 的 `WithDescription`：在 `selfCheck` 那段（以「`selfCheck`：使用者想法符合度評分」開頭的那行）後面加一行：

```
            `selfCheck.suggestion`：修正建議（fix-suggestions-design.md §4、§6），`{kind: fix_prompt | rewrite_tags | reroll | none, text, message, itemIds, notes}`；`text` 是給使用者看的一行（`none` 時 `null`），`message` 是按下按鈕要送給助理的話（只有 `fix_prompt`、`rewrite_tags` 有），`notes` 是註記。跟 `score` 一樣只在 `done` 而且評分 `ok` 時有。`seed` 是這張用的 seed。
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test src/PromptCopilot.Api.Tests -c Release`
Expected: 全部 PASS。

- [ ] **Step 8: 確認沒有漏掉的呼叫端**

Run: `grep -rn "new RenderView(\|new SelfCheckView(\|new RenderPostRequest(" src --include=*.cs`
Expected: 只出現在 `RenderRecord.cs`（與測試）；都已帶上新欄位（編譯過就代表都改了）。

- [ ] **Step 9: 同步單輪流程說明**

`docs/單輪流程說明.md` 第 452 行，在 `評分只顯示、不改提示詞（[符合度評分設計](superpowers/specs/2026-10-10-intent-fit-scoring-design.md)）。` 後面接：

`評分完成時程式另外給一個修正建議：有要求 prompt 沒寫就建議請助理補上（seed 不變）；只剩沒畫出來的就建議換 seed 重生；同一條換了 2 個 seed 都畫不出來就建議改寫那個 tag。使用者按下按鈕才做——改 prompt 的建議當成一般訊息送給助理、照常跳確認卡，換 seed 直接重生（[修正建議設計](superpowers/specs/2026-10-10-fix-suggestions-design.md)）。`

（先 `sed -n 452p docs/單輪流程說明.md` 確認原文。）

- [ ] **Step 10: Commit**

```bash
git add src/PromptCopilot.Api src/PromptCopilot.Api.Tests docs/單輪流程說明.md
git commit -m "feat(api): attach fix suggestions to scored previews and let users reroll the seed"
```

---

### Task 3: 前端

**Files:**
- Modify: `src/PromptCopilot.Frontend/types/api.ts`
- Modify: `src/PromptCopilot.Frontend/lib/render.ts`
- Modify: `src/PromptCopilot.Frontend/lib/renderSlot.ts`
- Modify: `src/PromptCopilot.Frontend/composables/useApi.ts:82`
- Modify: `src/PromptCopilot.Frontend/stores/session.ts`
- Modify: `src/PromptCopilot.Frontend/components/RenderPreview.vue`
- Test: `src/PromptCopilot.Frontend/tests/render.test.ts`
- Test: `src/PromptCopilot.Frontend/tests/renderSlot.test.ts`
- Test: `src/PromptCopilot.Frontend/tests/renderPoll.test.ts`（只改 fixture）

**Interfaces:**
- Consumes: Task 2 的對外格式（`seed`、`selfCheck.suggestion`、POST `reroll`）。
- Produces:
  - `type SuggestionKind = 'fix_prompt' | 'rewrite_tags' | 'reroll' | 'none'`、`interface SuggestionView { kind; text: string | null; message: string | null; itemIds: string[]; notes: string[] }`
  - `suggestionButton(o: { kind: SuggestionKind; isLatest: boolean; busy: boolean; inFlight: boolean; sent: boolean }) → SuggestionButton`
  - `RenderSlot.fixSent`、`markFixSent(prev)`
  - store：`requestRender(turnIndex, opts?: { reroll?: boolean })`、`followSuggestion(turnIndex)`

- [ ] **Step 1: Write the failing tests**

三個測試檔的 `RenderView` fixture（`render.test.ts` 的 `view`、`renderPoll.test.ts` 與 `renderSlot.test.ts` 的 `view`）：`selfCheck: { status: 'pending', score: null, summary: null, items: [] }` 改成 `selfCheck: { status: 'pending', score: null, summary: null, items: [], suggestion: null }`；並在 `timings: { … }` 後面加 `, seed: 42`（`render.test.ts` 的 fixture 是多行物件，加一行 `seed: 42,`）。

`tests/render.test.ts`：import 加 `suggestionButton`；檔尾加：

```ts
describe('suggestionButton', () => {
  const base = { kind: 'fix_prompt' as const, isLatest: true, busy: false, inFlight: false, sent: false }

  it('labels by kind and hides for none or old cards', () => {
    expect(suggestionButton(base)).toEqual({ visible: true, label: '請助理修改', disabled: false, note: null })
    expect(suggestionButton({ ...base, kind: 'rewrite_tags' })).toEqual({ visible: true, label: '請助理修改', disabled: false, note: null })
    expect(suggestionButton({ ...base, kind: 'reroll' })).toEqual({ visible: true, label: '換 seed 重生', disabled: false, note: null })
    expect(suggestionButton({ ...base, kind: 'none' })).toEqual({ visible: false })
    expect(suggestionButton({ ...base, isLatest: false })).toEqual({ visible: false })
  })

  // Review Focus 5：按過一次、對話在跑、預覽還沒結束都不能再送
  it('disables while busy, while a preview runs, and after the fix was sent', () => {
    expect(suggestionButton({ ...base, sent: true })).toEqual({ visible: true, label: '請助理修改', disabled: true, note: '已送出修正' })
    expect(suggestionButton({ ...base, inFlight: true })).toEqual({ visible: true, label: '請助理修改', disabled: true, note: '上一張還在生成' })
    expect(suggestionButton({ ...base, busy: true })).toEqual({ visible: true, label: '請助理修改', disabled: true, note: null })
    expect(suggestionButton({ ...base, kind: 'reroll', sent: true })).toEqual({ visible: true, label: '換 seed 重生', disabled: false, note: null })
  })
})
```

`tests/renderSlot.test.ts`：import 加 `markFixSent`；在 `describe('render slot', …)` 裡加：

```ts
  it('remembers a sent fix until a new preview replaces the slot', () => {
    const sent = markFixSent(done())
    expect(sent.fixSent).toBe(true)
    expect(beginRequest(sent).fixSent).toBe(true)
    expect(requestAccepted('r2').fixSent).toBe(false)
    expect(resumed('r1').fixSent).toBe(false)
  })
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/PromptCopilot.Frontend && npm test`
Expected: `render.test.ts`、`renderSlot.test.ts` 失敗（`suggestionButton`、`markFixSent` 不存在）。

- [ ] **Step 3: 型別、lib、renderSlot**

`types/api.ts`：在 `SelfCheckItemView` 的 interface 後面加：

```ts
export type SuggestionKind = 'fix_prompt' | 'rewrite_tags' | 'reroll' | 'none'
/** 修正建議（修正建議設計 §6）。text：給使用者看的一行（none 時 null）；message：按下按鈕送給助理的話（只有 fix_prompt、rewrite_tags）。 */
export interface SuggestionView { kind: SuggestionKind; text: string | null; message: string | null; itemIds: string[]; notes: string[] }
```

`RenderView` 的 `selfCheck` 型別加 `; suggestion: SuggestionView | null`；`timings` 那行後面（interface 內）加一行 `seed: number`。

`lib/render.ts`：import 加 `SuggestionKind`；檔尾加：

```ts
export type SuggestionButton = { visible: false } | { visible: true; label: string; disabled: boolean; note: string | null }

/** 修正建議的按鈕（修正建議設計 §8）：只在最新的定稿卡；「請助理修改」按過一次就停用（只記在前端，重新整理後恢復）。 */
export function suggestionButton(o: { kind: SuggestionKind; isLatest: boolean; busy: boolean; inFlight: boolean; sent: boolean }): SuggestionButton {
  if (o.kind === 'none' || !o.isLatest) return { visible: false }
  const label = o.kind === 'reroll' ? '換 seed 重生' : '請助理修改'
  if (o.kind !== 'reroll' && o.sent) return { visible: true, label, disabled: true, note: '已送出修正' }
  if (o.inFlight) return { visible: true, label, disabled: true, note: '上一張還在生成' }
  if (o.busy) return { visible: true, label, disabled: true, note: null }
  return { visible: true, label, disabled: false, note: null }
}
```

`lib/renderSlot.ts`：
- `RenderSlot` interface 加一行 `fixSent: boolean`，註解（在 interface 上方那段說明最後）加一句：`fixSent：這張的「請助理修改」已經按過（修正建議設計 §8），換成新的一張就重設。`
- `EMPTY` 加 `fixSent: false`。
- 檔尾加：

```ts
/** 按了「請助理修改」：同一張的建議不再送第二次。 */
export function markFixSent(prev: RenderSlot): RenderSlot {
  return { ...prev, fixSent: true }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd src/PromptCopilot.Frontend && npm test`
Expected: PASS（全部）。

- [ ] **Step 5: API、store、元件**

`composables/useApi.ts` 第 82 行的簽章換成：

```ts
  async function requestRender(id: string, body: { turnIndex: number; safety?: 'off'; reroll?: boolean }): Promise<RenderRequestResult> {
```

`stores/session.ts`：
- import 從 `../lib/renderSlot` 的那行加 `markFixSent`（先 `grep -n "lib/renderSlot" stores/session.ts` 找到那行）。
- `async function requestRender(turnIndex: number) {` 換成 `async function requestRender(turnIndex: number, opts: { reroll?: boolean } = {}) {`；其中 `messageBody({ turnIndex }, …)` 換成 `messageBody(opts.reroll ? { turnIndex, reroll: true } : { turnIndex }, …)`。
- 在 `requestRender` 函式後面加：

```ts
  /** 照修正建議做（修正建議設計 §8）：改 prompt 類當成一般訊息送給助理（照常跳確認卡），換 seed 直接重生。 */
  async function followSuggestion(turnIndex: number) {
    const slot = renders.value[turnIndex]
    const sg = slot?.view?.selfCheck.suggestion
    if (!slot || !sg || turnIndex !== latestFinalizedTurn.value || busy.value || renderInFlight.value) return
    if (sg.kind === 'reroll') { await requestRender(turnIndex, { reroll: true }); return }
    if ((sg.kind === 'fix_prompt' || sg.kind === 'rewrite_tags') && sg.message && !slot.fixSent) {
      setRender(turnIndex, markFixSent(slot))
      await runTurn(sg.message, { text: sg.message })
    }
  }
```

- `return { … }` 裡 `requestRender,` 後面加 `followSuggestion,`。

`components/RenderPreview.vue`：
- `<img …>` 那行後面加：

```vue
        <p class="mt-1 text-[11px] text-muted">seed {{ view.seed }}</p>
```

- `<p class="text-xs text-ink/80">{{ view.selfCheck.summary }}</p>` 後面加：

```vue
          <div v-if="sg && (sg.text || sg.notes.length)" class="mt-2" data-section="suggestion">
            <div v-if="sg.text" class="flex flex-wrap items-center gap-2 text-xs">
              <span class="font-medium">{{ sg.text }}</span>
              <button v-if="fix.visible" type="button" :disabled="fix.disabled"
                      class="rounded-md border border-ink px-2 py-0.5 font-medium hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
                      @click="s.followSuggestion(turnIndex)">
                {{ fix.label }}
              </button>
              <span v-if="fix.visible && fix.note" class="text-muted">{{ fix.note }}</span>
            </div>
            <p v-for="n in sg.notes" :key="n" class="text-xs text-muted">{{ n }}</p>
          </div>
```

- script：import 從 `../lib/render` 加 `suggestionButton`；加：

```ts
const sg = computed(() => view.value?.selfCheck.suggestion ?? null)
const fix = computed(() => suggestionButton({
  kind: sg.value?.kind ?? 'none', isLatest: s.latestFinalizedTurn === props.turnIndex,
  busy: s.busy, inFlight: s.renderInFlight, sent: slot.value?.fixSent ?? false,
}))
```

- [ ] **Step 6: 型別檢查與全部前端測試**

Run: `cd src/PromptCopilot.Frontend && npx nuxi typecheck && npm test`
Expected: typecheck 沒有錯誤；測試全部 PASS。

- [ ] **Step 7: Commit**

```bash
git add src/PromptCopilot.Frontend
git commit -m "feat(frontend): show the fix suggestion with a one-click fix or seed reroll"
```

---

### Task 4: 文件同步

**Files:**
- Modify: `docs/superpowers/specs/2026-10-10-intent-fit-scoring-design.md`（§14）
- Modify: `docs/superpowers/specs/2026-10-09-render-preview-design.md`（§2 seed 那列、§12）
- Modify: `README.md:75`

**Interfaces:** 無（只有文件）。

- [ ] **Step 1: 評分設計 §14**

在 §14 的標題下面第一段（「專案擁有者 2026-10-10 對閉環的初步想法…」）前面加一段：

`**手動版已做（2026-10-10）**：依問題歸類決定下一步的規則已實作成 \`FixAdvisor\`，以手動輔助的形式上線（[修正建議設計](2026-10-10-fix-suggestions-design.md)）：使用者按一下照建議改 prompt 或換 seed。閉環直接沿用同一個 \`FixAdvisor\`，差別只在由誰按。`

- [ ] **Step 2: 預覽設計**

- §2 決定紀錄裡「同一個 session 的 seed」那列的決定欄，在原文後面加：`（2026-10-10 起改成「目前的 seed」：換 seed 重生時才變，見[修正建議設計](2026-10-10-fix-suggestions-design.md) §5）`。
- §12 表格「session 固定 seed、同一個 session 一次一張」那列的右欄，在原文後面加：`；seed 已改成可換（修正建議設計 §5）`。

（先 `grep -n "同一個 session 的 seed\|session 固定 seed" docs/superpowers/specs/2026-10-09-render-preview-design.md` 確認位置。）

- [ ] **Step 3: README**

`README.md` 第 75 行的 `附看圖審查與「使用者想法符合度」評分` 改成 `附看圖審查、「使用者想法符合度」評分與修正建議（按一下請助理補 prompt，或換 seed 重生）`。

- [ ] **Step 4: 檢查連結**

Run: `grep -rn "2026-10-10-fix-suggestions-design.md" docs README.md | grep -v "docs/superpowers/plans/"`
Expected: 評分設計、預覽設計（2 處）、單輪流程說明（Task 2）都有。

- [ ] **Step 5: Commit**

```bash
git add docs README.md
git commit -m "docs: point scoring and preview docs to fix suggestions"
```

---

### Task 5: 實機驗收

需要 `.env` 的 RunPod 與 Gemini 設定；會花幾美分。照評分案 R9–R13 的做法：`docker compose up -d --build api frontend` 重建後，API 用 scratchpad 的 Python 腳本打 `localhost:5000`，畫面用 Playwright（`playwright-core` 驅動系統的 Edge，headless；把完成的 session 與 renderId 放進 sessionStorage 後重新整理），腳本不進 repo。

**Files:**
- Modify: `docs/eval-cases.md`（檔尾加 R14–R17）

**Interfaces:** 無。

- [ ] **Step 1: 重建並確認服務起來**

Run: `docker compose up -d --build api frontend`，再 `curl -s localhost:5000/api/config/render`
Expected: `{"enabled":true}`。

- [ ] **Step 2: 照建議走幾輪**

腳本：開對話 → 定稿 → 生成預覽 → 讀 `selfCheck.suggestion` → `fix_prompt`／`rewrite_tags` 就送 `message`（處理確認卡、定稿）再生成預覽；`reroll` 就 `POST` `reroll: true`；`none` 就停。每段對話最多走 4 步（額度每段 10 張）。跑 3–4 段不同的對話，例如：

- 「一個銀色雙馬尾的少女抱著一隻白貓站在傍晚的海邊，戴圓框眼鏡，動漫風」（細節多，容易有沒畫出來的）
- 「畫一個在咖啡廳看書的女孩，其他都隨便你決定」（追問一律回「這些你決定就好」，看委託註記）
- 「一隻戴著紅色圍巾的柴犬坐在雪地裡，背景有小木屋，水彩風」

每步記下：`seed`、`suggestion.kind`、`text`、`notes`、`score`。

- [ ] **Step 3: 對照預期**

| 編號 | 預期 |
| :--- | :--- |
| R14 | `fix_prompt` 送出的是程式組的那句話；新圖的 `seed` 跟上一張相同；那條要求這次有對應的 tag |
| R15 | 按 `reroll` 後新圖的 `seed` 不同；audit 的 `reroll: true` |
| R16 | 同一條要求在 2 個 seed 下都沒畫出來 → `rewrite_tags` |
| R17 | 委託項目畫錯 → 建議旁邊有註記 |

audit 從 Postgres 讀：`docker compose exec -T db sh -c 'psql -U "${POSTGRES_USER:-postgres}" -d "${POSTGRES_DB:-prompt_copilot}" -At -c "select session_id, payload->>'"'"'seed'"'"', payload->>'"'"'reroll'"'"', payload->>'"'"'suggestion'"'"' from audit_logs where event_type = '"'"'Render_Completed'"'"' order by id desc limit 10;"'`

- [ ] **Step 4: 畫面**

挑一張有建議與註記的，Playwright 讀 `[data-section="suggestion"]`：建議那一行、按鈕文字、註記；按一次「請助理修改」後按鈕停用、旁邊寫「已送出修正」；圖下面有 `seed …`。截圖存 scratchpad。

- [ ] **Step 5: 寫進文件**

`docs/eval-cases.md` 檔尾加一節「2026-10-10 修正建議（R14–R17）」，格式同 R9–R13（說明怎麼跑、表格、觀察）。碰不到的情況寫「實機沒碰到，由 `FixAdvisorTests` 涵蓋」。有沒過的條目先回報給使用者，不要自己改設計。

- [ ] **Step 6: Commit**

```bash
git add docs/eval-cases.md
git commit -m "docs: fix suggestions acceptance"
```
