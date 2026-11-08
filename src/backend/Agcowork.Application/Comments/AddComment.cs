using Agcowork.Application.Activity;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Records;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Comments;
using Agcowork.Domain.Records;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Comments;

/// <param name="RecordId">Record to comment on.</param>
/// <param name="Markdown">The comment, Markdown, max. 64 KiB. Mention members with <c>[@Name](actor:&lt;uuid&gt;)</c>.</param>
/// <param name="ParentCommentId">Root comment to answer (a reply); omitted: a new thread.</param>
/// <param name="IdempotencyKey">From the <c>Idempotency-Key</c> header: the same key by the same actor within 24 h returns the first comment.</param>
public sealed record AddCommentRequest(Guid RecordId, string? Markdown, Guid? ParentCommentId = null, string? IdempotencyKey = null);

/// <summary>
/// Adds a comment to a record: a new thread, or with <c>parentCommentId</c> a reply to a root of the same record. Requires
/// <c>write</c>; the record must be writable (not archived, nor its collection or space: 409). Every <c>actor:</c> link in the
/// text is a mention: each mentioned member gets one entry in its inbox (<see cref="ListMentions"/>), the author none for
/// themselves; non-members are rejected (400 <c>not_member</c>). The record gets no revision. Comment, mentions and the event
/// <c>comment.created</c> are stored in one transaction. Idempotent with a key, like <see cref="CreateRecord"/>.
/// </summary>
public sealed class AddComment(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    ITenantContext tenant,
    IActivityRecorder activity,
    TimeProvider timeProvider)
    : IUseCase<AddCommentRequest, CommentDto>
{
    public const string IdempotencyKeyField = "Idempotency-Key";

    public async Task<Result<CommentDto>> HandleAsync(AddCommentRequest request, CancellationToken cancellationToken)
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
        var idempotencyKey = request.IdempotencyKey;
        if (idempotencyKey is not null && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > IdempotencyKey.KeyMaxLength))
        {
            errors.Add(IdempotencyKeyField, ErrorCodes.InvalidFormat, $"The Idempotency-Key header must be 1 to {IdempotencyKey.KeyMaxLength} characters, e.g. a UUID.");
            return errors.ToError();
        }

        if (idempotencyKey is not null && !StorableText.IsStorable(idempotencyKey))
        {
            errors.Add(IdempotencyKeyField, ErrorCodes.InvalidValue, $"The Idempotency-Key header {StorableText.Hint}");
            return errors.ToError();
        }

        IdempotencyKey? expiredKey = null;
        if (idempotencyKey is not null)
        {
            var existing = await FindKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null && !existing.IsExpired(timeProvider.GetUtcNow()))
            {
                return await ReplayAsync(existing, request.RecordId, cancellationToken).ConfigureAwait(false);
            }

            expiredKey = existing;
        }

        var scan = CommentInput.Markdown(errors, request.Markdown);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var (transaction, record) = await CommentWrites.BeginAsync(db, request.RecordId, request.ParentCommentId, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            if (record is null)
            {
                return new NotFoundError("record");
            }

            if (await CommentWrites.RequireWritableAsync(db, record, cancellationToken).ConfigureAwait(false) is { } readOnly)
            {
                return readOnly;
            }

            Comment? parent = null;
            if (request.ParentCommentId is { } parentId)
            {
                parent = await db.Comments.AsNoTracking().SingleOrDefaultAsync(c => c.Id == parentId && c.RecordId == record.Id, cancellationToken).ConfigureAwait(false);
                if (parent is null)
                {
                    return new FieldError("parentCommentId", ErrorCodes.NotFound, "The record has no comment with this id (see GET …/records/{id}/comments).").ToValidationError();
                }

                if (!parent.IsRoot)
                {
                    return new FieldError(
                        "parentCommentId",
                        ErrorCodes.NotRoot,
                        $"Threads have one level: answer the root of the thread, comment {parent.ParentCommentId}, not a reply.").ToValidationError();
                }

                if (parent.IsDeleted)
                {
                    return CommentErrors.Deleted("parentCommentId");
                }
            }

            var mentioned = scan!.ActorIds.Where(id => id != currentActor.Id).ToList();
            await CommentInput.CheckMembersAsync(db, errors, mentioned, cancellationToken).ConfigureAwait(false);
            if (errors.Any)
            {
                return errors.ToError();
            }

            var comment = Comment.Create(record, parent, currentActor.Id, request.Markdown!, timeProvider);
            db.Comments.Add(comment);
            db.Mentions.AddRange(mentioned.Select(actorId => Mention.Create(comment, actorId, currentActor.Id, timeProvider)));
            CommentWrites.Record(activity, ActivityTypes.CommentCreated, comment, record, mentioned);

            if (idempotencyKey is not null)
            {
                if (expiredKey is not null)
                {
                    await db.IdempotencyKeys.Where(k => k.Id == expiredKey.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                }

                db.IdempotencyKeys.Add(IdempotencyKey.ForComment(tenant.WorkspaceId, currentActor.Id, idempotencyKey, record.Id, comment.Id, timeProvider));
            }

            if (await db.SaveChangesOrConflictAsync(cancellationToken, RecordConflicts.IdempotencyKey).ConfigureAwait(false) is { } conflict)
            {
                // A concurrent request with the same key won: answer with its result (nothing of ours was stored).
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return await FindKeyAsync(idempotencyKey!, cancellationToken).ConfigureAwait(false) is { } winner
                    ? await ReplayAsync(winner, request.RecordId, cancellationToken).ConfigureAwait(false)
                    : conflict;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await CommentReads.ToDtoAsync(db, comment, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<IdempotencyKey?> FindKeyAsync(string key, CancellationToken cancellationToken) =>
        db.IdempotencyKeys.AsNoTracking().SingleOrDefaultAsync(k => k.ActorId == currentActor.Id && k.Key == key, cancellationToken);

    /// <summary>
    /// The first result of a retried request. A key of a record create, or of a comment on another record, is no retry of this
    /// request: 409 <c>idempotency_key_reused</c> instead of answering with something unrelated.
    /// </summary>
    private async Task<Result<CommentDto>> ReplayAsync(IdempotencyKey key, Guid recordId, CancellationToken cancellationToken)
    {
        if (key.CommentId is not { } commentId || key.RecordId != recordId)
        {
            return RecordConflicts.IdempotencyKeyReused(IdempotencyKeyField);
        }

        var comment = await db.Comments.AsNoTracking().SingleAsync(c => c.Id == commentId, cancellationToken).ConfigureAwait(false);
        return await CommentReads.ToDtoAsync(db, comment, cancellationToken).ConfigureAwait(false);
    }
}
