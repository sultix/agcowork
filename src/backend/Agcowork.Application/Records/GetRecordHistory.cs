using System.Globalization;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Records;

/// <param name="RecordId">Record whose revisions to list.</param>
/// <param name="Limit">Page size, 1..200, default 50.</param>
/// <param name="Cursor"><c>nextCursor</c> of the previous page.</param>
public sealed record GetRecordHistoryRequest(Guid RecordId, int? Limit = null, string? Cursor = null);

/// <summary>Revisions of a record, newest first, with actor, kind and reason (content via GetRevision). Requires <c>read</c>.</summary>
public sealed class GetRecordHistory(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<GetRecordHistoryRequest, Page<RevisionSummaryDto>>
{
    public async Task<Result<Page<RevisionSummaryDto>>> HandleAsync(GetRecordHistoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        var limit = Paging.Limit(errors, request.Limit);
        var before = Paging.Decode<int>(errors, request.Cursor, position =>
            int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out var no) && no > 0 ? no : null);
        if (errors.Any)
        {
            return errors.ToError();
        }

        if (!await db.Records.AnyAsync(r => r.Id == request.RecordId, cancellationToken).ConfigureAwait(false))
        {
            return new NotFoundError("record");
        }

        var query = db.Revisions.AsNoTracking().Where(r => r.RecordId == request.RecordId);
        if (before is { } no)
        {
            query = query.Where(r => r.No < no);
        }

        var revisions = await query.OrderByDescending(r => r.No).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var next = revisions.Count > limit ? Paging.Encode(revisions[limit - 1].No.ToString(CultureInfo.InvariantCulture)) : null;
        return new Page<RevisionSummaryDto>([.. revisions.Take(limit).Select(RecordMapper.ToSummary)], next);
    }
}
