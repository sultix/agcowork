using Agcowork.Application.Activity;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Records;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Records;

/// <param name="RecordId">Archived record to restore.</param>
/// <param name="Reason">Why; stored with the revision.</param>
public sealed record RestoreRecordRequest(Guid RecordId, string? Reason = null);

/// <summary>
/// Restores an archived record: it becomes writable and listed again. Requires <c>write</c>. Creates a revision
/// <c>restored</c>. A record that is not archived answers 409 <c>record_not_archived</c>.
/// </summary>
public sealed class RestoreRecord(IAgcoworkDbContext db, ICurrentActor currentActor, ITenantContext tenant, IActivityRecorder activity, TimeProvider timeProvider)
    : IUseCase<RestoreRecordRequest, RecordDto>
{
    public async Task<Result<RecordDto>> HandleAsync(RestoreRecordRequest request, CancellationToken cancellationToken)
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
        RecordInput.Reason(errors, request.Reason);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var (transaction, record) = await RecordWrites.BeginAsync(db, request.RecordId, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            if (record is null)
            {
                return new NotFoundError("record");
            }

            if (!record.IsArchived)
            {
                return RecordConflicts.NotArchived;
            }

            var (collection, containerError) = await RecordWrites.LoadWritableContainerAsync(db, record, cancellationToken).ConfigureAwait(false);
            if (containerError is not null)
            {
                return containerError;
            }

            record.Restore(currentActor.Id, timeProvider);
            db.Revisions.Add(Revision.Capture(record, RevisionKind.Restored, request.Reason));
            activity.Record(ActivityTypes.RecordRestored, ActivityTargets.Record, record.Id, record.SpaceId, record.CollectionId);
            if (await RecordWrites.SaveAsync(db, transaction, record.Id, cancellationToken).ConfigureAwait(false) is { } conflict)
            {
                return conflict;
            }

            return RecordMapper.ToDto(record, RecordInput.SchemaOf(collection), includeBody: false);
        }
    }
}
