using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SVS.Application.Common.Interfaces;
using SVS.Application.Transcription;
using SVS.Infrastructure.AiService;
using SVS.Infrastructure.Media;
using SVS.Infrastructure.Notifications;

namespace SVS.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSvsInfrastructure(
        this IServiceCollection services, string connectionString, IConfiguration configuration)
    {
        services.AddDbContext<SvsDbContext>(o => o.UseNpgsql(connectionString));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<SvsDbContext>());

        services.AddHealthChecks().AddDbContextCheck<SvsDbContext>("database");

        services.Configure<AiServiceOptions>(configuration.GetSection("AiService"));
        services.Configure<MediaOptions>(configuration.GetSection("Media"));
        services.Configure<LiveOptions>(configuration.GetSection("Live"));

        services.AddHttpClient<IAiJobClient, HttpAiJobClient>(ConfigureAiClient);
        services.AddHttpClient<ILiveAiClient, HttpLiveAiClient>(ConfigureAiClient);

        services.AddSingleton<IMediaStorage, LocalMediaStorage>();
        services.AddSingleton<IJobProgressNotifier, NullJobProgressNotifier>();

        return services;
    }

    private static void ConfigureAiClient(IServiceProvider sp, HttpClient client)
    {
        var o = sp.GetRequiredService<IOptions<AiServiceOptions>>().Value;
        if (string.IsNullOrWhiteSpace(o.ApiKey))
            throw new InvalidOperationException("Thiếu cấu hình AiService:ApiKey (đặt bằng user-secrets hoặc biến môi trường AiService__ApiKey)");
        client.BaseAddress = new Uri(o.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(o.RequestTimeoutSeconds);
        client.DefaultRequestHeaders.Add("X-Api-Key", o.ApiKey);
    }
}
