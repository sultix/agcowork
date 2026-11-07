using Agcowork.Application.Activity;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;

namespace Agcowork.Application.Realtime;

/// <summary>
/// Delivers the stable events of one workspace to its live connections, in order: first drops connections whose subscriber
/// lost access (<see cref="RealtimeAccess"/>), then sends each event as <see cref="ActivityEventDto"/> with its current
/// <c>targetTitle</c>, like <c>GET /activity</c>. Runs in a scope whose tenant is the workspace (<see cref="RealtimeTenant"/>).
/// </summary>
public sealed class PublishActivity(IAgcoworkDbContext db, ITenantContext tenant, RealtimeAccess access, IRealtimeConnections connections)
{
    /// <param name="events">Events of the tenant's workspace, in commit-safe order.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    /// <returns>The number of connections that were dropped.</returns>
    public async Task<int> PublishAsync(IReadOnlyList<ActivityEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Any(e => e.WorkspaceId != tenant.WorkspaceId))
        {
            throw new InvalidOperationException("PublishActivity got events of another workspace than its tenant.");
        }

        var live = connections.InWorkspace(tenant.WorkspaceId);
        if (live.Count == 0 || events.Count == 0)
        {
            return 0;
        }

        var revoked = await access.FindRevokedAsync(live, cancellationToken).ConfigureAwait(false);
        if (revoked.Count > 0)
        {
            await connections.DisconnectAsync(tenant.WorkspaceId, [.. revoked], cancellationToken).ConfigureAwait(false);
        }

        if (revoked.Count == live.Count)
        {
            return revoked.Count;
        }

        var titles = await ActivityTitles.ResolveAsync(db, events, cancellationToken).ConfigureAwait(false);
        foreach (var activity in events)
        {
            var dto = ActivityEventDto.From(activity, titles.GetValueOrDefault((activity.TargetType, activity.TargetId)));

            // Excluded as well as disconnected: a connection that is still closing must not get the event either.
            await connections.SendActivityAsync(tenant.WorkspaceId, dto, [.. revoked], cancellationToken).ConfigureAwait(false);
        }

        return revoked.Count;
    }
}
