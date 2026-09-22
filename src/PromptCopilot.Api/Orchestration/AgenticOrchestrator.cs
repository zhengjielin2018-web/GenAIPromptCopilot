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
    Func<TurnContext, IReadOnlySet<string>, bool, Kernel> kernelFactory) : IPromptOrchestrator
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async IAsyncEnumerable<AgentEvent> RunTurnAsync(Session session, string userMessage, [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        var work = Task.Run(async () =>
        {
            try { await ExecuteAsync(session, userMessage, channel.Writer, ct); }
            finally { channel.Writer.Complete(); }
        }, CancellationToken.None);
        await foreach (var e in channel.Reader.ReadAllAsync(CancellationToken.None)) yield return e;
        await work;   // 讓取消例外浮出來給呼叫端
    }

    internal async Task ExecuteAsync(Session session, string text, ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        var turnIndex = session.TurnIndex + 1;
        writer.TryWrite(new SessionEvent(session.Id, turnIndex, session.Status.ToString()));

        // ① 輸入側：不進 kernel、不計任何東西
        var g = await guard.CheckAsync(text, ct);
        if (g.Blocked)
        {
            writer.TryWrite(new BlockedEvent(g.BlockCode!, g.Message!));
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, g.BlockCode!, RawInput: text));
            return;
        }

        // ② 一輪是一個交易（多輪 §5.6）
        var snapshot = session.Snapshot();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TurnTimeoutSeconds));
        var tct = timeout.Token;
        var sw = Stopwatch.StartNew();
        var stage = "setup";
        string version = "";
        try
        {
            tct.ThrowIfCancellationRequested();
            session.TurnIndex = turnIndex;
            var tools = ToolSetBuilder.Build(session, g.WantsAutoComplete, options);
            if (g.WantsAutoComplete) session.AutoFill = true;
            var turn = new TurnContext(session, turnIndex, g, tools, writer);
            (var systemPrompt, version) = prompts.Build(session, tools);
            EnsureSystemMessage(session.ChatHistory, systemPrompt);
            session.ChatHistory.AddUserMessage(text);
            var startIdx = session.ChatHistory.Count;
            var kernel = kernelFactory(turn, tools, true);

            stage = "loop";
            await CallAsync(turn, kernel, tct);

            if (turn.Outcome is null)
            {
                // 多輪 §5.3：補一則系統提示重試一次
                await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Protocol_Violation", version, text, """{"attempt":1}"""));
                session.ChatHistory.AddSystemMessage("你必須呼叫 AskUser、Discuss、FinalizePrompt 或 RequestSaveConsent 之一來結束這一輪，不要只回純文字。");
                await CallAsync(turn, kernel, tct);
            }
            if (turn.Outcome is null)
            {
                // 只看這一輪：純文字訊息會跨輪留在 history 裡，掃全部會把上一輪的回答當成這一輪的答案
                var lastText = session.ChatHistory.Skip(startIdx)
                    .LastOrDefault(m => m.Role == AuthorRole.Assistant && !string.IsNullOrWhiteSpace(m.Content))?.Content;
                if (tools.Contains(ToolNames.Discuss) && lastText is not null)
                {
                    // 仍為純文字：包成 Discuss（options 空、facetStates 用現值）；走正規 plugin 路徑，DiscussStreak 才會照常累加
                    var current = session.FacetStates.Select(kv => new FacetStateEntry(kv.Key, FacetStateParser.ToWire(kv.Value))).ToArray();
                    new DialogPlugin(turn, catalog, options).Discuss(lastText, current);
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

            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Turn_Completed", version, text,
                JsonSerializer.Serialize(new { outcome = turn.Outcome.GetType().Name, toolCalls = turn.ToolCalls, rejections = turn.Rejections }, Json),
                LatencyMs: (int)sw.ElapsedMilliseconds));
        }
        catch (OutputBlockedException e)
        {
            session.Restore(snapshot);
            writer.TryWrite(new BlockedEvent("Blocked_Output", $"這一輪的輸出被攔截：{e.Reason}。你可以改寫需求後再送。"));
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Blocked_Output", version, text, JsonSerializer.Serialize(new { e.Reason }, Json)));
        }
        catch (UpstreamBlockedException e)
        {
            session.Restore(snapshot);
            writer.TryWrite(new BlockedEvent("Blocked_Upstream",
                $"Gemini 判定這次的內容不該生成，已攔截（{e.Reason}）。這不是程式錯誤，也不是知識庫的問題；下一步在你手上——改寫需求或直接再送一次。"));
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Blocked_Upstream", version, text, JsonSerializer.Serialize(new { e.Reason, stage }, Json)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.Restore(snapshot);
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text, JsonSerializer.Serialize(new { stage, errorClass = "ClientDisconnected" }, Json)));
            throw;
        }
        catch (OperationCanceledException)
        {
            session.Restore(snapshot);
            writer.TryWrite(new ErrorEvent("timeout", $"這一輪超過 {options.TurnTimeoutSeconds} 秒沒完成，已取消。可以直接再送一次。"));
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text, JsonSerializer.Serialize(new { stage, errorClass = "Timeout" }, Json)));
        }
        catch (ProtocolViolationException e)
        {
            session.Restore(snapshot);
            writer.TryWrite(new ErrorEvent("protocol_violation", "模型這一輪沒有給出可用的回應，已還原。可以直接再送一次。"));
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text,
                JsonSerializer.Serialize(new { stage, errorClass = nameof(ProtocolViolationException), message = e.Message }, Json)));
        }
        catch (Exception e)
        {
            session.Restore(snapshot);
            writer.TryWrite(new ErrorEvent("turn_failed", $"這一輪失敗，已還原到送出前的狀態：{e.Message}。可以直接再送一次。"));
            await TryAuditAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text,
                JsonSerializer.Serialize(new { stage, errorClass = e.GetType().Name, message = e.Message }, Json)));
        }
    }

    /// <summary>稽核是旁路：寫不進去不該回滾已成立的一輪，也不該吃掉使用者該看到的事件。
    /// 失敗由 DB 監控發現；不另發事件，免得前端誤出重試按鈕。</summary>
    private async Task TryAuditAsync(AuditEntry entry)
    {
        try { await audit.WriteAsync(entry, CancellationToken.None); }
        catch (Exception e) when (e is not OperationCanceledException) { /* 吞掉 */ }
    }

    /// <summary>一次 SK auto-invoke：connector 自己跑 tool、跑 filter，Terminal filter 設 Terminate 就回來。
    /// SK 邊跑邊把 call 與結果寫進 history；最後若是純文字（沒 tool）它不會自己加，這裡補上。</summary>
    private async Task CallAsync(TurnContext turn, Kernel kernel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var settings = new GeminiPromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
        var history = turn.Session.ChatHistory;
        var msg = (await chat.GetChatMessageContentsAsync(history, settings, kernel, ct))[0];
        if (msg.Role == AuthorRole.Assistant && !msg.Items.OfType<FunctionCallContent>().Any() && !string.IsNullOrWhiteSpace(msg.Content))
            history.Add(msg);
    }

    /// <summary>主規格 §4.6：預算耗盡後只掛 FinalizePrompt 再跑一次；kernel 不掛 budget filter，否則第一個 call 又被擋。</summary>
    private async Task ForcedFinalizeAsync(TurnContext turn, CancellationToken ct)
    {
        turn.Outcome = null;
        var kernel = kernelFactory(turn, new HashSet<string> { ToolNames.FinalizePrompt }, false);
        turn.Session.ChatHistory.AddSystemMessage("tool 呼叫預算已用盡。請立即以現有資訊呼叫 FinalizePrompt 定稿；missing 的 facet 留白，不要再檢索。");
        await CallAsync(turn, kernel, ct);
    }

    private static void EnsureSystemMessage(ChatHistory h, string prompt)
    {
        if (h.Count > 0 && h[0].Role == AuthorRole.System) h[0] = new ChatMessageContent(AuthorRole.System, prompt);
        else h.Insert(0, new ChatMessageContent(AuthorRole.System, prompt));
    }

    internal static FinalEvent ToFinal(TurnOutcome o) => o switch
    {
        AskOutcome a => new FinalEvent("ask", Preamble: a.Preamble, Asks: a.Asks),
        MessageOutcome m => new FinalEvent("message", Message: m.Message, Options: m.Options),
        FinalizedOutcome f => new FinalEvent("finalized", Positive: f.Final.Positive, Negative: f.Final.Negative, Tips: f.Final.Tips),
        SaveConsentOutcome => new FinalEvent("save_consent_requested"),
        _ => throw new InvalidOperationException($"無法轉成 final 事件：{o.GetType().Name}"),
    };
}
