using System.Text.Json;
using Agcowork.Application.Activity;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Records;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Records;

/// <summary>Partial change of a record.</summary>
/// <param name="RecordId">Record to change.</param>
/// <param name="Title">New title; null leaves it unchanged.</param>
/// <param name="Properties">Patch: only the sent keys change, <c>null</c> removes a value (not allowed for required properties).</param>
/// <param name="Body">New Markdown body (replaces the old one, max. 2 MiB); null leaves it unchanged. To add text, use append.</param>
/// <param name="ExpectedRevision">If set, the change applies only when the record is still at this revision (409 <c>revision_mismatch</c> otherwise). Without it the last writer wins.</param>
/// <param name="Reason">Why the change is made; stored with the revision.</param>
public sealed record UpdateRecordRequest(
    Guid RecordId,
    string? Title = null,
    IReadOnlyDictionary<string, JsonElement>? Properties = null,
    string? Body = null,
    int? ExpectedRevision = null,
    string? Reason = null);

/// <summary>
/// Changes title, properties (as a patch) and/or body of a record. Requires <c>write</c>. With <c>expectedRevision</c>
/// two agents cannot overwrite each other: the second gets 409 <c>revision_mismatch</c> with <c>currentRevision</c> and the
/// keys changed since. A change that changes nothing creates no revision. Record, revision and activity event are stored
/// in one transaction under a row lock of the record.
/// </summary>
public sealed class UpdateRecord(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    ITenantContext tenant,
    PropertyValueValidator validator,
    IActivityRecorder activity,
    TimeProvider timeProvider)
    : IUseCase<UpdateRecordRequest, RecordDto>
{
    public async Task<Result<RecordDto>> HandleAsync(UpdateRecordRequest request, CancellationToken cancellationToken)
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
        var patch = RecordInput.Copy(request.Properties);
        var title = RecordInput.TakeTitle(errors, request.Title, patch);
        if (title is not null)
        {
            RecordInput.Title(errors, title);
        }

        RecordInput.Body(errors, "body", request.Body);
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

            if (request.ExpectedRevision is { } expected && expected != record.RevisionNo)
            {
                return await MismatchAsync(record, expected, cancellationToken).ConfigureAwait(false);
            }

            var schema = RecordInput.SchemaOf(collection);
            var normalized = PropertyValueNormalizer.Normalize(schema, patch);
            errors.AddRange(await RecordInput.ValidatePropertiesAsync(validator, schema, title, normalized, PropertyValidationMode.Partial, cancellationToken).ConfigureAwait(false));
            if (errors.Any)
            {
                return errors.ToError();
            }

            var properties = new Dictionary<string, JsonElement>(record.Properties, StringComparer.Ordinal);
            foreach (var (key, value) in normalized)
            {
                if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    properties.Remove(key);
                }
                else
                {
                    properties[key] = value;
                }
            }

            var before = record.Snapshot();
            if (!record.Change(new RecordSnapshot(title ?? record.Title, properties, request.Body ?? record.Body), currentActor.Id, timeProvider))
            {
                return RecordMapper.ToDto(record, schema, includeBody: false);
            }

            db.Revisions.Add(Revision.Capture(record, RevisionKind.Updated, request.Reason));
            activity.Record(ActivityTypes.RecordUpdated, ActivityTargets.Record, record.Id, record.SpaceId, record.CollectionId, RecordDiff.Between(before, record.Snapshot()));
            if (await RecordWrites.SaveAsync(db, transaction, record.Id, cancellationToken).ConfigureAwait(false) is { } conflict)
            {
                return conflict;
            }

            return RecordMapper.ToDto(record, schema, includeBody: false);
        }
    }

    private async Task<Error> MismatchAsync(Record record, int expected, CancellationToken cancellationToken)
    {
        var then = await db.Revisions.AsNoTracking()
            .SingleOrDefaultAsync(r => r.RecordId == record.Id && r.No == expected, cancellationToken)
            .ConfigureAwait(false);
        return RecordConflicts.RevisionMismatch(record.RevisionNo, then is null ? null : RecordDiff.ChangedKeys(then.Snapshot(), record.Snapshot()));
    }
}
