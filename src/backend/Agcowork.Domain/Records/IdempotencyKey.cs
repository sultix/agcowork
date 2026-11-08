using Agcowork.Domain.Common;

namespace Agcowork.Domain.Records;

/// <summary>
/// Remembers which record a create request with an <c>Idempotency-Key</c> produced, per workspace and actor, so a
/// retried request returns the same record instead of a duplicate. Valid for <see cref="Lifetime"/>. Adding a comment
/// (AP-029) uses the same keys: then <see cref="CommentId"/> is the comment and <see cref="RecordId"/> its record. A key
/// belongs to one kind of request; reusing it for the other kind is a conflict. Since AP-035 a key also remembers a hash of
/// the request (<see cref="RequestHash"/>): only a request with the same hash is a retry; another one with the same key is a
/// conflict instead of a replay of something unrelated.
/// </summary>
public sealed class IdempotencyKey : ITenantEntity
{
    public const int KeyMaxLength = 255;

    /// <summary>Length of <see cref="RequestHash"/>: SHA-256 as lowercase hex.</summary>
    public const int RequestHashLength = 64;

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private IdempotencyKey(Guid id, Guid workspaceId, Guid actorId, string key, Guid recordId, Guid? commentId, string requestHash, DateTimeOffset at)
    {
        Id = id;
        WorkspaceId = workspaceId;
        ActorId = actorId;
        Key = key;
        RecordId = recordId;
        CommentId = commentId;
        RequestHash = requestHash;
        At = at;
    }

    // For EF Core materialization.
    private IdempotencyKey()
    {
        Key = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid ActorId { get; private set; }

    /// <summary>The client-chosen key, exactly as sent.</summary>
    public string Key { get; private set; }

    public Guid RecordId { get; private set; }

    /// <summary>The comment an add-comment request produced; null for a record create.</summary>
    public Guid? CommentId { get; private set; }

    public bool IsForComment => CommentId is not null;

    /// <summary>
    /// SHA-256 (lowercase hex) of the normalized request that stored the key (target and content, see
    /// <c>Application.Records.IdempotencyFingerprint</c>); null for keys stored before AP-035, which replay without a check.
    /// </summary>
    public string? RequestHash { get; private set; }

    public DateTimeOffset At { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now - At >= Lifetime;

    /// <summary>
    /// Whether a request with <paramref name="requestHash"/> is a retry of the one that stored this key: same hash, or a key
    /// from before AP-035 without hash (accepted as before; such keys expire within 24 h).
    /// </summary>
    public bool Matches(string requestHash) => RequestHash is null || string.Equals(RequestHash, requestHash, StringComparison.Ordinal);

    public static IdempotencyKey Create(Guid workspaceId, Guid actorId, string key, Guid recordId, string requestHash, TimeProvider timeProvider) =>
        Create(workspaceId, actorId, key, recordId, commentId: null, requestHash, timeProvider);

    /// <summary>A key of an add-comment request: <paramref name="commentId"/> on <paramref name="recordId"/>.</summary>
    public static IdempotencyKey ForComment(Guid workspaceId, Guid actorId, string key, Guid recordId, Guid commentId, string requestHash, TimeProvider timeProvider)
    {
        if (commentId == Guid.Empty)
        {
            throw new ArgumentException("Comment must be set.", nameof(commentId));
        }

        return Create(workspaceId, actorId, key, recordId, commentId, requestHash, timeProvider);
    }

    private static IdempotencyKey Create(Guid workspaceId, Guid actorId, string key, Guid recordId, Guid? commentId, string requestHash, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        if (requestHash.Length != RequestHashLength)
        {
            throw new ArgumentException($"Request hash must be {RequestHashLength} hex characters.", nameof(requestHash));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > KeyMaxLength)
        {
            throw new ArgumentException($"Key must not exceed {KeyMaxLength} characters.", nameof(key));
        }

        if (workspaceId == Guid.Empty || actorId == Guid.Empty || recordId == Guid.Empty)
        {
            throw new ArgumentException("Workspace, actor and record must be set.");
        }

        var at = timeProvider.GetUtcNow();
        return new IdempotencyKey(Guid.CreateVersion7(at), workspaceId, actorId, key, recordId, commentId, requestHash, at);
    }
}
