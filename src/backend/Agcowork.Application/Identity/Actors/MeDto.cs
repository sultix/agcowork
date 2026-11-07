using Agcowork.Application.Identity.ApiKeys;
using Agcowork.Domain.Identity;

namespace Agcowork.Application.Identity.Actors;

/// <summary>The calling actor. <paramref name="ApiKey"/> is set when the call was made with an API key.</summary>
/// <param name="Id">Actor id.</param>
/// <param name="Kind">human or agent.</param>
/// <param name="Name">Display name.</param>
/// <param name="Role">Free-text role, not a permission.</param>
/// <param name="Email">Email address; the login of humans.</param>
/// <param name="InstanceScopes">Effective instance scopes of this call (a restricted key carries none).</param>
/// <param name="ApiKey">Metadata of the key used, for API-key calls.</param>
/// <param name="MustChangePassword">
/// True when a human still uses the bootstrap password or one an admin reset: they should set their own with
/// <c>PATCH /api/v1/me/password</c>. Always false for agents.
/// </param>
public sealed record MeDto(
    Guid Id,
    ActorKind Kind,
    string Name,
    string? Role,
    string? Email,
    IReadOnlyList<string> InstanceScopes,
    ApiKeyDto? ApiKey,
    bool MustChangePassword);
