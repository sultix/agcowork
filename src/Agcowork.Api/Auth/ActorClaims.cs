using System.Security.Claims;
using Agcowork.Application.Identity.Authentication;

namespace Agcowork.Api.Auth;

/// <summary>Claim layout of an authenticated actor. The only translation between <see cref="AuthenticatedActor"/> and claims.</summary>
public static class ActorClaims
{
    public const string ActorId = "actor_id";
    public const string ActorKind = "actor_kind";
    public const string InstanceScope = "instance_scope";
    public const string ApiKeyId = "api_key_id";
    public const string KeyWorkspace = "key_workspace";
    public const string KeyScope = "key_scope";

    public static ClaimsPrincipal ToPrincipal(AuthenticatedActor actor, string authenticationType)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var claims = new List<Claim>
        {
            new(ActorId, actor.ActorId.ToString()),
            new(ActorKind, actor.Kind.ToString()),
        };
        claims.AddRange(actor.InstanceScopes.Select(scope => new Claim(InstanceScope, scope)));
        if (actor.ApiKeyId is { } keyId)
        {
            claims.Add(new Claim(ApiKeyId, keyId.ToString()));
        }

        if (actor.KeyWorkspaceId is { } workspaceId)
        {
            claims.Add(new Claim(KeyWorkspace, workspaceId.ToString()));
        }

        claims.AddRange((actor.KeyScopes ?? []).Select(scope => new Claim(KeyScope, scope)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType, ActorId, roleType: null));
    }
}
