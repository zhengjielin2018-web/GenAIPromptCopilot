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
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, g.BlockCode!, RawInput: text), ct);
            writer.TryWrite(new BlockedEvent(g.BlockCode!, g.Message!));
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

            stage = "apply";
            if (turn.Outcome is BlockedOutcome blocked) throw new OutputBlockedException(blocked.Reason);
            if (turn.Outcome is null) throw new ProtocolViolationException("LLM 未以終止型 tool 結束本輪");
            if (turn.Outcome is BudgetExhaustedOutcome) throw new ProtocolViolationException("tool 預算耗盡");
            writer.TryWrite(ToFinal(turn.Outcome));
            writer.TryWrite(turn.DimensionsSnapshot());

            HistoryTrimmer.CompressTurn(session.ChatHistory, startIdx);
            HistoryTrimmer.Truncate(session.ChatHistory, options.HistoryTurns);

            // 交易已成立：稽核寫不進去不該把成功的一輪回滾掉
            try
            {
                await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Turn_Completed", version, text,
                    JsonSerializer.Serialize(new { outcome = turn.Outcome.GetType().Name, toolCalls = turn.ToolCalls, rejections = turn.Rejections }, Json),
                    LatencyMs: (int)sw.ElapsedMilliseconds), CancellationToken.None);
            }
            catch (Exception) { /* 由 DB 監控發現；不發事件，免得前端誤出重試按鈕 */ }
        }
        catch (OutputBlockedException e)
        {
            session.Restore(snapshot);
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Blocked_Output", version, text, JsonSerializer.Serialize(new { e.Reason }, Json)), CancellationToken.None);
            writer.TryWrite(new BlockedEvent("Blocked_Output", $"這一輪的輸出被攔截：{e.Reason}。你可以改寫需求後再送。"));
        }
        catch (UpstreamBlockedException e)
        {
            session.Restore(snapshot);
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Blocked_Upstream", version, text, JsonSerializer.Serialize(new { e.Reason, stage }, Json)), CancellationToken.None);
            writer.TryWrite(new BlockedEvent("Blocked_Upstream",
                $"Gemini 判定這次的內容不該生成，已攔截（{e.Reason}）。這不是程式錯誤，也不是知識庫的問題；下一步在你手上——改寫需求或直接再送一次。"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.Restore(snapshot);
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text, JsonSerializer.Serialize(new { stage, errorClass = "ClientDisconnected" }, Json)), CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException)
        {
            session.Restore(snapshot);
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text, JsonSerializer.Serialize(new { stage, errorClass = "Timeout" }, Json)), CancellationToken.None);
            writer.TryWrite(new ErrorEvent("timeout", $"這一輪超過 {options.TurnTimeoutSeconds} 秒沒完成，已取消。可以直接再送一次。"));
        }
        catch (Exception e)
        {
            session.Restore(snapshot);
            await audit.WriteAsync(new AuditEntry(session.Id, turnIndex, "Turn_Failed", version, text,
                JsonSerializer.Serialize(new { stage, errorClass = e.GetType().Name, message = e.Message }, Json)), CancellationToken.None);
            writer.TryWrite(new ErrorEvent("turn_failed", $"這一輪失敗，已還原到送出前的狀態：{e.Message}。可以直接再送一次。"));
        }
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
