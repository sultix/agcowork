using Agcowork.Domain.Comments;
using Agcowork.Domain.Identity;

namespace Agcowork.Application.Comments;

/// <summary>A comment on a record. The single JSON shape of comments for REST, MCP and <c>record_get(includeComments)</c>.</summary>
/// <param name="Id">Comment id (<c>commentId</c>; as <c>parentCommentId</c> to answer a root).</param>
/// <param name="RecordId">The record the comment is on.</param>
/// <param name="ParentCommentId">The root this comment answers; null for a root (start of a thread).</param>
/// <param name="AuthorId">Who wrote it; actor id, also usable in a mention <c>[@Name](actor:&lt;id&gt;)</c>.</param>
/// <param name="AuthorName">Current display name of the author (also after the author left the workspace).</param>
/// <param name="AuthorKind"><c>human</c> or <c>agent</c>.</param>
/// <param name="Markdown">The comment as Markdown; mentions are links <c>[@Name](actor:&lt;id&gt;)</c>. Null for a deleted comment (placeholder of a thread that still has replies).</param>
/// <param name="CreatedAt">When it was written (UTC).</param>
/// <param name="EditedAt">When the text was last edited; null if never. Comments keep no revisions.</param>
/// <param name="ResolvedAt">Roots only: when the thread was resolved; null while open.</param>
/// <param name="ResolvedBy">Roots only: who resolved the thread.</param>
/// <param name="DeletedAt">Set for a deleted comment; it appears only as the placeholder of a root with replies.</param>
/// <param name="Replies">
/// Roots only (in thread listings): the replies, oldest first; at most the newest <see cref="ListComments.MaxRepliesPerThread"/>.
/// Null for replies and single comments.
/// </param>
/// <param name="OmittedReplies">Roots only (in thread listings): older replies left out because of the limit; 0 normally.</param>
public sealed record CommentDto(
    Guid Id,
    Guid RecordId,
    Guid? ParentCommentId,
    Guid AuthorId,
    string AuthorName,
    ActorKind AuthorKind,
    string? Markdown,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    DateTimeOffset? ResolvedAt,
    Guid? ResolvedBy,
    DateTimeOffset? DeletedAt,
    IReadOnlyList<CommentDto>? Replies = null,
    int? OmittedReplies = null)
{
    /// <summary>Name shown when an author cannot be found (actors are never deleted, so this does not happen in practice).</summary>
    public const string UnknownAuthor = "Unknown actor";

    /// <param name="comment">The stored comment.</param>
    /// <param name="actors">Names and kinds of actors by id (<see cref="CommentReads.ActorsAsync"/>).</param>
    /// <param name="replies">For roots in a thread listing: the replies.</param>
    /// <param name="omittedReplies">For roots in a thread listing: replies left out.</param>
    internal static CommentDto From(Comment comment, IReadOnlyDictionary<Guid, ActorLabel> actors, IReadOnlyList<CommentDto>? replies = null, int? omittedReplies = null)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(actors);
        var author = actors.GetValueOrDefault(comment.AuthorId);
        return new CommentDto(
            comment.Id,
            comment.RecordId,
            comment.ParentCommentId,
            comment.AuthorId,
            author?.Name ?? UnknownAuthor,
            author?.Kind ?? ActorKind.Agent,
            comment.IsDeleted ? null : comment.BodyMarkdown,
            comment.CreatedAt,
            comment.EditedAt,
            comment.ResolvedAt,
            comment.ResolvedBy,
            comment.DeletedAt,
            replies,
            omittedReplies);
    }
}

/// <summary>The discussion of a record: its threads.</summary>
/// <param name="Threads">
/// Roots, oldest first, each with its replies (oldest first). Deleted comments are left out, except a deleted root that still
/// has replies: it stays as a placeholder without text.
/// </param>
/// <param name="OmittedThreads">
/// Older threads left out because a record shows at most <see cref="Comments.ListComments.MaxThreads"/>; 0 normally. Resolved
/// threads can be hidden with <c>includeResolved=false</c>.
/// </param>
public sealed record CommentThreadsDto(IReadOnlyList<CommentDto> Threads, int OmittedThreads);

/// <summary>One mention of the caller (an entry of the inbox).</summary>
/// <param name="Id">Mention id; send it to <c>mentions/read</c>.</param>
/// <param name="CommentId">The comment with the mention.</param>
/// <param name="ParentCommentId">The thread root, if the comment is a reply; to answer, send this (or <paramref name="CommentId"/> for a root) as <c>parentCommentId</c>.</param>
/// <param name="RecordId">The record the discussion is on.</param>
/// <param name="RecordTitle">Current title of the record.</param>
/// <param name="ByActorId">Who mentioned you.</param>
/// <param name="ByActorName">Current display name of that actor.</param>
/// <param name="Excerpt">The beginning of the comment as plain text, at most 200 characters.</param>
/// <param name="CreatedAt">When you were mentioned (UTC).</param>
/// <param name="ReadAt">When you marked it read; null while unread.</param>
public sealed record MentionDto(
    Guid Id,
    Guid CommentId,
    Guid? ParentCommentId,
    Guid RecordId,
    string RecordTitle,
    Guid ByActorId,
    string ByActorName,
    string Excerpt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

/// <summary>One page of the caller's mentions, newest first.</summary>
/// <param name="Items">The mentions of this page.</param>
/// <param name="NextCursor">Cursor of the next (older) page; null on the last page.</param>
/// <param name="UnreadCount">All unread mentions of the caller in this workspace, independent of filter and page (for a badge).</param>
public sealed record MentionPage(IReadOnlyList<MentionDto> Items, string? NextCursor, int UnreadCount);

/// <summary>Outcome of marking mentions read.</summary>
/// <param name="Marked">How many of the caller's unread mentions were marked read now (ids of others, unknown or already read ones do not count).</param>
/// <param name="UnreadCount">Unread mentions of the caller left in this workspace.</param>
public sealed record MarkMentionsReadResult(int Marked, int UnreadCount);
