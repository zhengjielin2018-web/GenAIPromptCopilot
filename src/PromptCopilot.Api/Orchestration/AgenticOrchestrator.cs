using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using PromptCopilot.Api.Configuration;
using PromptCopilot.Api.Data;
using PromptCopilot.Api.Llm;
using PromptCopilot.Api.Plugins;
using PromptCopilot.Api.Safety;
using PromptCopilot.Api.Sessions;
using PromptCopilot.Api.Streaming;

namespace PromptCopilot.Api.Orchestration;

public sealed class AgenticOrchestrator(
    IChatCompletionService chat,
    FacetCatalog catalog,
    SafetyGuard guard,
    SystemPromptBuilder prompts,
    IAuditSink audit,
    OrchestratorOptions options,
    SafetyClassifier classifier,
    ILogger<AgenticOrchestrator> logger,
    IRecommendationService recommendations,
    Func<TurnContext, IReadOnlySet<string>, bool, Kernel> kernelFactory) : IPromptOrchestrator
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        var work = Task.Run(async () =>
        {
            try { await ExecuteAsync(session, input, channel.Writer, ct); }
            finally { channel.Writer.Complete(); }
        }, CancellationToken.None);
        var drained = false;
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(CancellationToken.None)) yield return e;
            drained = true;
        }
        finally
        {
            // 客戶端中途斷線時，端點的 finally 會立刻放掉 session 鎖。列舉器 dispose 若不等背景這一輪
            // 收完尾（Restore、audit），下一輪就能在還在回滾的 Session 上開跑，兩輪互相踩。
            if (drained) await work;                                    // 正常走完：例外照舊浮到呼叫端
            else { try { await work; } catch (OperationCanceledException) { } }   // 提早離開：只吞取消
        }
    }

    /// <summary>每輪結束時（任何結局）寫一行摘要 log：容器 log 原本看不出一輪發生什麼事（known-issues #7）。</summary>
    private sealed class TurnTrace
    {
        public string Result = "Turn_Failed";
        public string? Detail;
        public TurnContext? Turn;
    }

    internal async Task ExecuteAsync(Session session, TurnInput input, ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        // 這一輪的上游觀察紀錄。要在任何 LLM 呼叫之前放好：輸入分類器（guard）也走同一個 chat client，
        // 它的呼叫一樣經過 GeminiDiagnosticsHandler、一樣算進這一輪。AsyncLocal 的值只往下流，不會漏回呼叫端。
        var upstream = new UpstreamDiagnostics();
        UpstreamDiagnostics.Current = upstream;
        var turnIndex = session.TurnIndex + 1;
        var trace = new TurnTrace();
        var sw = Stopwatch.StartNew();
        try { await ExecuteTurnAsync(session, input, writer, upstream, trace, ct); }
        finally
        {
            logger.LogInformation("Turn {SessionId}#{TurnIndex} {Result} {Detail} tools={ToolCalls} gemini={GeminiCalls} {ElapsedMs} ms",
                session.Id, turnIndex, trace.Result, trace.Detail ?? "-", trace.Turn?.ToolCalls ?? 0, upstream.Calls, sw.ElapsedMilliseconds);
        }
    }

    private async Task ExecuteTurnAsync(Session session, TurnInput input, ChannelWriter<AgentEvent> writer, UpstreamDiagnostics upstream, TurnTrace trace, CancellationToken ct)
    {
        var text = input.Text;
        var turnIndex = session.TurnIndex + 1;
        writer.TryWrite(new SessionEvent(session.Id, turnIndex, session.Status.ToString(), input.Adoption is null ? null : text));

        // 一輪是一個交易（多輪 §5.6）。快照要在 guard 之前取：guard 自己也會丟例外
        // （上游攔截、分類器壞掉），那些一樣要走下面的攔截／失敗路徑，不能整包飛出去。
        var snapshot = session.Snapshot();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TurnTimeoutSeconds));
        var tct = timeout.Token;
        var sw = Stopwatch.StartNew();
        var stage = "guard";
        string version = "";
        try
        {
            // ① 輸入側：不進 kernel、不計任何東西
            var g = await guard.CheckAsync(text, input.SafetyOn, ct);
            if (g.Blocked)
            {
                trace.Result = g.BlockCode!;
                writer.TryWrite(new BlockedEvent(g.BlockCode!, g.Message!));
                await TryAuditAsync(new AuditEntry(session.Id, turnIndex, g.BlockCode!, RawInput: text,
                    PayloadJson: g.BlockDetail is null ? null : JsonSerializer.Serialize(new { term = g.BlockDetail }, Json)));
                return;
            }

            stage = "setup";
            tct.ThrowIfCancellationRequested();
            session.TurnIndex = turnIndex;
            // 採用：快照已取，這裡記的帳失敗時會一起回滾（設計 §9）。TurnIndex 由這裡補，端點不知道輪次。
            if (input.Adoption is { } adoption) session.RecordAdoption(adoption with { TurnIndex = turnIndex }, input.AdoptedPreset!);
            var tools = ToolSetBuilder.Build(session, g.WantsAutoComplete, options);
            if (g.WantsAutoComplete) session.AutoFill = true;
            var turn = new TurnContext(session, turnIndex, g, tools, writer, snapshot.FacetStates) { SafetyOn = input.SafetyOn };
            trace.Turn = turn;
            (var systemPrompt, version) = prompts.Build(session, tools);
            EnsureSystemMessage(session.ChatHistory, systemPrompt);
            session.ChatHistory.AddUserMessage(text);
            var startIdx = session.ChatHistory.Count;
            var kernel = kernelFactory(turn, tools, true);

            stage = "loop";
            var firstText = await CallAsync(turn, kernel, tct);

            if (turn.Outcome is null)
            {
                // 多輪 §5.3：補一則系統提示重試一次
                await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Protocol_Violation", version, text, """{"attempt":1}"""));
                // 第一次的純文字先拿出 history：connector 把 system 訊息全搬進 systemInstruction，contents 就會以這則 model 結尾，
                // Gemini 回 400「Requests ending with a model turn are not supported.」（known-issues #3）。
                // 重試連文字都沒產的話放回原位，下面的包裝才還能用它（只掃 startIdx 之後）。
                var firstAt = firstText is null ? -1 : session.ChatHistory.IndexOf(firstText);
                if (firstAt >= 0) session.ChatHistory.RemoveAt(firstAt);
                var retryText = await CallWithReminderAsync(turn, kernel,
                    "你必須呼叫 AskUser、Discuss、FinalizePrompt 或 RequestSaveConsent 之一來結束這一輪，不要只回純文字。", tct);
                if (firstAt >= 0 && turn.Outcome is null && retryText is null) session.ChatHistory.Insert(firstAt, firstText!);
            }
            if (turn.Outcome is null)
            {
                // 只看這一輪：純文字訊息會跨輪留在 history 裡，掃全部會把上一輪的回答當成這一輪的答案
                var lastText = session.ChatHistory.Skip(startIdx)
                    .LastOrDefault(m => m.Role == AuthorRole.Assistant && !string.IsNullOrWhiteSpace(m.Content))?.Content;
                if (tools.Contains(ToolNames.Discuss) && lastText is not null)
                {
                    // 這條路沒經過 kernel，OutputSafetyFilter 不會跑；但包出來的 message 一樣會送到
                    // 使用者眼前（主規格 §6.2 點名 Discuss.message），所以這裡自己檢一次（審查開關關著就不檢）。
                    var v = turn.SafetyOn ? await classifier.ClassifyOutputAsync(lastText, tct) : null;
                    if (v is { } hit && (hit.Nsfw || hit.RealPerson)) turn.Outcome = new BlockedOutcome(hit.Reason);
                    else
                    {
                        // 仍為純文字：包成 Discuss（options 空、facetStates 用現值）；走正規 plugin 路徑，DiscussStreak 才會照常累加
                        var current = session.FacetStates.Select(kv => new FacetStateEntry(kv.Key, FacetStateParser.ToWire(kv.Value))).ToArray();
                        new DialogPlugin(turn, catalog, options).Discuss(lastText, current);
                    }
                }
                else throw new ProtocolViolationException("LLM 兩次都未以終止型 tool 結束本輪");
            }
            if (turn.Outcome is BudgetExhaustedOutcome)
            {
                await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Tool_Budget_Exhausted", version, text, JsonSerializer.Serialize(new { turn.ToolCalls }, Json)));
                await ForcedFinalizeAsync(turn, tct);
            }

            stage = "apply";
            if (turn.Outcome is BlockedOutcome blocked) throw new OutputBlockedException(blocked.Reason);
            if (turn.Outcome is null or BudgetExhaustedOutcome) throw new ProtocolViolationException("強制定稿後仍無定稿");
            // 事件先算好再修剪：宣告出去的那一刻起，這一輪不能再被任何失敗回滾
            var final = ToFinal(turn.Outcome);
            var dimensions = turn.DimensionsSnapshot();
            HistoryTrimmer.CompressTurn(session.ChatHistory, startIdx);
            HistoryTrimmer.Truncate(session.ChatHistory, options.HistoryTurns);
            writer.TryWrite(final);
            writer.TryWrite(dimensions);
            var recommended = await TryRecommendAsync(session, turn.Outcome, turnIndex, version, text);
            if (recommended is not null) writer.TryWrite(recommended);
            trace.Result = "Turn_Completed";
            trace.Detail = turn.Outcome.GetType().Name;

            // 主規格 §5.1：LLM 挑了哪些 facet 追問、哪些被使用者放掉，要在紀錄裡看得見。
            // 不另開事件（沒有行為掛在上面），寫進這一筆的 payload。
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Turn_Completed", version, text,
                Payload(("retrieval", session.RetrievalMode), ("safety", input.SafetyOn ? "on" : "off"), ("outcome", turn.Outcome.GetType().Name), ("toolCalls", turn.ToolCalls), ("rejections", turn.Rejections),
                    ("askedFacetIds", turn.Outcome is AskOutcome ask ? ask.Asks.SelectMany(a => a.MissingFacetIds).Distinct().ToArray() : null),
                    ("waivedFacetIds", session.FacetStates.Where(kv => kv.Value == FacetState.Waived).Select(kv => kv.Key).ToArray()),
                    ("tagOrigins", turn.Outcome is FinalizedOutcome fin ? TagOrigins(fin.Final.PositiveSources) : null),
                    ("recommendations", recommended is null ? null : (object)new
                    {
                        dimensions = recommended.Dimensions.Select(d => new { dimension = d.Dimension, anchored = d.Anchored, presetIds = d.Sets.Select(x => x.PresetId).ToArray() }).ToArray(),
                    }),
                    ("adoption", input.Adoption is null ? null : (object)new
                    {
                        presetId = input.Adoption.PresetId, dimension = input.Adoption.Dimension,
                        take = input.Adoption.Taken.Keys.ToArray(), filled = input.Adoption.Filled, replaced = input.Adoption.Replaced,
                    })),
                LatencyMs: (int)sw.ElapsedMilliseconds));
        }
        catch (OutputBlockedException e)
        {
            session.Restore(snapshot);
            (trace.Result, trace.Detail) = ("Blocked_Output", e.Reason);
            writer.TryWrite(new BlockedEvent("Blocked_Output", $"這一輪的輸出被攔截：{e.Reason}。你可以改寫需求後再送。"));
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Blocked_Output", version, text, Payload(("reason", e.Reason))));
        }
        catch (UpstreamBlockedException e)
        {
            session.Restore(snapshot);
            // 例外的 reason 是從 connector 的訊息字面猜的（多半只剩 SAFETY）；handler 從回應本文讀到的才可靠
            var block = upstream.Block;
            var reason = block?.Reason ?? e.Reason;
            (trace.Result, trace.Detail) = ("Blocked_Upstream", reason);
            writer.TryWrite(new BlockedEvent("Blocked_Upstream",
                $"Gemini 判定這次的內容不該生成，已攔截（{reason}）。這不是程式錯誤，也不是知識庫的問題；下一步在你手上——改寫需求或直接再送一次。"));
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Blocked_Upstream", version, text,
                Payload(("reason", reason), ("stage", stage), ("attempts", AttemptsOf(e)),
                    ("upstream", block is null ? null : (object)new { kind = block.Kind, reason = block.Reason, safetyRatings = block.SafetyRatings }))));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.Restore(snapshot);
            (trace.Result, trace.Detail) = ("Turn_Failed", "ClientDisconnected");
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text, Payload(("stage", stage), ("errorClass", "ClientDisconnected"))));
            throw;
        }
        catch (OperationCanceledException)
        {
            session.Restore(snapshot);
            (trace.Result, trace.Detail) = ("Turn_Failed", "Timeout");
            writer.TryWrite(new ErrorEvent("timeout", $"這一輪超過 {options.TurnTimeoutSeconds} 秒沒完成，已取消。可以直接再送一次。"));
            // 打了幾次 Gemini、逾時那一刻是不是還有一次卡著沒回來（pendingMs）：分得出是上游慢還是我們自己卡住
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text,
                Payload(("stage", stage), ("errorClass", "Timeout"), ("upstream", Fields(("calls", upstream.Calls), ("pendingMs", upstream.PendingMs))))));
        }
        catch (ProtocolViolationException e)
        {
            session.Restore(snapshot);
            (trace.Result, trace.Detail) = ("Turn_Failed", nameof(ProtocolViolationException));
            writer.TryWrite(new ErrorEvent("protocol_violation", "模型這一輪沒有給出可用的回應，已還原。可以直接再送一次。"));
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text,
                Payload(("stage", stage), ("errorClass", nameof(ProtocolViolationException)), ("message", e.Message))));
        }
        catch (Exception e)
        {
            session.Restore(snapshot);
            (trace.Result, trace.Detail) = ("Turn_Failed", e.GetType().Name);
            // 例外訊息可能帶連線字串、路徑、上游原文：留在 audit 就好，不送到使用者眼前
            writer.TryWrite(new ErrorEvent("turn_failed", "這一輪失敗，已還原到送出前的狀態。可以直接再送一次。"));
            // connector 只留 "400 (Bad Request)"；最近一次 Gemini 回非 2xx 時，把狀態碼與本文一起記下（known-issues #3）
            var http = upstream.HttpError;
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text,
                Payload(("stage", stage), ("errorClass", e.GetType().Name), ("message", e.Message), ("attempts", AttemptsOf(e)),
                    ("upstream", http is null ? null : (object)new { status = http.Status, body = http.Body }))));
        }
    }

    /// <summary>null 的欄位直接不寫進去（主規格 §4.6：attempts 沒有就不要硬塞一個 0）。</summary>
    private static string Payload(params (string Key, object? Value)[] fields) => JsonSerializer.Serialize(Fields(fields), Json);

    /// <summary>同一個省略 null 的規則，給巢狀的物件用。</summary>
    private static Dictionary<string, object?> Fields(params (string Key, object? Value)[] fields) =>
        fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value);

    /// <summary>positive 各來源的 tag 數；四者加總等於 positive 的 tag 數（eval #25）。</summary>
    private static object TagOrigins(IReadOnlyList<TagSource>? sources)
    {
        var s = sources ?? Array.Empty<TagSource>();
        return new
        {
            rag = s.Count(x => x.Origin == TagAttribution.Rag),
            adopted = s.Count(x => x.Origin == TagAttribution.Adopted),
            llm = s.Count(x => x.Origin == TagAttribution.Llm),
            @base = s.Count(x => x.Origin == TagAttribution.Base),
        };
    }

    /// <summary>只有重試層包出來的兩種例外知道自己打了幾次。</summary>
    private static object? AttemptsOf(Exception e) => e switch
    {
        UpstreamBlockedException { Attempts: > 0 } u => u.Attempts,
        UnusableResponseException { Attempts: > 0 } r => r.Attempts,
        _ => null,
    };

    /// <summary>推薦是附加的（設計 §5.1、§9）：final 已宣告出去，推薦失敗或逾時只記 audit，不回滾、不發 error。
    /// 用自己的逾時，不掛在整輪的 token 上——整輪的 token 取消會走回滾路徑。</summary>
    private async Task<RecommendationsEvent?> TryRecommendAsync(Session session, TurnOutcome outcome, int turnIndex, string version, string text)
    {
        if (!session.RetrievalEnabled || outcome is not (AskOutcome or FinalizedOutcome)) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.RecommendationTimeoutSeconds));
        try { return await recommendations.BuildAsync(session, outcome, turnIndex, cts.Token); }
        catch (Exception e)
        {
            logger.LogWarning(e, "recommendations failed for session {SessionId} turn {TurnIndex}", session.Id, turnIndex);
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Recommendation_Failed", version, text,
                Payload(("stage", "recommend"), ("errorClass", e is OperationCanceledException ? "Timeout" : e.GetType().Name))));
            return null;
        }
    }

    /// <summary>稽核是旁路：寫不進去不該回滾已成立的一輪，也不該吃掉使用者該看到的事件。
    /// 失敗由 DB 監控發現；不另發事件，免得前端誤出重試按鈕。</summary>
    private async Task TryAuditAsync(AuditEntry entry)
    {
        try { await audit.WriteAsync(entry, CancellationToken.None); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "audit write failed: {EventType} session {SessionId} turn {TurnIndex}", entry.EventType, entry.SessionId, entry.TurnIndex);
        }
    }

    /// <summary>一次 SK auto-invoke：connector 自己跑 tool、跑 filter，Terminal filter 設 Terminate 就回來。
    /// SK 邊跑邊把 call 與結果寫進 history；最後若是純文字（沒 tool）它不會自己加，這裡補上，並回傳補上的那則（沒補就是 null）。</summary>
    private async Task<ChatMessageContent?> CallAsync(TurnContext turn, Kernel kernel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = new GeminiPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
        var history = turn.Session.ChatHistory;
        var msg = (await chat.GetChatMessageContentsAsync(history, settings, kernel, ct))[0];
        if (msg.Role != AuthorRole.Assistant || msg.Items.OfType<FunctionCallContent>().Any() || string.IsNullOrWhiteSpace(msg.Content)) return null;
        history.Add(msg);
        return msg;
    }

    /// <summary>帶一則只給這次呼叫看的系統提示。connector 把 history 裡每一則 system 訊息（不管位置）都併進 systemInstruction，
    /// 留著的話之後每一輪都會送出去，直到 Truncate 剪掉那一輪（known-issues #3）。呼叫完就拿掉；system 在線上跟位置無關，拿掉不影響其他訊息。
    /// 不改成 user 訊息：Truncate 以 user 訊息數輪次，多一則會從一輪的中間剪。</summary>
    private async Task<ChatMessageContent?> CallWithReminderAsync(TurnContext turn, Kernel kernel, string reminder, CancellationToken ct)
    {
        var history = turn.Session.ChatHistory;
        var note = new ChatMessageContent(AuthorRole.System, reminder);
        history.Add(note);
        try { return await CallAsync(turn, kernel, ct); }
        finally { history.Remove(note); }
    }

    /// <summary>主規格 §4.6：預算耗盡後只掛 FinalizePrompt 再跑一次；kernel 不掛 budget filter，否則第一個 call 又被擋。</summary>
    private async Task ForcedFinalizeAsync(TurnContext turn, CancellationToken ct)
    {
        turn.Outcome = null;
        turn.ForcedFinalize = true;          // 定稿閘門放行：只剩 FinalizePrompt，擋下去這一輪就沒有出口
        var kernel = kernelFactory(turn, new HashSet<string> { ToolNames.FinalizePrompt }, false);
        await CallWithReminderAsync(turn, kernel, "tool 呼叫預算已用盡。請立即以現有資訊呼叫 FinalizePrompt 定稿，不要再檢索。facetStates 依使用者原話標記：使用者講過的 facet 標 covered，真的沒講的才是 missing，其餘 missing 的 facet 留白。", ct);
    }

    private static void EnsureSystemMessage(ChatHistory h, string prompt)
    {
        if (h.Count > 0 && h[0].Role == AuthorRole.System) h[0] = new ChatMessageContent(AuthorRole.System, prompt);
        else h.Insert(0, new ChatMessageContent(AuthorRole.System, prompt));
    }

    public static FinalEvent ToFinal(TurnOutcome o) => o switch
    {
        AskOutcome a => new FinalEvent("ask", Preamble: a.Preamble, Asks: a.Asks),
        MessageOutcome m => new FinalEvent("message", Message: m.Message, Options: m.Options),
        FinalizedOutcome f => new FinalEvent("finalized", Positive: f.Final.Positive, Negative: f.Final.Negative, Tips: f.Final.Tips, IntentSummary: f.Final.IntentSummary,
            PositiveSources: f.Final.PositiveSources ?? Array.Empty<TagSource>(), NegativeSources: f.Final.NegativeSources ?? Array.Empty<TagSource>()),
        SaveConsentOutcome => new FinalEvent("save_consent_requested"),
        _ => throw new InvalidOperationException($"無法轉成 final 事件：{o.GetType().Name}"),
    };
}
