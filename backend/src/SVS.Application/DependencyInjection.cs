using Microsoft.Extensions.DependencyInjection;
using SVS.Application.Transcription;

namespace SVS.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddSvsApplication(this IServiceCollection services)
    {
        services.AddScoped<TranscriptionJobCoordinator>();
        return services;
    }
}
