using Agcowork.Domain.Common;

namespace Agcowork.Domain.Comments;

/// <summary>
/// One entry of an actor's inbox: <see cref="MentionedActorId"/> was mentioned by <see cref="ByActorId"/> in a comment
/// (architecture §2 "Kommentare und Erwähnungen"). Unique per (comment, mentioned actor); nobody gets one for mentioning
/// themselves. Mentions follow their comment: an edit that drops the link deletes the mention, deleting the comment deletes all
/// of them. <see cref="RecordId"/> is the comment's record, kept here so the inbox needs no join to find it.
/// </summary>
public sealed class Mention : ITenantEntity
{
    private Mention(Guid id, Guid workspaceId, Guid commentId, Guid recordId, Guid mentionedActorId, Guid byActorId, DateTimeOffset createdAt)
    {
        Id = id;
        WorkspaceId = workspaceId;
        CommentId = commentId;
        RecordId = recordId;
        MentionedActorId = mentionedActorId;
        ByActorId = byActorId;
        CreatedAt = createdAt;
    }

    // For EF Core materialization.
    private Mention()
    {
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid CommentId { get; private set; }

    public Guid RecordId { get; private set; }

    public Guid MentionedActorId { get; private set; }

    /// <summary>Who wrote the mention (the comment's author at the time).</summary>
    public Guid ByActorId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the mentioned actor marked it read; null while unread. Set in bulk by the application (<c>MarkMentionsRead</c>).</summary>
    public DateTimeOffset? ReadAt { get; private set; }

    public bool IsRead => ReadAt is not null;

    /// <summary>A mention of <paramref name="mentionedActorId"/> in <paramref name="comment"/>, written by <paramref name="byActorId"/>.</summary>
    public static Mention Create(Comment comment, Guid mentionedActorId, Guid byActorId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (mentionedActorId == Guid.Empty || byActorId == Guid.Empty)
        {
            throw new ArgumentException("Mentioned actor and author must be set.");
        }

        if (mentionedActorId == byActorId)
        {
            throw new ArgumentException("Mentioning yourself creates no mention.", nameof(mentionedActorId));
        }

        if (comment.IsDeleted)
        {
            throw new ArgumentException("A deleted comment mentions nobody.", nameof(comment));
        }

        var createdAt = timeProvider.GetUtcNow();
        return new Mention(Guid.CreateVersion7(createdAt), comment.WorkspaceId, comment.Id, comment.RecordId, mentionedActorId, byActorId, createdAt);
    }
}
