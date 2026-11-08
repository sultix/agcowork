using System.Text.Json.Nodes;
using Agcowork.Application.Activity;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Comments;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agcowork.Application.Comments;

/// <summary>Errors of the comment module. Codes are part of the API contract.</summary>
public static class CommentErrors
{
    /// <summary>403 <c>not_author</c>: only the author edits a comment.</summary>
    public static ForbiddenError NotAuthor { get; } = new("only the author can edit a comment") { Code = ErrorCodes.NotAuthor };

    /// <summary>403 <c>not_author</c>: deleting needs the author or the <c>admin</c> scope.</summary>
    public static ForbiddenError NotAuthorOrAdmin { get; } =
        new("only the author or a member with admin can delete a comment") { Code = ErrorCodes.NotAuthor };

    /// <summary>409 <c>comment_deleted</c>: a deleted comment can be neither edited nor answered.</summary>
    public static ConflictError Deleted(string? field = null) =>
        new("comment_deleted", "The comment is deleted: it can be neither edited nor answered. Start a new thread instead.", field);

}

/// <summary>Name and kind of an actor, for authors and mentioners.</summary>
internal sealed record ActorLabel(string Name, ActorKind Kind);

/// <summary>Input rules of the comment use cases.</summary>
internal static class CommentInput
{
    public const string MarkdownField = "markdown";

    /// <summary>Field errors per kind (<c>not_member</c>, <c>invalid_value</c>) in one response.</summary>
    public const int MaxFieldErrors = 20;

    /// <summary>
    /// Checks the text (required, storable, at most <see cref="Comment.BodyMaxBytes"/>) and its <c>actor:</c> links. Returns the
    /// mentions found, or null when the text is invalid (errors added).
    /// </summary>
    public static MentionScan? Markdown(FieldErrors errors, string? markdown)
    {
        if (!errors.Required(MarkdownField, markdown) || !errors.Storable(MarkdownField, markdown))
        {
            return null;
        }

        if (!Comment.IsBodyWithinLimit(markdown))
        {
            errors.Add(MarkdownField, ErrorCodes.TooLong, $"A comment must not exceed {Comment.BodyMaxBytes / 1024} KiB (UTF-8). Put long content into the record and link it.");
            return null;
        }

        var scan = CommentMarkdown.FindMentions(markdown);
        AddCapped(errors, ErrorCodes.InvalidValue, scan.InvalidTargets, target =>
            $"'{target}' is no actor id. A mention is a link [@Name](actor:<uuid>) with the actor id of a member of this workspace.");

        return scan.InvalidTargets.Count == 0 ? scan : null;
    }

    /// <summary>Adds <c>not_member</c> on <c>markdown</c> for every id in <paramref name="actorIds"/> that is no member of the workspace.</summary>
    public static async Task CheckMembersAsync(IAgcoworkDbContext db, FieldErrors errors, IReadOnlyCollection<Guid> actorIds, CancellationToken cancellationToken)
    {
        if (actorIds.Count == 0)
        {
            return;
        }

        var ids = actorIds.ToList();
        // Memberships are tenant-filtered: an actor of another workspace is no member here.
        var members = await db.Memberships.AsNoTracking()
            .Where(m => ids.Contains(m.ActorId))
            .Select(m => m.ActorId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        AddCapped(errors, ErrorCodes.NotMember, [.. ids.Except(members)], id =>
            $"actor:{id:D} is no member of this workspace and cannot be mentioned. Mention members only (see the members of the workspace).");
    }

    /// <summary>
    /// One error on <c>markdown</c> per item, at most <see cref="MaxFieldErrors"/>; the last one says how many more there are, so a
    /// comment full of bad links cannot produce an unbounded error list.
    /// </summary>
    private static void AddCapped<T>(FieldErrors errors, string code, IReadOnlyList<T> items, Func<T, string> message)
    {
        for (var i = 0; i < items.Count && i < MaxFieldErrors; i++)
        {
            var more = items.Count - MaxFieldErrors;
            var text = message(items[i]);
            errors.Add(MarkdownField, code, i == MaxFieldErrors - 1 && more > 0 ? $"{text} (and {more} more like this)" : text);
        }
    }
}

/// <summary>Loading and locking in the writing comment use cases.</summary>
internal static class CommentWrites
{
    /// <summary>
    /// Opens the transaction of a comment change and takes the locks in the fixed order collection → record → comment: a shared lock
    /// on the record's collection and on the record (a comment does not change the record, but an archive must not run in
    /// between), then, with <paramref name="commentId"/>, an exclusive lock on that comment. Returns the record (not tracked).
    /// </summary>
    public static async Task<(IDbContextTransaction Transaction, Record? Record)> BeginAsync(
        IAgcoworkDbContext db,
        Guid recordId,
        Guid? commentId,
        CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var collectionId = await db.Records.AsNoTracking()
            .Where(r => r.Id == recordId)
            .Select(r => r.CollectionId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (collectionId is { } id)
        {
            await db.LockCollectionSharedAsync(id, cancellationToken).ConfigureAwait(false);
        }

        await db.LockRecordSharedAsync(recordId, cancellationToken).ConfigureAwait(false);
        if (commentId is { } comment)
        {
            await db.LockCommentAsync(comment, cancellationToken).ConfigureAwait(false);
        }

        var record = await db.Records.AsNoTracking().SingleOrDefaultAsync(r => r.Id == recordId, cancellationToken).ConfigureAwait(false);
        return (transaction, record);
    }

    /// <summary>
    /// Archived records (and records of an archived collection or space) are read-only, their discussion included: 409 like
    /// every record write (<c>record_archived</c>, <c>collection_archived</c>, <c>space_archived</c>).
    /// </summary>
    public static async Task<Error?> RequireWritableAsync(IAgcoworkDbContext db, Record record, CancellationToken cancellationToken)
    {
        if (record.IsArchived)
        {
            return Records.RecordConflicts.Archived;
        }

        var (_, error) = await Records.RecordWrites.LoadWritableContainerAsync(db, record, cancellationToken).ConfigureAwait(false);
        return error;
    }

    /// <summary>Record id of a comment of this workspace; null when there is none.</summary>
    public static Task<Guid?> RecordOfAsync(IAgcoworkDbContext db, Guid commentId, CancellationToken cancellationToken) =>
        db.Comments.AsNoTracking().Where(c => c.Id == commentId).Select(c => (Guid?)c.RecordId).SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Records the event of a comment change: target <c>comment</c>, space and collection of the record and the record as related
    /// record (so the activity filters, including <c>recordId</c>, find it), diff with <c>recordId</c>, <c>parentCommentId</c> and <c>mentionedActorIds</c> (new mentions).
    /// </summary>
    public static void Record(IActivityRecorder activity, string type, Comment comment, Record record, IEnumerable<Guid> mentionedActorIds)
    {
        var diff = new ActivityDiff()
            .Set("recordId", JsonValue.Create(comment.RecordId))
            .Set("parentCommentId", comment.ParentCommentId is { } parent ? JsonValue.Create(parent) : null)
            .Set("mentionedActorIds", new JsonArray([.. mentionedActorIds.Select(id => (JsonNode?)JsonValue.Create(id))]))
            .ToJson();
        activity.Record(type, ActivityTargets.Comment, comment.Id, record.SpaceId, record.CollectionId, diff, relatedRecordId: comment.RecordId);
    }
}

/// <summary>Shared reads of the comment use cases.</summary>
internal static class CommentReads
{
    /// <summary>Names and kinds of <paramref name="actorIds"/>. Actors are global, so a former member keeps its name.</summary>
    public static async Task<IReadOnlyDictionary<Guid, ActorLabel>> ActorsAsync(IAgcoworkDbContext db, IEnumerable<Guid> actorIds, CancellationToken cancellationToken)
    {
        var ids = actorIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, ActorLabel>();
        }

