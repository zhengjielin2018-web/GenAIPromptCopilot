namespace PromptCopilot.Api.Rendering;

public enum RenderStatus { Queued, Generating, Reviewing, SelfChecking, Done, Failed, Blocked }
public enum SelfCheckStatus { Pending, Ok, Unavailable }

public static class RenderWire
{
    public static string Status(RenderStatus s) => s switch
    {
        RenderStatus.Queued => "queued", RenderStatus.Generating => "generating", RenderStatus.Reviewing => "reviewing",
        RenderStatus.SelfChecking => "self_checking", RenderStatus.Done => "done", RenderStatus.Failed => "failed", _ => "blocked",
    };

    public static string SelfCheck(SelfCheckStatus s) => s switch { SelfCheckStatus.Pending => "pending", SelfCheckStatus.Ok => "ok", _ => "unavailable" };
}

public sealed record SelfCheckItem(string FacetId, string Label, string Tag);
public sealed record SelfCheckVerdict(string FacetId, string Label, string Tag, string Verdict, string Reason);

/// <summary>收件時的快照（預覽設計 §4、§5.1）：之後使用者再改設定也不影響這張圖。TurnIndex 是哪張定稿卡；
/// 之後自主閉環是哪一輪（設計 §12）。PromptReviewed：定稿當時有沒有經過輸出審查（FinalPrompt.Reviewed）。</summary>
public sealed record RenderRequest(string SessionId, int TurnIndex, string Positive, string Negative, long Seed,
    IReadOnlyList<SelfCheckItem> SelfCheckItems, bool SafetyOn, bool PromptReviewed);

public static class RenderMessages
{
    public const string PromptBlocked = "提示詞沒有通過審查，沒有生成預覽";
    public const string ImageBlocked = "預覽圖被判定為不當內容，沒有顯示";
    public const string ReviewFailed = "預覽圖沒有通過審查，沒有顯示";
    public const string Failed = "預覽圖生成失敗，可以再按一次";
    public const string Timeout = "生成逾時，可以再按一次";
}

public sealed record SelfCheckView(string Status, IReadOnlyList<SelfCheckVerdict> Items);
public sealed record RenderTimingsView(int? QueueMs, int? DelayMs, int? ExecutionMs, int? ReviewMs, int? SelfCheckMs);
public sealed record RenderView(string RenderId, int TurnIndex, string Status, int? Position, string Safety, string? Message,
    SelfCheckView SelfCheck, RenderTimingsView Timings);

/// <summary>一張預覽。背景服務寫、端點讀，全部經過 _gate。狀態由事實推導而不是一格一格設（預覽設計 §5.2）：
/// 圖還沒到是 queued／generating；審查開著而且還沒過是 reviewing（圖不給）；自評還沒好是 self_checking（圖給）；都好了是 done。
/// 審圖與自評平行跑，誰先好都一樣。failed／blocked 是終點，之後的結果一律忽略。</summary>
public sealed class RenderRecord(string id, RenderRequest request, DateOnly quotaDay)
{
    private readonly object _gate = new();
    private bool _generating, _imageArrived, _reviewPassed, _selfCheckDone;
    private RenderStatus? _end;
    private string? _message;
    private byte[]? _image;
    private IReadOnlyList<SelfCheckVerdict> _selfCheck = Array.Empty<SelfCheckVerdict>();
    private SelfCheckStatus _selfCheckState = SelfCheckStatus.Pending;

    public string Id => id;
    public RenderRequest Request => request;
    /// <summary>收件時保留額度的那一天（台灣時間）；沒送 RunPod 就結束時退回到這一天。</summary>
    public DateOnly QuotaDay => quotaDay;

    public string? RunPodJobId { get; private set; }
    public bool Submitted { get; private set; }
    public DateTimeOffset? EnqueuedAt { get; private set; }
    public int? QueueMs { get; private set; }
    public int? DelayMs { get; private set; }
    public int? ExecutionMs { get; private set; }
    public int? ReviewMs { get; private set; }
    public int? SelfCheckMs { get; private set; }
    /// <summary>只進 audit：被擋在哪一關（prompt／image）、失敗種類、細節（判定理由、例外）。不回給使用者（同 SafetyGuard 的 BlockDetail）。</summary>
    public string? BlockStage { get; private set; }
    public string? FailureKind { get; private set; }
    public string? Detail { get; private set; }

