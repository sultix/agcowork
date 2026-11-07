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

    public static AuthenticatedActor ForApiKey(Actor actor, ApiKey key)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(key);
        return new AuthenticatedActor(actor.Id, actor.Kind, [.. actor.InstanceScopes], key.Id, key.WorkspaceId, [.. key.Scopes]);
    }
}
