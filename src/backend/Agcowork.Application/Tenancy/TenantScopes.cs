using Agcowork.Application.Common.Results;
using Agcowork.Application.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Application.Tenancy;

/// <summary>
/// Runs work in a DI scope of its own, for adapters whose request scope is not (or not yet) bound to the workspace they act
/// on: instance-level MCP tools (AP-011), later SignalR hubs. A scope holds at most one tenant (<see cref="ITenantContextAccessor"/>),
/// so every call gets a fresh scope; the request scope stays untouched. The actor still comes from the authentication of the
/// current request (<c>ICurrentActor</c> is not scope-bound to the request).
/// </summary>
public sealed class TenantScopes(IServiceScopeFactory scopeFactory)
{
    /// <summary>Fresh scope without a tenant, e.g. for <see cref="CreateWorkspace"/>, which sets the tenant of its scope itself.</summary>
    public async Task<Result<T>> RunIsolatedAsync<T>(Func<IServiceProvider, CancellationToken, Task<Result<T>>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = scopeFactory.CreateAsyncScope();
        return await work(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fresh scope with the tenant of the workspace <paramref name="workspaceId"/>, resolved exactly like the tenancy middleware
    /// does for <c>/w/{slug}</c> (membership, key restriction, effective scopes). Not a member, restricted elsewhere or unknown:
    /// the same <see cref="NotFoundError"/>, and <paramref name="work"/> does not run.
    /// </summary>
    public async Task<Result<T>> RunInWorkspaceAsync<T>(
        Guid workspaceId,
        Func<IServiceProvider, CancellationToken, Task<Result<T>>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;

        // The caller's own memberships (key restriction applied) map the id to the slug; foreign workspaces are not in the list.
        var mine = await services.GetRequiredService<ListMyWorkspaces>().HandleAsync(new ListMyWorkspacesRequest(), cancellationToken).ConfigureAwait(false);
        if (mine.IsFailure)
        {
            return mine.Error;
        }

        if (mine.Value.FirstOrDefault(w => w.Id == workspaceId) is not { } workspace)
        {
            return new NotFoundError("workspace");
        }

        var tenant = await services.GetRequiredService<ResolveTenant>().HandleAsync(new ResolveTenantRequest(workspace.Slug), cancellationToken).ConfigureAwait(false);
        if (tenant.IsFailure)
        {
            return tenant.Error;
        }

        services.GetRequiredService<ITenantContextAccessor>().SetTenant(tenant.Value);
        return await work(services, cancellationToken).ConfigureAwait(false);
    }
}
