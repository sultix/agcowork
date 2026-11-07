using System.Reflection;
using Agcowork.Application.Activity;
using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Common;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agcowork.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers application-layer services: the clock and every <see cref="IUseCase{TRequest,TResponse}"/>
    /// / <see cref="IUseCase{TRequest}"/> implementation of this assembly as a scoped service of its
    /// concrete type (adapters inject e.g. <c>CreateActor</c> directly), the tenant context and the property-value services.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        // Tenant of the current scope: set by the tenancy middleware, read by use cases and the DbContext filter.
        services.TryAddScoped<ITenantContextAccessor, TenantContextAccessor>();
        services.TryAddScoped<ITenantContext, CurrentTenantContext>();

        // Property values (AP-006); relation targets are records of the target collection (AP-007).
        services.TryAddScoped<PropertyValueValidator>();
        services.TryAddScoped<IRelationTargetChecker, RecordRelationTargetChecker>();
        services.TryAddScoped<IActorMembershipChecker, MembershipActorChecker>();
        services.TryAddScoped<ISchemaProvider, SchemaProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISchemaChangedHook, TemplatePresetSchemaHook>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISchemaChangedHook, RecordValuesSchemaHook>());

        // Views (AP-008): standard views with every collection; schema changes clean up view references.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICollectionCreatedHook, DefaultViewsHook>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISchemaChangedHook, ViewSchemaHook>());

        // Activity log (AP-007): events are added by the writing use cases and saved with their change.
        services.TryAddScoped<IActivityRecorder, ActivityRecorder>();

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
