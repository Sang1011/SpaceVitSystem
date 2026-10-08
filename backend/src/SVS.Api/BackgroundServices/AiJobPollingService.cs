using Microsoft.Extensions.Options;
using SVS.Application.Transcription;
using SVS.Infrastructure.AiService;

namespace SVS.Api.BackgroundServices;

/// <summary>
/// Định kỳ hỏi AI service về các job đang hoạt động. Đây chỉ là một cách "đẩy" thay đổi vào
/// TranscriptionJobCoordinator.SyncByIdAsync; chuyển sang callback/message queue chỉ cần gọi hàm đó từ nơi khác.
/// Mỗi job dùng một scope riêng để lỗi của job này không làm hỏng DbContext của job khác.
/// </summary>
public sealed class AiJobPollingService(
    IServiceScopeFactory scopes, IOptions<AiServiceOptions> options, ILogger<AiJobPollingService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));
        while (!stop.IsCancellationRequested)
        {
            try
            {
                List<Guid> ids;
                using (var scope = scopes.CreateScope())
                    ids = await scope.ServiceProvider.GetRequiredService<TranscriptionJobCoordinator>().GetActiveJobIdsAsync(stop);

                foreach (var id in ids)
                {
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<TranscriptionJobCoordinator>().SyncByIdAsync(id, stop);
                    }
                    catch (Exception ex) when (!stop.IsCancellationRequested)
                    {
                        log.LogError(ex, "Đồng bộ job {JobId} lỗi", id);
                    }
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                log.LogError(ex, "Vòng polling AI service lỗi");
            }

            try { await Task.Delay(interval, stop); }
            catch (OperationCanceledException) { break; }
        }
    }
}
