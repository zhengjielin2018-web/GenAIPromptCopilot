using System.ComponentModel;
using Microsoft.SemanticKernel;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Orchestration;
using PromptCopilot.Api.Sessions;

namespace PromptCopilot.Api.Plugins;

public sealed class DialogPlugin(TurnContext turn, FacetCatalog catalog, OrchestratorOptions options)
{
    private Session S => turn.Session;

    /// <summary>prompt 要求 20 字；伺服器放寬到 40，模型稍微超過不必白燒一次重叫（設計 §3.2）。</summary>
    public const int MaxChoiceChars = 40;

    [KernelFunction(ToolNames.Confirm)]
    [Description("確認：使用者說了會改動畫面的話（描述題材、回答追問、要求修改、說隨便），先用這個跟他確認；他按下確認卡的按鈕後，下一輪你才能動手。message 寫你理解的畫面或打算怎麼改；要求跟現有內容衝突或有多種解讀時，choices 給 2–4 個解讀讓他選，沒有歧義就省略。")]
    // choices 排在最後而且有預設值：SK 只看「有沒有預設值」決定必填與否（同 Discuss.options）
    public string Confirm(
        [Description("繁中 1–3 句：你理解的畫面，或打算怎麼改；有 choices 時寫成問題")] string message,
        [Description("0 個或 2–4 個解讀，繁中陳述句、20 字以內、彼此互斥；沒有歧義就省略")] string[]? choices = null)
    {
        var text = message?.Trim() ?? "";
        if (text.Length == 0) return Reject("message 不可為空");
        var kept = (choices ?? Array.Empty<string>()).Select(c => c?.Trim() ?? "").Where(c => c.Length > 0).Distinct().ToList();
        if (kept.Count == 1) return Reject("choices 只有 1 個；沒有歧義就省略 choices，有歧義就給 2–4 個");
        if (kept.Count > AskCleaner.MaxOptions) return Reject($"choices 有 {kept.Count} 個，最多 {AskCleaner.MaxOptions} 個");
        if (kept.FirstOrDefault(c => c.Length > MaxChoiceChars) is { } tooLong) return Reject($"choices「{tooLong}」超過 {MaxChoiceChars} 字");
        S.SetPendingConfirmation(new PendingConfirmation(turn.TurnIndex, text, kept, turn.Guard.WantsAutoComplete));
        turn.Outcome = new ConfirmOutcome(text, kept);
        return "ok";
    }

    private string Reject(string reason)
    {
        turn.Rejections.Add($"Confirm：{reason}");
        return $"錯誤：{reason}";
    }

    [KernelFunction(ToolNames.AskUser)]
    [Description("索取：我需要使用者回答才能繼續。還有 facet 缺的維度都要問，只列缺的 facet；一次最多 3 個維度，問不完下一輪再問。每個維度 2–4 個不同方向的選項。")]
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
        MarkOffered(kept.SelectMany(a => a.Options.Select(o => ((string?)a.Dimension, o))));
        turn.Outcome = new AskOutcome(preamble, kept);
        return "ok";
    }

    [KernelFunction(ToolNames.Discuss)]
    [Description("回應：這是我對使用者問題的回答，使用者可以無視它繼續講別的。用於解說、比較、給參考方向。不宣告需求、不卡住流程。不能改 facet 狀態。")]
    // options 排在最後而且有預設值：SK 只看「有沒有預設值」決定必填與否，可為 null 不算；
    // 沒有預設值時 Gemini 照描述省略它會丟 KernelException，白白吃掉一格 tool 預算。
    public string Discuss(
        [Description("繁中回覆")] string message,
        [Description("目前每個 facet 的狀態")] FacetStateEntry[] facetStates,
        [Description("0–4 個參考方向，可省略")] OptionItem[]? options = null)
    {
        // 不分狀態：確認之前不能改畫面，Discuss 帶著改過的狀態等於繞過確認（先確認再動手設計 §3.3）
        if (S.Profile is not null && StatesDiffer(facetStates))
            return "錯誤：Discuss 不能改 facet 狀態；使用者要改畫面時，請用 Confirm 跟他確認";
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
        [Description("繁中一句話（20–40 字）描述使用者這次的需求：題材、主要風格、場景。不含提問與閒聊。會成為共享庫的檢索鍵，要寫成另一個使用者會怎麼描述同樣的需求")] string intentSummary,
        [Description("目前每個 facet 的狀態")] FacetStateEntry[] facetStates)
    {
        if (S.Profile is null) return "錯誤：請先呼叫 SetProfile";
        if (string.IsNullOrWhiteSpace(positivePrompt)) return "錯誤：positivePrompt 不可為空";
        if (string.IsNullOrWhiteSpace(intentSummary)) return "錯誤：intentSummary 不可為空";
        SessionPlugin.Apply(turn, catalog, facetStates);
        if (UnaskedMissing() is { Count: > 0 } missing)
            return $"錯誤：還有 {missing.Count} 個 facet 缺少而且追問額度未用完，請先呼叫 AskUser 追問（一次最多 {options.MaxAsksPerCall} 個維度）：{string.Join(",", missing)}";
        // tag 來源由伺服器比對 ledger 標，不要求模型自述（主規格 §9）
        var positive = positivePrompt.Trim();
        var negative = negativePrompt.Trim();
        S.RecordFinalize(new FinalPrompt(positive, negative, tips.Trim(), intentSummary.Trim(),
            TagAttribution.Attribute(positive, S.Ledger, negative: false, S.Adoptions), TagAttribution.Attribute(negative, S.Ledger, negative: true),
            Reviewed: turn.SafetyOn));
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

    /// <summary>定稿閘門（主規格 §4.6）：AskUser 還在清單上（Collecting、額度沒用完、沒說隨便）而且不是預算用盡的
    /// 強制定稿時，仍為 missing 又沒有委託 note 的 facet。system.md 的追問政策模型不遵守，改成違規的選項不給選（§4.3）。</summary>
    private List<string>? UnaskedMissing()
    {
        if (!turn.Tools.Contains(ToolNames.AskUser) || turn.ForcedFinalize) return null;
        return S.FacetStates.Where(kv => kv.Value == FacetState.Missing && !S.FacetNotes.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
    }

    private bool StatesDiffer(IEnumerable<FacetStateEntry> incoming)
    {
        var applicable = catalog.IdsForProfile(S.Profile!);
        foreach (var e in incoming)
        {
            if (!applicable.Contains(e.FacetId) || !FacetStateParser.TryParse(e.State, out var st)) continue;
            if (turn.TurnStartFacetStates.GetValueOrDefault(e.FacetId) != st) return true;
        }
        return false;
    }

    private void MarkOffered(IEnumerable<(string? dimension, OptionItem option)> offered)
    {
        foreach (var (dim, o) in offered)
            if (o.PresetId is { } id) S.Ledger.MarkOffered(id, new OfferedRef(turn.TurnIndex, dim, o.Label));
    }
}
