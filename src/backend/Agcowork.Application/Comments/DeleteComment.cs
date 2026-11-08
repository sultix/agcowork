using Agcowork.Application.Activity;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Comments;

/// <param name="CommentId">Comment to delete.</param>
public sealed record DeleteCommentRequest(Guid CommentId);

/// <summary>
/// Deletes a comment softly: the text is emptied, the comment stays as placeholder while its thread has replies, and its mentions
/// are deleted (they leave the inboxes). Requires <c>write</c>, and the author or the <c>admin</c> scope (403 <c>not_author</c>
/// otherwise); the record must be writable (409). Deleting a deleted comment changes nothing. Event <c>comment.deleted</c>.
/// </summary>
public sealed class DeleteComment(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    ITenantContext tenant,
    IActivityRecorder activity,
    TimeProvider timeProvider)
    : IUseCase<DeleteCommentRequest>
{
    public async Task<Result> HandleAsync(DeleteCommentRequest request, CancellationToken cancellationToken)
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

            if (comment.AuthorId != currentActor.Id && tenant.RequireScope(WorkspaceScope.Admin) is not null)
            {
                return CommentErrors.NotAuthorOrAdmin;
            }

            if (!comment.Delete(timeProvider))
            {
                return Result.Success();
            }

            db.Mentions.RemoveRange(await db.Mentions.Where(m => m.CommentId == comment.Id).ToListAsync(cancellationToken).ConfigureAwait(false));
            CommentWrites.Record(activity, ActivityTypes.CommentDeleted, comment, record, []);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
    }
}
