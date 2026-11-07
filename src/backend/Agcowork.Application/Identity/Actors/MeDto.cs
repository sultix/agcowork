using Agcowork.Application.Identity.ApiKeys;
using Agcowork.Domain.Identity;

namespace Agcowork.Application.Identity.Actors;

/// <summary>The calling actor. <paramref name="ApiKey"/> is set when the call was made with an API key.</summary>
public sealed record MeDto(
    Guid Id,
    ActorKind Kind,
    string Name,
    string? Role,
    string? Email,
    IReadOnlyList<string> InstanceScopes,
    ApiKeyDto? ApiKey);
