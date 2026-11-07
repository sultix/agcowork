using Agcowork.Domain.Identity;

namespace Agcowork.Application.Identity.Authentication;

/// <summary>
/// Result of a successful authentication. The Api turns it into a ClaimsPrincipal; the claims are
/// read back through <c>ICurrentActor</c>.
/// </summary>
public sealed record AuthenticatedActor(
    Guid ActorId,
    ActorKind Kind,
    IReadOnlyList<string> InstanceScopes,
    Guid? ApiKeyId = null,
    Guid? KeyWorkspaceId = null,
    IReadOnlyList<string>? KeyScopes = null)
{
    public static AuthenticatedActor ForSession(Actor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new AuthenticatedActor(actor.Id, actor.Kind, [.. actor.InstanceScopes]);
    }

    /// <summary>
    /// Principal of a key call. A restricted key (<see cref="ApiKey.IsRestricted"/>: workspace restriction or
    /// own scopes) carries <b>no instance scopes</b>, even if the actor has them: restricting a key must never
    /// leave it able to manage actors, keys or create workspaces.
    /// </summary>
    public static AuthenticatedActor ForApiKey(Actor actor, ApiKey key)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(key);
        IReadOnlyList<string> instanceScopes = key.IsRestricted ? [] : [.. actor.InstanceScopes];
        return new AuthenticatedActor(actor.Id, actor.Kind, instanceScopes, key.Id, key.WorkspaceId, [.. key.Scopes]);
    }
}
