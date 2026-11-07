using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Records;

/// <param name="RecordId">Record to read.</param>
/// <param name="IncludeBody">Include the Markdown body.</param>
/// <param name="IncludeHistory">Attach the latest n revisions (0..100), newest first.</param>
public sealed record GetRecordRequest(Guid RecordId, bool IncludeBody = false, int IncludeHistory = 0);

/// <summary>One record as JSON, archived ones included. Requires <c>read</c>. The Markdown form is <see cref="GetRecordMarkdown"/>.</summary>
public sealed class GetRecord(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<GetRecordRequest, RecordDto>
{
    public const int MaxHistory = 100;

    public async Task<Result<RecordDto>> HandleAsync(GetRecordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        if (request.IncludeHistory is < 0 or > MaxHistory)
        {
            return new FieldError("includeHistory", ErrorCodes.OutOfRange, $"'includeHistory' must be between 0 and {MaxHistory}.").ToValidationError();
        }

        var record = await db.Records.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.RecordId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return new NotFoundError("record");
        }

        var schema = await RecordReads.SchemaAsync(db, record.CollectionId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RevisionSummaryDto>? history = null;
        if (request.IncludeHistory > 0)
        {
            var revisions = await db.Revisions.AsNoTracking()
                .Where(r => r.RecordId == record.Id)
                .OrderByDescending(r => r.No)
                .Take(request.IncludeHistory)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            history = [.. revisions.Select(RecordMapper.ToSummary)];
        }

        return RecordMapper.ToDto(record, schema, request.IncludeBody, history);
    }
}

/// <summary>Shared reads of the record use cases.</summary>
internal static class RecordReads
{
    /// <summary>Schema of the record's collection, null for free pages.</summary>
    public static async Task<IReadOnlyList<Domain.Collections.Schema.PropertyDefinition>?> SchemaAsync(
        IAgcoworkDbContext db,
        Guid? recordCollectionId,
        CancellationToken cancellationToken) =>
        recordCollectionId is { } collectionId
            ? await db.Collections.AsNoTracking().Where(c => c.Id == collectionId).Select(c => c.Schema).SingleAsync(cancellationToken).ConfigureAwait(false)
            : null;
}
