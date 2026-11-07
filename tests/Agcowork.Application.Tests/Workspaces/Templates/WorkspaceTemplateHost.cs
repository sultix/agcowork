using Agcowork.Application.Collections;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Workspaces;
using Agcowork.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Application.Tests.Workspaces.Templates;

/// <summary>Builds <see cref="CreateWorkspace"/> the way the host does: the template resolves the use cases from the same scope.</summary>
internal static class WorkspaceTemplateHost
{
    public static CreateWorkspace CreateWorkspace(
        AgcoworkDbContext db,
        TestTenantAccessor tenant,
        TestActor caller,
        TimeProvider clock,
        params ICollectionCreatedHook[] extraHooks)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgcoworkDbContext>(db);
        services.AddSingleton<ICurrentActor>(caller);
        services.AddSingleton(clock);
        services.AddSingleton<ITenantContextAccessor>(tenant);
        foreach (var hook in extraHooks)
        {
            services.AddSingleton(hook);
        }

        services.AddApplication();
        var scope = services.BuildServiceProvider().CreateScope().ServiceProvider;
        return new CreateWorkspace(db, caller, clock, tenant, scope);
    }
}
