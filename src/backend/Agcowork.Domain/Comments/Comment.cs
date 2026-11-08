using System.Text;
using Agcowork.Domain.Common;
using Agcowork.Domain.Records;

namespace Agcowork.Domain.Comments;

/// <summary>
/// A comment on a record (architecture §2 "Kommentare und Erwähnungen"). Threads have one level: a comment is a root
/// (<see cref="ParentCommentId"/> null) or a reply to a root of the same record. Only roots can be resolved; a resolved
/// thread stays readable. <see cref="BodyMarkdown"/> is canonical Markdown (E2); mentions are <c>[@Name](actor:&lt;uuid&gt;)</c>
/// links in it (<see cref="CommentMarkdown"/>). Comments have no revisions: <see cref="EditedAt"/> marks an edit.
/// Deleting is soft: the body is emptied and <see cref="DeletedAt"/> set, so replies keep their thread.
/// </summary>
/// <remarks>
/// Invariants: same workspace and record as the parent; the parent is a root; only roots are resolved; a deleted comment has an
/// empty body and cannot be edited; the body is at most <see cref="BodyMaxBytes"/> bytes (UTF-8) and not blank. Guards throw:
/// user input is validated beforehand in the application layer.
/// </remarks>
public sealed class Comment : ITenantEntity
{
    /// <summary>64 KiB of UTF-8.</summary>
    public const int BodyMaxBytes = 64 * 1024;

    private Comment(Guid id, Guid workspaceId, Guid recordId, Guid authorId, Guid? parentCommentId, string bodyMarkdown, DateTimeOffset createdAt)
    {
        Id = id;
        WorkspaceId = workspaceId;
        RecordId = recordId;
        AuthorId = authorId;
        ParentCommentId = parentCommentId;
        BodyMarkdown = bodyMarkdown;
        CreatedAt = createdAt;
    }

    // For EF Core materialization.
    private Comment()
    {
        BodyMarkdown = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid RecordId { get; private set; }

    public Guid AuthorId { get; private set; }

    /// <summary>The root this comment answers; null for a root.</summary>
    public Guid? ParentCommentId { get; private set; }

    /// <summary>Markdown; empty once deleted.</summary>
    public string BodyMarkdown { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Time of the last edit of the body; null if never edited.</summary>
    public DateTimeOffset? EditedAt { get; private set; }

    /// <summary>Roots only: when the thread was resolved; null while open.</summary>
    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>Roots only: who resolved the thread.</summary>
    public Guid? ResolvedBy { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsRoot => ParentCommentId is null;

    public bool IsResolved => ResolvedAt is not null;

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>
    /// A new comment on <paramref name="record"/>, as a reply to <paramref name="parent"/> when given (a root of the same record
    /// that is not deleted).
    /// </summary>
    public static Comment Create(Record record, Comment? parent, Guid authorId, string bodyMarkdown, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (authorId == Guid.Empty)
        {
            throw new ArgumentException("Author must be set.", nameof(authorId));
        }

        if (parent is not null)
        {
            if (parent.WorkspaceId != record.WorkspaceId || parent.RecordId != record.Id)
            {
                throw new ArgumentException("A reply belongs to the same workspace and record as its parent.", nameof(parent));
            }

            if (!parent.IsRoot)
            {
                throw new ArgumentException("Threads have one level: a reply answers a root, never another reply.", nameof(parent));
            }

            if (parent.IsDeleted)
            {
                throw new ArgumentException("A deleted comment cannot be answered.", nameof(parent));
            }
        }

        var createdAt = timeProvider.GetUtcNow();
        return new Comment(Guid.CreateVersion7(createdAt), record.WorkspaceId, record.Id, authorId, parent?.Id, GuardBody(bodyMarkdown), createdAt);
    }

    /// <summary>Replaces the body. Returns false (and changes nothing) when it is the same. Throws when deleted.</summary>
    public bool Edit(string bodyMarkdown, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        EnsureNotDeleted();
        var body = GuardBody(bodyMarkdown);
        if (string.Equals(body, BodyMarkdown, StringComparison.Ordinal))
        {
            return false;
        }

        BodyMarkdown = body;
        EditedAt = timeProvider.GetUtcNow();
        return true;
    }

    /// <summary>Resolves the thread of this root. Returns false when it already is resolved. Throws for replies.</summary>
    public bool Resolve(Guid actorId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        EnsureRoot();
        if (actorId == Guid.Empty)
        {
            throw new ArgumentException("Actor must be set.", nameof(actorId));
        }

        if (IsResolved)
        {
            return false;
        }

        ResolvedAt = timeProvider.GetUtcNow();
        ResolvedBy = actorId;
        return true;
    }

    /// <summary>Opens the resolved thread of this root again. Returns false when it is open. Throws for replies.</summary>
    public bool Reopen()
    {
        EnsureRoot();
        if (!IsResolved)
        {
            return false;
        }

        ResolvedAt = null;
        ResolvedBy = null;
        return true;
    }

    /// <summary>Soft delete: empties the body. Returns false when already deleted.</summary>
    public bool Delete(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (IsDeleted)
        {
            return false;
        }

        BodyMarkdown = string.Empty;
        DeletedAt = timeProvider.GetUtcNow();
        return true;
    }

    /// <summary>Whether <paramref name="body"/> fits <see cref="BodyMaxBytes"/>.</summary>
    public static bool IsBodyWithinLimit(string? body) => body is null || Encoding.UTF8.GetByteCount(body) <= BodyMaxBytes;

    private void EnsureRoot()
    {
        if (!IsRoot)
        {
            throw new InvalidOperationException($"Comment {Id} is a reply; only the root of a thread can be resolved or reopened.");
        }
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new InvalidOperationException($"Comment {Id} is deleted and cannot be edited.");
        }
    }

    private static string GuardBody(string bodyMarkdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyMarkdown);
        if (!IsBodyWithinLimit(bodyMarkdown))
        {
            throw new ArgumentException($"The comment must not exceed {BodyMaxBytes} bytes (UTF-8).", nameof(bodyMarkdown));
        }

        return bodyMarkdown;
    }
}
