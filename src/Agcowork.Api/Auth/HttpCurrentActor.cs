using System.Security.Claims;
using Agcowork.Application.Common.Security;
using Agcowork.Domain.Identity;

namespace Agcowork.Api.Auth;

/// <summary><see cref="ICurrentActor"/> read from the claims of the current HTTP request (see <see cref="ActorClaims"/>).</summary>
internal sealed class HttpCurrentActor(IHttpContextAccessor httpContextAccessor) : ICurrentActor
{
    private ClaimsPrincipal User => httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal();

    public bool IsAuthenticated =>
        User.Identity?.IsAuthenticated == true && Guid.TryParse(User.FindFirstValue(ActorClaims.ActorId), out _);

    public Guid Id => Guid.Parse(Required(ActorClaims.ActorId));

    public ActorKind Kind => Enum.Parse<ActorKind>(Required(ActorClaims.ActorKind));

    public IReadOnlySet<string> InstanceScopes => Values(ActorClaims.InstanceScope);

    public Guid? ApiKeyId => OptionalGuid(ActorClaims.ApiKeyId);

    public Guid? KeyWorkspaceRestriction => OptionalGuid(ActorClaims.KeyWorkspace);

    public IReadOnlySet<string> KeyScopes => Values(ActorClaims.KeyScope);

    private string Required(string claimType)
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException("No authenticated actor. Check ICurrentActor.IsAuthenticated (RequireAuthenticated) first.");
        }

        return User.FindFirstValue(claimType)
            ?? throw new InvalidOperationException($"Claim '{claimType}' is missing.");
    }

    private Guid? OptionalGuid(string claimType)
    {
        _ = Required(ActorClaims.ActorId);
        return Guid.TryParse(User.FindFirstValue(claimType), out var value) ? value : null;
    }

    private HashSet<string> Values(string claimType)
    {
        _ = Required(ActorClaims.ActorId);
        return User.FindAll(claimType).Select(claim => claim.Value).ToHashSet(StringComparer.Ordinal);
    }
}
