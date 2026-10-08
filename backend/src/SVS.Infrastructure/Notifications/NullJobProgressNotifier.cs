using SVS.Application.Common.Interfaces;

namespace SVS.Infrastructure.Notifications;

public sealed class NullJobProgressNotifier : IJobProgressNotifier
{
    public Task NotifyAsync(JobProgressUpdate update, CancellationToken ct) => Task.CompletedTask;
}
