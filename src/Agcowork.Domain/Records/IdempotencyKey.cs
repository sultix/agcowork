using Agcowork.Domain.Common;

namespace Agcowork.Domain.Records;

/// <summary>
/// Remembers which record a create request with an <c>Idempotency-Key</c> produced, per workspace and actor, so a
/// retried request returns the same record instead of a duplicate. Valid for <see cref="Lifetime"/>.
/// </summary>
public sealed class IdempotencyKey : ITenantEntity
{
    public const int KeyMaxLength = 255;

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private IdempotencyKey(Guid id, Guid workspaceId, Guid actorId, string key, Guid recordId, DateTimeOffset at)
    {
        Id = id;
        WorkspaceId = workspaceId;
        ActorId = actorId;
        Key = key;
        RecordId = recordId;
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

    public DateTimeOffset At { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now - At >= Lifetime;

    public static IdempotencyKey Create(Guid workspaceId, Guid actorId, string key, Guid recordId, TimeProvider timeProvider)
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
        return new IdempotencyKey(Guid.CreateVersion7(at), workspaceId, actorId, key, recordId, at);
    }
}
