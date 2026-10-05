using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SVS.Application.Common.Interfaces;

namespace SVS.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSvsInfrastructure(
        this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<SvsDbContext>(o => o.UseNpgsql(connectionString));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<SvsDbContext>());

        services.AddHealthChecks().AddDbContextCheck<SvsDbContext>("database");

        return services;
    }
}