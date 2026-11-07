using System.Reflection;
using Agcowork.Application.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agcowork.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers application-layer services: the clock and every <see cref="IUseCase{TRequest,TResponse}"/>
    /// / <see cref="IUseCase{TRequest}"/> implementation of this assembly as a scoped service of its
    /// concrete type (adapters inject e.g. <c>CreateActor</c> directly).
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        foreach (var useCase in FindUseCases(typeof(DependencyInjection).Assembly))
        {
            services.TryAddScoped(useCase);
        }

        return services;
    }

    /// <summary>All concrete use-case types of an assembly.</summary>
    public static IReadOnlyList<Type> FindUseCases(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return [.. assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .Where(type => type.GetInterfaces().Any(IsUseCaseInterface))];
    }

    private static bool IsUseCaseInterface(Type type) =>
        type.IsGenericType
        && (type.GetGenericTypeDefinition() == typeof(IUseCase<,>) || type.GetGenericTypeDefinition() == typeof(IUseCase<>));
}
