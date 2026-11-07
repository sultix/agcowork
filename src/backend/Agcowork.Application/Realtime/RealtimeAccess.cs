using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Identity.Authentication;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Realtime;

/// <summary>
/// Checks again whether realtime subscribers may still read the workspace: the same rules as connecting (and as every REST
/// request), evaluated against the current state. Runs before every delivery, so removing a membership, a role or scope
/// without <c>read</c>, deactivating the actor, revoking or expiring the key, or a password change (sessions) cuts the
/// connection off at the next event of the workspace.
/// </summary>
/// <remarks>Runs in a scope whose tenant is the workspace (<see cref="RealtimeTenant"/>), so memberships go through the tenant filter.</remarks>
public sealed class RealtimeAccess(IAgcoworkDbContext db, ITenantContext tenant, TimeProvider timeProvider)
{
    /// <summary>The connection ids whose subscriber has lost access to the current workspace.</summary>
    public async Task<IReadOnlySet<string>> FindRevokedAsync(IReadOnlyList<RealtimeConnection> connections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (connections.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var actorIds = connections.Select(c => c.Subscriber.ActorId).Distinct().ToList();
        var keyIds = connections.Select(c => c.Subscriber.ApiKeyId).OfType<Guid>().Distinct().ToList();

        var memberships = await db.Memberships.AsNoTracking()
            .Where(m => actorIds.Contains(m.ActorId))
            .ToDictionaryAsync(m => m.ActorId, cancellationToken)
            .ConfigureAwait(false);
        var actors = await db.Actors.AsNoTracking()
            .Where(a => actorIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, cancellationToken)
            .ConfigureAwait(false);
        var keys = keyIds.Count == 0
            ? []
            : await db.ApiKeys.AsNoTracking().Where(k => keyIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, cancellationToken).ConfigureAwait(false);
        var passwordChangedAt = await db.LocalLogins.AsNoTracking()
            .Where(l => actorIds.Contains(l.ActorId))
            .ToDictionaryAsync(l => l.ActorId, l => l.UpdatedAt, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var revoked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            var subscriber = connection.Subscriber;
            var allowed = subscriber.WorkspaceId == tenant.WorkspaceId
                && memberships.TryGetValue(subscriber.ActorId, out var membership)
                && actors.TryGetValue(subscriber.ActorId, out var actor)
                && actor.IsActive
                && Scopes(subscriber, membership, actor, keys, passwordChangedAt, now) is { } scopes
                && scopes.Contains(WorkspaceScope.Read);
            if (!allowed)
            {
                revoked.Add(connection.ConnectionId);
            }
        }

        return revoked;
    }

    /// <summary>Effective scopes of the subscriber's credential, or null when the credential is no longer valid.</summary>
    private IReadOnlySet<string>? Scopes(
        RealtimeSubscriber subscriber,
        Membership membership,
        Actor actor,
        Dictionary<Guid, ApiKey> keys,
        Dictionary<Guid, DateTimeOffset> passwordChangedAt,
        DateTimeOffset now)
    {
        if (subscriber.ApiKeyId is { } keyId)
        {
            if (!keys.TryGetValue(keyId, out var key)
                || key.ActorId != actor.Id
                || key.GetStatus(actor, now) != ApiKeyStatus.Active
                || (key.WorkspaceId is { } restriction && restriction != tenant.WorkspaceId))
            {
                return null;
            }

            return EffectiveScopes.Compute(membership.Scopes, key.Scopes.ToHashSet(StringComparer.Ordinal));
        }

        // Session: the same rule as AuthenticateSession on every request (human, local login, password unchanged).
        if (actor.Kind != ActorKind.Human
            || !passwordChangedAt.TryGetValue(actor.Id, out var changedAt)
            || !AuthenticateSession.IsCurrent(subscriber.PasswordStamp, subscriber.SessionIssuedAt, changedAt))
        {
            return null;
        }

        return EffectiveScopes.Compute(membership.Scopes, new HashSet<string>(StringComparer.Ordinal));
    }
}
