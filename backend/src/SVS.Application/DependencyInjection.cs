using Microsoft.Extensions.DependencyInjection;

namespace SVS.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddSvsApplication(this IServiceCollection services)
    {
        return services;
    }
}