using Agcowork.Domain.Common;
using Agcowork.Domain.Workspaces;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agcowork.Infrastructure.Tenancy;

/// <summary>
/// Defense in depth behind the query filter (architecture §2a, step 4). Before every save:
/// <list type="bullet">
/// <item>an added <see cref="ITenantEntity"/> with an empty <c>WorkspaceId</c> gets the current tenant's id;</item>
/// <item>adding, changing or deleting a tenant entity of any other workspace throws
/// <see cref="InvalidOperationException"/>, as does moving an entity to another workspace;</item>
/// <item>inside a tenant, changing or deleting any <see cref="Workspace"/> other than the tenant's own throws.</item>
/// </list>
/// Bulk operations (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>) bypass this interceptor; see <see cref="Application.Common.Persistence.IAgcoworkDbContext"/> for their rules.
/// The only exception: entities of a workspace that is itself inserted in the same save (creating a
/// workspace together with its owner membership, later its template content) need no tenant context.
/// A violation is a programming error, so it surfaces as 500 and is logged by the exception handler.
/// </summary>
internal sealed class TenantSaveChangesInterceptor : SaveChangesInterceptor
{
    public static TenantSaveChangesInterceptor Instance { get; } = new();

    private const string WorkspaceIdProperty = nameof(ITenantEntity.WorkspaceId);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Enforce(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        Enforce(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Enforce(DbContext? context)
    {
        if (context is not AgcoworkDbContext db)
        {
            return;
        }

        var tenant = db.TenantWorkspaceId;
        HashSet<Guid>? newWorkspaces = null;

        // The tenant root itself: inside a tenant only the own workspace may be changed or deleted.
        if (tenant != Guid.Empty)
        {
            foreach (var workspace in db.ChangeTracker.Entries<Workspace>())
            {
                if (workspace.State is EntityState.Modified or EntityState.Deleted && workspace.Entity.Id != tenant)
                {
                    throw new InvalidOperationException(
                        $"Tenant isolation violated: {workspace.State} Workspace {workspace.Entity.Id} is not the current tenant {tenant}.");
                }
            }
        }

        foreach (var entry in db.ChangeTracker.Entries<ITenantEntity>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var property = entry.Property<Guid>(WorkspaceIdProperty);
            if (entry.State == EntityState.Added && property.CurrentValue == Guid.Empty)
            {
                if (tenant == Guid.Empty)
                {
                    throw Violation(entry, "has no WorkspaceId and there is no tenant context to take it from");
                }

                property.CurrentValue = tenant;
            }

            var workspaceId = property.CurrentValue;
            if (entry.State == EntityState.Modified && property.OriginalValue != workspaceId)
            {
                throw Violation(entry, "was moved to another workspace");
            }

            if (workspaceId == tenant)
            {
                continue;
            }

            newWorkspaces ??= [.. db.ChangeTracker.Entries<Workspace>().Where(w => w.State == EntityState.Added).Select(w => w.Entity.Id)];
            if (entry.State == EntityState.Added && newWorkspaces.Contains(workspaceId))
            {
                continue;
            }

            throw Violation(entry, tenant == Guid.Empty
                ? $"belongs to workspace {workspaceId} but there is no tenant context"
                : $"belongs to workspace {workspaceId}, not to the current tenant {tenant}");
        }
    }

    private static InvalidOperationException Violation(EntityEntry<ITenantEntity> entry, string reason) =>
        new($"Tenant isolation violated: {entry.State} {entry.Metadata.ClrType.Name} {reason}.");
}
