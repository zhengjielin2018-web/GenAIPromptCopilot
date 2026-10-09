namespace PromptCopilot.Api.Rendering;

/// <summary>一次處理一張（對應 RunPod Max Workers 1）。服務停止時：手上那張由 ProcessAsync 取消並收尾，佇列裡的全部標失敗。
/// pipeline 用 Lazy：背景服務在啟動時就建立，直接注入會連帶提早建出 Gemini 的 chat service，沒設 Llm:ApiKey 時整個 API 起不來
/// （Program.cs：Gemini 的 service 在第一輪才建，key 沒設時只在啟動時警告）。第一張進來才建。</summary>
public sealed class RenderWorker(RenderQueue queue, Lazy<RenderPipeline> pipeline, ILogger<RenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var r = await queue.DequeueAsync(stoppingToken);
                // ProcessAsync 自己會收尾；這裡是最後一道，單張出什麼事都不能讓背景服務停掉
                try { await pipeline.Value.ProcessAsync(r, stoppingToken); }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested) { logger.LogError(e, "render {RenderId} crashed the pipeline", r.Id); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var r in queue.DrainWaiting()) await pipeline.Value.AbandonAsync(r);
        }
    }
}
