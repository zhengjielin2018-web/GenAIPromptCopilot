using System.Threading.Channels;

namespace PromptCopilot.Api.Rendering;

/// <summary>生成預覽的佇列（預覽設計 §4）。一次只處理一張，對應 RunPod 的 Max Workers 1。
/// 也管預估等待（排在前面的張數 × 最近 10 張佔住佇列的平均秒數）與全站每日計數（台灣時間午夜歸零）。</summary>
public sealed class RenderQueue(TimeProvider time)
{
    /// <summary>台灣沒有日光節約，固定 UTC+8。</summary>
    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);
    private readonly Channel<RenderRecord> _channel = Channel.CreateUnbounded<RenderRecord>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();
    private readonly List<RenderRecord> _waiting = new();
    private readonly Queue<double> _recent = new();
    private RenderRecord? _current;
    private DateOnly _day;
    private int _dailyCount;

    public void Enqueue(RenderRecord r)
    {
        r.MarkEnqueued(time.GetUtcNow());
        lock (_gate) _waiting.Add(r);
        _channel.Writer.TryWrite(r);
    }

    public async ValueTask<RenderRecord> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            var r = await _channel.Reader.ReadAsync(ct);
            lock (_gate)
            {
                // DrainWaiting 拿走過的不再處理
                if (!_waiting.Remove(r)) continue;
                _current = r;
                return r;
            }
        }
    }

    /// <summary>busySeconds：這張佔住佇列多久（生圖加上審圖、自評，下一張要等到這些都結束）；沒取到圖的傳 null，不進平均。</summary>
    public void Done(RenderRecord r, double? busySeconds)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, r)) _current = null;
            if (busySeconds is not { } s) return;
            _recent.Enqueue(s);
            while (_recent.Count > 10) _recent.Dequeue();
        }
    }

    public int? PositionOf(RenderRecord r) { lock (_gate) { var i = _waiting.IndexOf(r); return i < 0 ? null : i + 1; } }

    public double EstimatedWaitSeconds(double defaultSeconds)
    {
        lock (_gate)
        {
            var ahead = _waiting.Count + (_current is null ? 0 : 1);
            return ahead * (_recent.Count == 0 ? defaultSeconds : _recent.Average());
        }
    }

    public int DailyCount { get { lock (_gate) { Roll(); return _dailyCount; } } }

    /// <summary>收件時保留一張；回傳保留在哪一天，退回時用。</summary>
    public DateOnly TakeDaily() { lock (_gate) { Roll(); _dailyCount++; return _day; } }

    /// <summary>沒送 RunPod 就結束的退回。跨日了就不退：今天的計數跟昨天的保留無關。</summary>
    public void ReturnDaily(DateOnly day) { lock (_gate) { Roll(); if (day == _day && _dailyCount > 0) _dailyCount--; } }

    /// <summary>服務停止時，佇列裡還沒處理的全部拿出來（由呼叫端標失敗）。</summary>
    public IReadOnlyList<RenderRecord> DrainWaiting() { lock (_gate) { var all = _waiting.ToList(); _waiting.Clear(); return all; } }

    private void Roll()
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().ToOffset(Taipei).DateTime);
        if (today == _day) return;
        _day = today;
        _dailyCount = 0;
    }
}
