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

/// <param name="RecordId">Record to archive.</param>
/// <param name="Reason">Why; stored with the revision.</param>
public sealed record ArchiveRecordRequest(Guid RecordId, string? Reason = null);

/// <summary>
/// Archives a record (soft delete): it stays readable, is hidden from lists by default and cannot be changed until
/// restored. Requires <c>write</c>. Records are never deleted. Creates a revision <c>archived</c>.
/// </summary>
public sealed class ArchiveRecord(IAgcoworkDbContext db, ICurrentActor currentActor, ITenantContext tenant, IActivityRecorder activity, TimeProvider timeProvider)
    : IUseCase<ArchiveRecordRequest, RecordDto>
{
    public async Task<Result<RecordDto>> HandleAsync(ArchiveRecordRequest request, CancellationToken cancellationToken)
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

            if (record.IsArchived)
            {
                return RecordConflicts.Archived;
            }

            var (collection, containerError) = await RecordWrites.LoadWritableContainerAsync(db, record, cancellationToken).ConfigureAwait(false);
            if (containerError is not null)
            {
                return containerError;
            }

            record.Archive(currentActor.Id, timeProvider);
            db.Revisions.Add(Revision.Capture(record, RevisionKind.Archived, request.Reason));
            activity.Record(ActivityTypes.RecordArchived, ActivityTargets.Record, record.Id, record.SpaceId, record.CollectionId);
            if (await RecordWrites.SaveAsync(db, transaction, record.Id, cancellationToken).ConfigureAwait(false) is { } conflict)
            {
                return conflict;
            }

            return RecordMapper.ToDto(record, RecordInput.SchemaOf(collection), includeBody: false);
        }
    }
}
