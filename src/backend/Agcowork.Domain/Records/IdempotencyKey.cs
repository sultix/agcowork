using Agcowork.Domain.Common;

namespace Agcowork.Domain.Records;

/// <summary>
/// Remembers which record a create request with an <c>Idempotency-Key</c> produced, per workspace and actor, so a
/// retried request returns the same record instead of a duplicate. Valid for <see cref="Lifetime"/>. Adding a comment
/// (AP-029) uses the same keys: then <see cref="CommentId"/> is the comment and <see cref="RecordId"/> its record. A key
/// belongs to one kind of request; reusing it for the other kind is a conflict.
/// </summary>
public sealed class IdempotencyKey : ITenantEntity
{
    public const int KeyMaxLength = 255;

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private IdempotencyKey(Guid id, Guid workspaceId, Guid actorId, string key, Guid recordId, Guid? commentId, DateTimeOffset at)
    {
        Id = id;
        WorkspaceId = workspaceId;
        ActorId = actorId;
        Key = key;
        RecordId = recordId;
        CommentId = commentId;
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

    public DateTimeOffset At { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now - At >= Lifetime;

    public static IdempotencyKey Create(Guid workspaceId, Guid actorId, string key, Guid recordId, TimeProvider timeProvider) =>
        Create(workspaceId, actorId, key, recordId, commentId: null, timeProvider);

    /// <summary>A key of an add-comment request: <paramref name="commentId"/> on <paramref name="recordId"/>.</summary>
    public static IdempotencyKey ForComment(Guid workspaceId, Guid actorId, string key, Guid recordId, Guid commentId, TimeProvider timeProvider)
    {
        if (commentId == Guid.Empty)
        {
            throw new ArgumentException("Comment must be set.", nameof(commentId));
        }

        return Create(workspaceId, actorId, key, recordId, commentId, timeProvider);
    }

    private static IdempotencyKey Create(Guid workspaceId, Guid actorId, string key, Guid recordId, Guid? commentId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
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
        return new IdempotencyKey(Guid.CreateVersion7(at), workspaceId, actorId, key, recordId, commentId, at);
    }
}
