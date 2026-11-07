using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agcowork.Application;

public static class DependencyInjection
{
    /// <summary>Registers application-layer services (use cases, validation). Empty in phase 0 apart from the clock.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