        var actors = await db.Actors.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.Kind })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return actors.ToDictionary(a => a.Id, a => new ActorLabel(a.Name, a.Kind));
    }

    /// <summary>One comment with its author.</summary>
    public static async Task<CommentDto> ToDtoAsync(IAgcoworkDbContext db, Comment comment, CancellationToken cancellationToken) =>
        CommentDto.From(comment, await ActorsAsync(db, [comment.AuthorId], cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The threads of a record (<see cref="ListComments"/>): the newest <see cref="ListComments.MaxThreads"/> visible roots, shown
    /// oldest first, each with its newest <see cref="ListComments.MaxRepliesPerThread"/> replies that are not deleted. A root is visible when it is not deleted or still has replies,
    /// and, unless <paramref name="includeResolved"/>, when it is open. Four queries plus one for the authors, independent of the
    /// number of threads.
    /// </summary>
    public static async Task<CommentThreadsDto> ThreadsAsync(IAgcoworkDbContext db, Guid recordId, bool includeResolved, CancellationToken cancellationToken)
    {
        var roots = db.Comments.AsNoTracking()
            .Where(c => c.RecordId == recordId && c.ParentCommentId == null)
            .Where(c => c.DeletedAt == null || db.Comments.Any(r => r.ParentCommentId == c.Id && r.DeletedAt == null));
        if (!includeResolved)
        {
            roots = roots.Where(c => c.ResolvedAt == null);
        }

        var newest = await roots
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .Take(ListComments.MaxThreads + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var omitted = 0;
        if (newest.Count > ListComments.MaxThreads)
        {
            newest.RemoveAt(newest.Count - 1);
            omitted = await roots.CountAsync(cancellationToken).ConfigureAwait(false) - ListComments.MaxThreads;
        }

        newest.Reverse();
        var rootIds = newest.Select(c => c.Id).ToList();
        var visibleReplies = db.Comments.AsNoTracking()
            .Where(c => c.RecordId == recordId && c.ParentCommentId != null && rootIds.Contains(c.ParentCommentId.Value) && c.DeletedAt == null);

        // The newest replies of each thread (one query, a lateral join per root), shown oldest first, and how many each thread has.
        var replies = rootIds.Count == 0
            ? []
            : await db.Comments.AsNoTracking()
                .Where(root => rootIds.Contains(root.Id))
                .SelectMany(root => db.Comments.AsNoTracking()
                    .Where(c => c.ParentCommentId == root.Id && c.DeletedAt == null)
                    .OrderByDescending(c => c.CreatedAt)
                    .ThenByDescending(c => c.Id)
                    .Take(ListComments.MaxRepliesPerThread))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var replyCounts = replies.Count == 0
            ? new Dictionary<Guid, int>()
            : await visibleReplies
                .GroupBy(c => c.ParentCommentId!.Value)
                .Select(g => new { Root = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Root, g => g.Count, cancellationToken)
                .ConfigureAwait(false);

        var actors = await ActorsAsync(db, newest.Concat(replies).Select(c => c.AuthorId), cancellationToken).ConfigureAwait(false);
        var repliesByRoot = replies.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToLookup(c => c.ParentCommentId!.Value);
        return new CommentThreadsDto(
            [.. newest.Select(root =>
            {
                var shown = repliesByRoot[root.Id].Select(reply => CommentDto.From(reply, actors)).ToList();
                return CommentDto.From(root, actors, shown, replyCounts.GetValueOrDefault(root.Id) - shown.Count);
            })],
            omitted);
    }
}
