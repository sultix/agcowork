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

/// <param name="RecordId">Record to append to.</param>
/// <param name="Markdown">Markdown to append, e.g. a log entry. Required.</param>
/// <param name="Section">Heading to append under (e.g. <c>Log</c> or <c>## Log</c>, case-insensitive); created at the end when missing. Omitted: end of the body.</param>
/// <param name="Reason">Why; stored with the revision.</param>
public sealed record AppendToRecordRequest(Guid RecordId, string? Markdown, string? Section = null, string? Reason = null);

/// <summary>
/// Appends Markdown to the body without sending (or knowing) the whole body: the preferred way for agents to write
/// log-like entries (architecture §4.2). Requires <c>write</c>. Concurrent appends never overwrite each other (row lock);
/// each one creates a revision <c>appended</c>. Section rules: <see cref="MarkdownAppend"/>.
/// </summary>
public sealed class AppendToRecord(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    ITenantContext tenant,
    IActivityRecorder activity,
    TimeProvider timeProvider)
    : IUseCase<AppendToRecordRequest, RecordDto>
{
    public async Task<Result<RecordDto>> HandleAsync(AppendToRecordRequest request, CancellationToken cancellationToken)
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
        if (errors.Required("markdown", request.Markdown))
        {
            RecordInput.Body(errors, "markdown", request.Markdown);
        }

        if (errors.Storable("section", request.Section))
        {
            errors.MaxLength("section", request.Section, RecordInput.SectionMaxLength);
        }

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

            var appended = MarkdownAppend.Append(record.Body, request.Markdown!, request.Section);
            if (!Record.IsBodyWithinLimit(appended))
            {
                return new FieldError(
                    "markdown",
                    ErrorCodes.TooLong,
                    $"Appending would make the body exceed {Record.BodyMaxBytes / (1024 * 1024)} MiB (UTF-8). Start a new record and link it instead.").ToValidationError();
            }

            var before = record.Snapshot();
            record.Append(request.Markdown!, request.Section, currentActor.Id, timeProvider);
            db.Revisions.Add(Revision.Capture(record, RevisionKind.Appended, request.Reason));

            var diff = RecordDiff.Between(before, record.Snapshot());
            if (MarkdownAppend.NormalizeSection(request.Section) is { } section)
            {
                diff["section"] = section;
            }

            activity.Record(ActivityTypes.RecordAppended, ActivityTargets.Record, record.Id, record.SpaceId, record.CollectionId, diff);
            if (await RecordWrites.SaveAsync(db, transaction, record.Id, cancellationToken).ConfigureAwait(false) is { } conflict)
            {
                return conflict;
            }

            return RecordMapper.ToDto(record, RecordInput.SchemaOf(collection), includeBody: false);
        }
    }
}