    public string? Message { get { lock (_gate) return _message; } }

    public RenderStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (_end is { } end) return end;
                if (!_imageArrived) return _generating ? RenderStatus.Generating : RenderStatus.Queued;
                if (request.SafetyOn && !_reviewPassed) return RenderStatus.Reviewing;
                return _selfCheckDone ? RenderStatus.Done : RenderStatus.SelfChecking;
            }
        }
    }

    public bool IsFinished => Status is RenderStatus.Done or RenderStatus.Failed or RenderStatus.Blocked;

    /// <summary>算不算進張數上限（預覽設計 §5.1）：還沒結束的先保留；結束了的只算真的送去 RunPod 的。</summary>
    public bool CountsTowardLimit => Submitted || !IsFinished;

    /// <summary>審查開著時，過了才給；被擋的圖不存。</summary>
    public byte[]? Image { get { lock (_gate) return Status is RenderStatus.SelfChecking or RenderStatus.Done ? _image : null; } }

    public SelfCheckStatus SelfCheckState { get { lock (_gate) return _selfCheckState; } }
    public IReadOnlyList<SelfCheckVerdict> SelfCheck { get { lock (_gate) return _selfCheck; } }

    public void MarkEnqueued(DateTimeOffset at) { lock (_gate) EnqueuedAt = at; }

    public void MarkGenerating(int queueMs) { lock (_gate) { if (_end is not null) return; _generating = true; QueueMs = queueMs; } }

    public void MarkSubmitted(string jobId) { lock (_gate) { Submitted = true; RunPodJobId = jobId; } }

    public void ImageArrived(byte[] png, int? delayMs, int? executionMs)
    {
        lock (_gate) { if (_end is not null) return; _image = png; _imageArrived = true; DelayMs = delayMs; ExecutionMs = executionMs; }
    }

    public void ReviewPassed(int reviewMs) { lock (_gate) { if (_end is not null) return; _reviewPassed = true; ReviewMs = reviewMs; } }

    /// <summary>verdicts 為 null 表示自評失敗（Gemini 出錯或拒收）：標 unavailable，圖照給。</summary>
    public void SelfCheckFinished(IReadOnlyList<SelfCheckVerdict>? verdicts, int ms)
    {
        lock (_gate)
        {
            if (_end is not null) return;
            _selfCheckDone = true; SelfCheckMs = ms;
            _selfCheck = verdicts ?? Array.Empty<SelfCheckVerdict>();
            _selfCheckState = verdicts is null ? SelfCheckStatus.Unavailable : SelfCheckStatus.Ok;
        }
    }

    public void Block(string message, string stage, string? detail, int? reviewMs = null)
    {
        lock (_gate)
        {
            if (_end is not null || Status == RenderStatus.Done) return;
            _end = RenderStatus.Blocked; _message = message; BlockStage = stage; Detail = detail; ReviewMs = reviewMs ?? ReviewMs;
            _image = null; _selfCheck = Array.Empty<SelfCheckVerdict>();
        }
    }

    public void Fail(string message, string kind, string? detail)
    {
        lock (_gate)
        {
            if (_end is not null || Status == RenderStatus.Done) return;
            _end = RenderStatus.Failed; _message = message; FailureKind = kind; Detail = detail; _image = null;
        }
    }

    public RenderView View(int? position)
    {
        lock (_gate)
        {
            var status = Status;
            return new RenderView(id, request.TurnIndex, RenderWire.Status(status), status == RenderStatus.Queued ? position : null,
                request.SafetyOn ? "on" : "off", _message,
                new SelfCheckView(RenderWire.SelfCheck(_selfCheckState), status == RenderStatus.Done ? _selfCheck : Array.Empty<SelfCheckVerdict>()),
                new RenderTimingsView(QueueMs, DelayMs, ExecutionMs, ReviewMs, SelfCheckMs));
        }
    }
}
