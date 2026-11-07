namespace Agcowork.Application.Tenancy;

/// <summary>
/// Name of the EF Core global query filter that limits every tenant entity to the current workspace.
/// Outside a tenant context the filter matches nothing.
/// </summary>
/// <remarks>
/// Bypassing it (<c>IgnoreQueryFilters([TenantQueryFilter.Name])</c>) is reserved for queries that are
/// bounded otherwise and must say so in a comment: <see cref="ResolveTenant"/> and
/// <c>ListMyWorkspaces</c> read only the calling actor's own memberships; instance-admin use cases may
/// read across workspaces. Never bypass it in a workspace use case.
/// <para>
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> are protected by this filter alone (the SaveChanges interceptor does
/// not see them): never call them after <c>IgnoreQueryFilters</c>, never <c>SetProperty</c> on <c>WorkspaceId</c>.
/// </para>
/// </remarks>
public static class TenantQueryFilter
{
    public const string Name = "tenant";
}
