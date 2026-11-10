using Microsoft.Extensions.DependencyInjection;
using SVS.Application.Auth;
using SVS.Application.Spaces;
using SVS.Application.Transcription;

namespace SVS.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddSvsApplication(this IServiceCollection services)
    {
        services.AddScoped<AuthService>();
        services.AddScoped<SpaceAccessService>();
        services.AddScoped<SpaceService>();
        services.AddScoped<TranscriptionJobCoordinator>();
        services.AddScoped<LiveSessionCoordinator>();
        return services;
    }
}
