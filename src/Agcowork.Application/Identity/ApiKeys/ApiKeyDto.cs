using Agcowork.Domain.Identity;

namespace Agcowork.Application.Identity.ApiKeys;

/// <summary>Metadata of an API key. Never contains the secret or its hash.</summary>
public sealed record ApiKeyDto(
    Guid Id,
    Guid ActorId,
    string Prefix,
    IReadOnlyList<string> Scopes,
    Guid? WorkspaceId,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt)
{
    public static ApiKeyDto From(ApiKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new ApiKeyDto(key.Id, key.ActorId, key.Prefix, [.. key.Scopes], key.WorkspaceId, key.ExpiresAt, key.LastUsedAt, key.RevokedAt, key.CreatedAt);
    }
}

/// <param name="Key">Plain-text key. Shown exactly once; store it now, it cannot be retrieved later.</param>
/// <param name="ApiKey">Metadata of the new key.</param>
public sealed record CreatedApiKeyDto(string Key, ApiKeyDto ApiKey);
