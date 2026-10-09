namespace PromptCopilot.Api.Rendering;

/// <summary>一次處理一張（對應 RunPod Max Workers 1）。服務停止時：手上那張由 ProcessAsync 取消並收尾，佇列裡的全部標失敗。</summary>
public sealed class RenderWorker(RenderQueue queue, RenderPipeline pipeline, ILogger<RenderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var r = await queue.DequeueAsync(stoppingToken);
                // ProcessAsync 自己會收尾；這裡是最後一道，單張出什麼事都不能讓背景服務停掉
                try { await pipeline.ProcessAsync(r, stoppingToken); }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested) { logger.LogError(e, "render {RenderId} crashed the pipeline", r.Id); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var r in queue.DrainWaiting()) await pipeline.AbandonAsync(r);
        }
    }
}
