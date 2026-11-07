using Agcowork.Domain.Identity;

namespace Agcowork.Application.Identity.Authentication;

/// <summary>
/// Result of a successful authentication. The Api turns it into a ClaimsPrincipal; the claims are
/// read back through <c>ICurrentActor</c>.
/// </summary>
/// <param name="ActorId">The authenticated actor.</param>
/// <param name="Kind">Human or agent.</param>
/// <param name="InstanceScopes">Effective instance scopes.</param>
/// <param name="ApiKeyId">Key calls: the key used.</param>
/// <param name="KeyWorkspaceId">Key calls: workspace restriction of the key.</param>
/// <param name="KeyScopes">Key calls: scope restriction of the key.</param>
/// <param name="PasswordStamp">
/// Sessions only: <see cref="LocalLogin.UpdatedAt"/> of the password that was verified (login) or set (own change), read
/// or written together with that hash. The session cookie stores it; the session is valid while it equals the current value.
/// </param>
public sealed record AuthenticatedActor(
    Guid ActorId,
    ActorKind Kind,
    IReadOnlyList<string> InstanceScopes,
    Guid? ApiKeyId = null,
    Guid? KeyWorkspaceId = null,
    IReadOnlyList<string>? KeyScopes = null,
    DateTimeOffset? PasswordStamp = null)
{
    /// <param name="actor">The human.</param>
    /// <param name="passwordStamp"><see cref="LocalLogin.UpdatedAt"/> of the password the session is bound to.</param>
    public static AuthenticatedActor ForSession(Actor actor, DateTimeOffset passwordStamp)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new AuthenticatedActor(actor.Id, actor.Kind, [.. actor.InstanceScopes], PasswordStamp: passwordStamp);
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
