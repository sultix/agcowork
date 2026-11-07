using Agcowork.Application.Activity;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Realtime;

/// <summary>
/// The activity log of the whole instance, read forward in the commit-safe order of <see cref="ActivityLog"/>, for the realtime
/// dispatcher. Runs without tenant and returns events of every workspace; the dispatcher hands each event only to the
/// connections of its own workspace.
/// </summary>
public sealed class InstanceActivityReader(IAgcoworkDbContext db)
{
    /// <summary>The newest stable position of the instance: where the dispatcher starts (no replay of old events).</summary>
    public Task<ActivityPosition> HeadAsync(CancellationToken cancellationToken) =>
        AllWorkspaces().HeadAsync(cancellationToken);

    /// <summary>Up to <paramref name="limit"/> stable events after <paramref name="position"/>, in commit-safe order.</summary>
    public async Task<IReadOnlyList<ActivityEvent>> ReadAsync(ActivityPosition position, int limit, CancellationToken cancellationToken) =>
        await AllWorkspaces().Stable().After(position).InCommitOrder().Take(limit).ToListAsync(cancellationToken).ConfigureAwait(false);

    // Bypasses the tenant filter: the dispatcher serves all workspaces of the instance and has no tenant. Read-only, and
    // the only consumer routes every event to the group of its own WorkspaceId (architecture §2a, step 6).
    private IQueryable<ActivityEvent> AllWorkspaces() =>
        db.ActivityEvents.IgnoreQueryFilters([TenantQueryFilter.Name]).AsNoTracking();
}
