namespace Agcowork.Application.Tenancy;

/// <summary>
/// Holds the tenant of the current scope (one HTTP request, later one MCP call). Set exactly once by
/// the tenancy middleware; read by <see cref="ITenantContext"/> and by the DbContext's query filter.
/// </summary>
public interface ITenantContextAccessor
{
    /// <summary>Null outside the workspace route group (instance endpoints, startup, bootstrap).</summary>
    ITenantContext? Current { get; }

    /// <summary>Sets the tenant. Throws when one is already set: a scope never switches tenants.</summary>
    void SetTenant(ITenantContext tenant);
}

/// <summary>Scoped default implementation.</summary>
public sealed class TenantContextAccessor : ITenantContextAccessor
{
    public ITenantContext? Current { get; private set; }

    public void SetTenant(ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (Current is not null)
        {
            throw new InvalidOperationException("The tenant context is already set for this scope.");
        }

        Current = tenant;
    }
}

/// <summary><see cref="ITenantContext"/> as injected into use cases: delegates to the accessor at call time.</summary>
internal sealed class CurrentTenantContext(ITenantContextAccessor accessor) : ITenantContext
{
    private ITenantContext Tenant => accessor.Current
        ?? throw new InvalidOperationException("No tenant context. Workspace use cases run only inside the /w/{workspaceSlug} route group.");

    public Guid WorkspaceId => Tenant.WorkspaceId;

    public string Slug => Tenant.Slug;

    public bool IsArchived => Tenant.IsArchived;

    public Domain.Workspaces.Membership Membership => Tenant.Membership;

    public IReadOnlySet<string> EffectiveScopes => Tenant.EffectiveScopes;
}
