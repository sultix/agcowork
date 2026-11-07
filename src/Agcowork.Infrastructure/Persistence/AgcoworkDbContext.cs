using System.Linq.Expressions;
using System.Reflection;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Common;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Records;
using Agcowork.Domain.Spaces;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agcowork.Infrastructure.Persistence;

/// <summary>
/// EF Core context. Tenancy (architecture §2a): every <see cref="ITenantEntity"/> gets the global query
/// filter <see cref="TenantQueryFilter.Name"/> (<c>WorkspaceId == current tenant</c>, nothing outside a
/// tenant), and <see cref="Tenancy.TenantSaveChangesInterceptor"/> refuses writes to other tenants.
/// </summary>
/// <param name="options">Provider options (see AgcoworkDbContextOptions).</param>
/// <param name="tenantAccessor">Tenant of the current scope; null (e.g. design time) behaves like "no tenant".</param>
public sealed class AgcoworkDbContext(DbContextOptions<AgcoworkDbContext> options, ITenantContextAccessor? tenantAccessor = null)
    : DbContext(options), IAgcoworkDbContext
{
    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(AgcoworkDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<Space> Spaces => Set<Space>();

    public DbSet<Collection> Collections => Set<Collection>();

    public DbSet<Template> Templates => Set<Template>();

    public DbSet<Record> Records => Set<Record>();

    public DbSet<Revision> Revisions => Set<Revision>();

    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();

    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    public DbSet<Actor> Actors => Set<Actor>();

    public DbSet<LocalLogin> LocalLogins => Set<LocalLogin>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    /// <summary>
    /// Workspace id of the current tenant, <see cref="Guid.Empty"/> without one (matches no row).
    /// Read by the query filter on every query, so it always reflects the accessor's current state.
    /// </summary>
    public Guid TenantWorkspaceId => tenantAccessor?.Current?.WorkspaceId ?? Guid.Empty;

    public Task LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockWorkspaceAsync requires an open transaction; the lock is released at commit.");
        }

        return Database.ExecuteSqlAsync($"SELECT 1 FROM workspaces WHERE id = {workspaceId} FOR UPDATE", cancellationToken);
    }

    public Task LockRecordAsync(Guid recordId, CancellationToken cancellationToken)
    {
        if (Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("LockRecordAsync requires an open transaction; the lock is released at commit.");
        }

        // Raw SQL bypasses the query filter, so the tenant condition is spelled out.
        return Database.ExecuteSqlAsync($"SELECT 1 FROM records WHERE id = {recordId} AND workspace_id = {TenantWorkspaceId} FOR UPDATE", cancellationToken);
    }

    public Task LockCollectionAsync(Guid collectionId, CancellationToken cancellationToken) => LockCollection(collectionId, shared: false, cancellationToken);

    public Task LockCollectionSharedAsync(Guid collectionId, CancellationToken cancellationToken) => LockCollection(collectionId, shared: true, cancellationToken);

    private Task<int> LockCollection(Guid collectionId, bool shared, CancellationToken cancellationToken)
    {
        if (Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Collection locks require an open transaction; the lock is released at commit.");
        }

        // Raw SQL bypasses the query filter, so the tenant condition is spelled out.
        return shared
            ? Database.ExecuteSqlAsync($"SELECT 1 FROM collections WHERE id = {collectionId} AND workspace_id = {TenantWorkspaceId} FOR SHARE", cancellationToken)
            : Database.ExecuteSqlAsync($"SELECT 1 FROM collections WHERE id = {collectionId} AND workspace_id = {TenantWorkspaceId} FOR UPDATE", cancellationToken);
    }

    /// <summary>Translates unique violations into <see cref="UniqueConstraintViolationException"/> (see <see cref="UniqueConflict"/>).</summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: { } constraintName,
        })
        {
            throw new UniqueConstraintViolationException(constraintName, exception);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // GIN indexes over (workspace_id, properties): btree_gin provides the GIN operator class for uuid (trusted extension).
        modelBuilder.HasPostgresExtension("btree_gin");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgcoworkDbContext).Assembly);

        // Convention: every tenant entity is filtered, without opt-in per configuration.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => t.BaseType is null && typeof(ITenantEntity).IsAssignableFrom(t.ClrType)))
        {
            ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantEntity
    {
        // Referencing a context member makes EF evaluate it per query against the executing context instance.
        Expression<Func<TEntity, bool>> filter = entity => entity.WorkspaceId == TenantWorkspaceId;
        modelBuilder.Entity<TEntity>().HasQueryFilter(TenantQueryFilter.Name, filter);
    }
}
