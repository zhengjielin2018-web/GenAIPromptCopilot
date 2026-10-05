using System.Diagnostics;
using System.Text.Json.Serialization;

namespace PromptCopilot.Api.Llm;

/// <summary>Gemini 的一筆安全評分，照 Gemini 送的原樣收；缺的欄位是 null，寫 audit 時省略。</summary>
public sealed record UpstreamSafetyRating(
    [property: JsonPropertyName("category"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Category,
    [property: JsonPropertyName("probability"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Probability,
    [property: JsonPropertyName("blocked"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Blocked);

/// <summary>上游攔截。Kind：<c>input_blocked</c>（<c>promptFeedback.blockReason</c>，輸入整個被拒）或
/// <c>output_blocked</c>（<c>candidates[0].finishReason</c> 屬內容攔截，輸出被截）。</summary>
public sealed record UpstreamBlock(string Kind, string Reason, IReadOnlyList<UpstreamSafetyRating> SafetyRatings);

/// <summary>非 2xx 的回應：狀態碼與本文（截到約 2 KB）。</summary>
public sealed record UpstreamHttpError(int Status, string Body);

/// <summary>一輪之內對 Gemini generateContent 的觀察紀錄（known-issues #7）。
/// connector 碰到攔截只丟一句 "Prompt was blocked due to Gemini API safety reasons."、碰到 400 只留狀態碼，
/// 真正的原因在回應本文裡；<see cref="GeminiDiagnosticsHandler"/> 在 HTTP 層讀本文，記到這裡給 orchestrator 寫 audit。
/// AgenticOrchestrator 每輪開頭放一個新的進 <see cref="Current"/>；AsyncLocal 讓同一條 async 流程上的 handler 找得到它，
/// 物件本身是共用的，handler 改的東西 orchestrator 看得到。輸入／輸出分類器走同一個 chat client，所以它們的呼叫也算在內。
/// Block 與 HttpError 只反映「最近一次收到回應的呼叫」：重試過了，前一次的 400 或攔截不該冒充這一輪的原因。
/// 同一輪的呼叫是依序的，鎖只是保險。</summary>
public sealed class UpstreamDiagnostics
{
    private static readonly AsyncLocal<UpstreamDiagnostics?> Slot = new();

    /// <summary>目前這條 async 流程所屬那一輪的紀錄；不在一輪裡（embedding、測試）就是 null，handler 什麼都不記。</summary>
    public static UpstreamDiagnostics? Current
    {
        get => Slot.Value;
        set => Slot.Value = value;
    }

    private readonly object _gate = new();
    private int _calls;
    private long? _inFlightSince;
    private UpstreamBlock? _block;
    private UpstreamHttpError? _httpError;
    private int _toolNameRepairs;
    private readonly List<string> _undeclared = new();

    /// <summary>這一輪打出去幾次 generateContent（含重試、含分類器）。</summary>
    public int Calls { get { lock (_gate) return _calls; } }

    /// <summary>還沒回來的那一次已經等了多久；沒有在途的呼叫就是 null。</summary>
    public long? PendingMs
    {
        get
        {
            lock (_gate)
                return _inFlightSince is { } since ? (long)Stopwatch.GetElapsedTime(since).TotalMilliseconds : null;
        }
    }

    public UpstreamBlock? Block { get { lock (_gate) return _block; } }
    public UpstreamHttpError? HttpError { get { lock (_gate) return _httpError; } }

    /// <summary>這一輪模型寫成裸名、被 <see cref="GeminiToolNameHandler"/> 改回宣告全名的工具呼叫數（known-issues #13）。</summary>
    public int ToolNameRepairs { get { lock (_gate) return _toolNameRepairs; } }

    /// <summary>這一輪模型呼叫過、對不上任何宣告的工具名，依出現順序。</summary>
    public IReadOnlyList<string> UndeclaredToolCalls { get { lock (_gate) return _undeclared.ToArray(); } }

    public void ToolNameRepaired()
    {
        lock (_gate) _toolNameRepairs++;
    }

    /// <summary>記一次沒宣告的呼叫，回傳這一輪累計幾次。</summary>
    public int UndeclaredToolCall(string name)
    {
        lock (_gate) { _undeclared.Add(name); return _undeclared.Count; }
    }

    public void CallStarted()
    {
        lock (_gate) { _calls++; _inFlightSince = Stopwatch.GetTimestamp(); }
    }

    /// <summary>收到回應：不再在途，這次的結果蓋掉上一次的。</summary>
    public void CallCompleted(UpstreamHttpError? httpError, UpstreamBlock? block)
    {
        lock (_gate) { _inFlightSince = null; _httpError = httpError; _block = block; }
    }

    /// <summary>沒收到回應就斷了（連線錯誤）：不再在途，但也沒有新的結果可記。
    /// 被取消的呼叫不走這裡——逾時時要看得出它卡了多久。</summary>
    public void CallAbandoned()
    {
        lock (_gate) _inFlightSince = null;
    }
}
