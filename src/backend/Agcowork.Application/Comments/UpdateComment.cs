using Agcowork.Application.Activity;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Comments;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Comments;

/// <param name="CommentId">Comment to change.</param>
/// <param name="Markdown">New text (replaces the old one); only the author may send it. Mentions are synchronized with it.</param>
/// <param name="Resolved">true resolves the thread, false reopens it; roots only.</param>
public sealed record UpdateCommentRequest(Guid CommentId, string? Markdown = null, bool? Resolved = null);

/// <summary>
/// Edits a comment and/or resolves or reopens its thread, in one transaction. Requires <c>write</c>; the record must be writable
/// (409 otherwise). Text: author only (403 <c>not_author</c>), not on deleted comments (409 <c>comment_deleted</c>); sets
/// <c>editedAt</c>, no revision. Mentions follow the text: new links create mentions (members only, 400 <c>not_member</c>),
/// removed links delete theirs, kept ones stay with their read state. Resolving/reopening: roots only (400 <c>not_root</c>), any
/// member with <c>write</c>; resolving a resolved thread (or reopening an open one) changes nothing. Events:
/// <c>comment.updated</c> (diff <c>mentionedActorIds</c>: the added mentions), <c>comment.resolved</c>, <c>comment.reopened</c>;
/// none when nothing changed. Used by <c>PATCH /comments/{id}</c>, <c>POST …/resolve|reopen</c> and MCP <c>comment_update</c>.
/// </summary>
public sealed class UpdateComment(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    ITenantContext tenant,
    IActivityRecorder activity,
    TimeProvider timeProvider)
    : IUseCase<UpdateCommentRequest, CommentDto>
{
    public async Task<Result<CommentDto>> HandleAsync(UpdateCommentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireWritable() is { } archived)
        {
            return archived;
        }

        if (tenant.RequireScope(WorkspaceScope.Write) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        if (request.Markdown is null && request.Resolved is null)
        {
            errors.Add(CommentInput.MarkdownField, ErrorCodes.Required, "Send 'markdown' (new text) and/or 'resolved' (true resolves the thread, false reopens it).");
            return errors.ToError();
        }

        var scan = request.Markdown is null ? null : CommentInput.Markdown(errors, request.Markdown);
        if (errors.Any)
        {
            return errors.ToError();
        }

        if (await CommentWrites.RecordOfAsync(db, request.CommentId, cancellationToken).ConfigureAwait(false) is not { } recordId)
        {
            return new NotFoundError("comment");
        }

        var (transaction, record) = await CommentWrites.BeginAsync(db, recordId, request.CommentId, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var comment = await db.Comments.SingleOrDefaultAsync(c => c.Id == request.CommentId, cancellationToken).ConfigureAwait(false);
            if (record is null || comment is null)
            {
                return new NotFoundError("comment");
            }

            if (await CommentWrites.RequireWritableAsync(db, record, cancellationToken).ConfigureAwait(false) is { } readOnly)
            {
                return readOnly;
            }

            if (scan is not null)
            {
                if (comment.AuthorId != currentActor.Id)
                {
                    return CommentErrors.NotAuthor;
                }

                if (comment.IsDeleted)
                {
                    return CommentErrors.Deleted(CommentInput.MarkdownField);
                }
            }

            if (request.Resolved is not null && !comment.IsRoot)
            {
                return new FieldError(
                    "resolved",
                    ErrorCodes.NotRoot,
                    $"Only the root of a thread can be resolved or reopened: comment {comment.ParentCommentId}.").ToValidationError();
            }

            if (scan is not null && await EditAsync(comment, request.Markdown!, scan, errors, cancellationToken).ConfigureAwait(false) is { } added)
            {
                CommentWrites.Record(activity, ActivityTypes.CommentUpdated, comment, record, added);
            }

            if (errors.Any)
            {
                return errors.ToError();
            }

            if (request.Resolved == true && comment.Resolve(currentActor.Id, timeProvider))
            {
                CommentWrites.Record(activity, ActivityTypes.CommentResolved, comment, record, []);
            }
            else if (request.Resolved == false && comment.Reopen())
            {
                CommentWrites.Record(activity, ActivityTypes.CommentReopened, comment, record, []);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await CommentReads.ToDtoAsync(db, comment, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replaces the text and synchronizes the mentions. Returns the newly mentioned actors, or null when the text did not change
    /// (or a new mention is no member: then <paramref name="errors"/> has <c>not_member</c> and nothing changed).
    /// </summary>
    private async Task<IReadOnlyList<Guid>?> EditAsync(Comment comment, string markdown, MentionScan scan, FieldErrors errors, CancellationToken cancellationToken)
    {
        if (string.Equals(comment.BodyMarkdown, markdown, StringComparison.Ordinal))
        {
            return null;
        }

        // Members are checked only for new links: an actor mentioned before may have left the workspace since, which must not
        // block editing the rest of the comment.
        var before = CommentMarkdown.FindMentions(comment.BodyMarkdown).ActorIds;
        var wanted = scan.ActorIds.Where(id => id != comment.AuthorId).ToList();
        await CommentInput.CheckMembersAsync(db, errors, [.. wanted.Except(before)], cancellationToken).ConfigureAwait(false);
        if (errors.Any)
        {
            return null;
        }

        var existing = await db.Mentions.Where(m => m.CommentId == comment.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        db.Mentions.RemoveRange(existing.Where(m => !wanted.Contains(m.MentionedActorId)));
        var added = wanted.Where(id => existing.All(m => m.MentionedActorId != id)).ToList();
        db.Mentions.AddRange(added.Select(id => Mention.Create(comment, id, comment.AuthorId, timeProvider)));
        comment.Edit(markdown, timeProvider);
        return added;
    }
}
