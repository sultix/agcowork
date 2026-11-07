using Agcowork.Domain.Activity;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Records;
using Agcowork.Domain.Spaces;
using Agcowork.Domain.Views;
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

    DbSet<View> Views { get; }

    DbSet<Record> Records { get; }

    DbSet<Revision> Revisions { get; }

    DbSet<ActivityEvent> ActivityEvents { get; }

    DbSet<IdempotencyKey> IdempotencyKeys { get; }

    /// <summary>For explicit transactions (<c>BeginTransactionAsync</c>); see architecture §5.</summary>
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a row lock on the workspace until the current transaction ends, serializing changes
    /// that must see a consistent set of memberships (owner invariant). Requires an open transaction.
    /// </summary>
    Task LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes a row lock on a record of the current tenant until the current transaction ends, serializing changes of
    /// the same record (revision numbers, appends, <c>expectedRevision</c> checks). A record of another workspace is
    /// not locked. Requires an open transaction.
    /// </summary>
    Task LockRecordAsync(Guid recordId, CancellationToken cancellationToken);

    /// <summary>
    /// Exclusive row lock (<c>FOR UPDATE</c>) on a collection of the current tenant until the transaction ends. Taken by
    /// schema changes, so no record write runs against the old schema while values are cleaned up. Requires an open transaction.
    /// </summary>
    /// <remarks>
    /// Lock order, everywhere: collection before record. Record writers take <see cref="LockCollectionSharedAsync"/>
    /// first, then <see cref="LockRecordAsync"/>; schema changes take this lock, then touch records.
    /// </remarks>
    Task LockCollectionAsync(Guid collectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Shared row lock (<c>FOR SHARE</c>) on a collection of the current tenant until the transaction ends. Taken by every
    /// record write in a collection before the record lock: record writes run in parallel, schema changes wait for them
    /// (and they for a schema change). Requires an open transaction.
    /// </summary>
    Task LockCollectionSharedAsync(Guid collectionId, CancellationToken cancellationToken);
}
