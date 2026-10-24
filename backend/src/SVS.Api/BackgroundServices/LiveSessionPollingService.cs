using Microsoft.Extensions.Options;
using SVS.Application.Transcription;

namespace SVS.Api.BackgroundServices;

/// <summary>
/// Hỏi AI service về các phiên ghi âm đang hoạt động (chu kỳ ngắn, mặc định 1 giây để transcript hiện gần realtime).
/// Cũng là nơi tự dừng phiên bị bỏ và gửi bù chunk/finalize khi AI service tạm mất kết nối. Mỗi phiên dùng một scope riêng.
/// </summary>
public sealed class LiveSessionPollingService(
    IServiceScopeFactory scopes, IOptions<LiveOptions> options, ILogger<LiveSessionPollingService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(0.5, options.Value.PollIntervalSeconds));
        while (!stop.IsCancellationRequested)
        {
            try
            {
                List<Guid> ids;
                using (var scope = scopes.CreateScope())
                    ids = await scope.ServiceProvider.GetRequiredService<LiveSessionCoordinator>().GetActiveSessionIdsAsync(stop);

                foreach (var id in ids)
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<LiveSessionCoordinator>().SyncByIdAsync(id, stop);
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested)
                    {
                        log.LogError(ex, "Đồng bộ phiên {SessionId} lỗi", id);
                    }
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                log.LogError(ex, "Vòng polling phiên live lỗi");
            }

            try { await Task.Delay(interval, stop); }
            catch (OperationCanceledException) { break; }
        }
    }
}
