using Agcowork.Domain.Collections;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Spaces;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agcowork.Application.Common.Persistence;

/// <summary>
/// Data access for use cases. Use cases query <see cref="DbSet{TEntity}"/> directly (no repositories)
/// and call <see cref="SaveChangesAsync"/> once per unit of work. Implemented by the EF Core context
/// in Infrastructure.
/// </summary>
/// <remarks>
/// Tenant entities (<see cref="Domain.Common.ITenantEntity"/>, e.g. <see cref="Memberships"/>) are
/// filtered to the current tenant (<see cref="Tenancy.ITenantContext"/>); outside a tenant context
/// they are invisible. Bypass only with <c>IgnoreQueryFilters([TenantQueryFilter.Name])</c> and a
/// comment explaining why the query cannot leak other tenants' data.
/// <para>
/// Bulk operations (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>) skip change tracking and therefore the tenant
/// interceptor; only the query filter protects them. Rules: never combine them with <c>IgnoreQueryFilters</c>
/// on a tenant entity, and never <c>SetProperty(e =&gt; e.WorkspaceId, …)</c>.
/// </para>
/// </remarks>
public interface IAgcoworkDbContext
{
    DbSet<Actor> Actors { get; }

    DbSet<LocalLogin> LocalLogins { get; }

    DbSet<ApiKey> ApiKeys { get; }

    DbSet<Workspace> Workspaces { get; }

    DbSet<Membership> Memberships { get; }

    DbSet<Space> Spaces { get; }

    DbSet<Collection> Collections { get; }

    DbSet<Template> Templates { get; }

    /// <summary>For explicit transactions (<c>BeginTransactionAsync</c>); see architecture §5.</summary>
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a row lock on the workspace until the current transaction ends, serializing changes
    /// that must see a consistent set of memberships (owner invariant). Requires an open transaction.
    /// </summary>
    Task LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken);
}
