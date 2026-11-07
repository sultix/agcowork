namespace Agcowork.Domain.Identity;

/// <summary>
/// Credential of an agent actor. Stores the visible prefix and a SHA-256 hash of the secret;
/// the plain-text key exists only once, in the response that created it.
/// </summary>
public sealed class ApiKey
{
    public const int SecretHashLength = 32;

    /// <summary><see cref="LastUsedAt"/> is written at most once per interval to avoid a write per request.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private ApiKey(
        Guid id,
        Guid actorId,
        string prefix,
        byte[] secretHash,
        IReadOnlyList<string> scopes,
        Guid? workspaceId,
        DateTimeOffset? expiresAt,
        DateTimeOffset createdAt)
    {
        Id = id;
        ActorId = actorId;
        Prefix = prefix;
        SecretHash = secretHash;
        Scopes = scopes;
        WorkspaceId = workspaceId;
        ExpiresAt = expiresAt;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid ActorId { get; private set; }

    /// <summary>Visible, unique identifier part of the token (<see cref="ApiKeyToken.PrefixLength"/> Base62 characters).</summary>
    public string Prefix { get; private set; }

    /// <summary>SHA-256 of the secret part of the token.</summary>
    public byte[] SecretHash { get; private set; }

    /// <summary>
    /// Optional restriction (subset of <see cref="KeyScope.All"/>); empty means the membership rights apply in full.
    /// Any restriction also drops the actor's instance scopes for this key (<see cref="IsRestricted"/>).
    /// </summary>
    public IReadOnlyList<string> Scopes { get; private set; }

    /// <summary>
    /// Optional restriction to a single workspace. Evaluated by the tenancy layer; also drops the actor's
    /// instance scopes for this key (<see cref="IsRestricted"/>).
    /// </summary>
    public Guid? WorkspaceId { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static ApiKey Issue(
        Actor actor,
        string prefix,
        byte[] secretHash,
        IEnumerable<string> scopes,
        Guid? workspaceId,
        DateTimeOffset? expiresAt,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(secretHash);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (actor.Kind != ActorKind.Agent)
        {
            throw new ArgumentException("API keys can only be issued to agent actors.", nameof(actor));
        }

        if (!ApiKeyToken.IsBase62(prefix, ApiKeyToken.PrefixLength))
        {
            throw new ArgumentException($"Prefix must be {ApiKeyToken.PrefixLength} Base62 characters.", nameof(prefix));
        }

        if (secretHash.Length != SecretHashLength)
        {
            throw new ArgumentException($"Secret hash must be {SecretHashLength} bytes (SHA-256).", nameof(secretHash));
        }

        var normalizedScopes = scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unknown = normalizedScopes.FirstOrDefault(scope => !KeyScope.IsKnown(scope));
        if (unknown is not null)
        {
            throw new ArgumentException($"Unknown key scope '{unknown}'.", nameof(scopes));
        }

        var createdAt = timeProvider.GetUtcNow();
        if (expiresAt is { } expiry && expiry <= createdAt)
        {
            throw new ArgumentException("Expiry must lie in the future.", nameof(expiresAt));
        }

        return new ApiKey(
            Guid.CreateVersion7(createdAt),
            actor.Id,
            prefix,
            [.. secretHash],
            normalizedScopes,
            workspaceId,
            expiresAt?.ToUniversalTime(),
            createdAt);
    }

    public bool IsRevoked => RevokedAt is not null;

    /// <summary>
    /// True when the key is restricted to a workspace or carries its own scopes. Restricted keys authenticate
    /// without the actor's instance scopes (<c>instance:admin</c>, <c>workspaces:create</c>): they are
    /// workspace credentials only.
    /// </summary>
    public bool IsRestricted => WorkspaceId is not null || Scopes.Count > 0;

    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt is { } expiry && expiry <= now;

    /// <summary>Decides whether the key may authenticate. Revocation wins over expiry, expiry over an inactive owner.</summary>
    public ApiKeyStatus GetStatus(Actor owner, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Id != ActorId)
        {
            throw new ArgumentException("Actor does not own this key.", nameof(owner));
        }

        if (IsRevoked)
        {
            return ApiKeyStatus.Revoked;
        }

        if (IsExpiredAt(now))
        {
            return ApiKeyStatus.Expired;
        }

        return owner.IsActive ? ApiKeyStatus.Active : ApiKeyStatus.ActorInactive;
    }

    /// <summary>Revokes the key. Idempotent: the first revocation time is kept.</summary>
    public void Revoke(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        RevokedAt ??= timeProvider.GetUtcNow();
    }

    /// <summary>
    /// True when <see cref="LastUsedAt"/> is older than <see cref="LastUsedResolution"/> (or unset) and a
    /// use at <paramref name="now"/> should be recorded. Persisting is the caller's job, as a conditional
    /// update so concurrent requests write at most once.
    /// </summary>
    public bool IsLastUsedUpdateDue(DateTimeOffset now) => LastUsedAt is not { } last || now - last >= LastUsedResolution;
}
