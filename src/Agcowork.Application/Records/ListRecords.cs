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

/// <param name="CollectionId">Records of this collection. Either this or <paramref name="SpaceId"/>.</param>
/// <param name="SpaceId">Free pages (records without collection) of this space.</param>
/// <param name="IncludeArchived">Also list archived records.</param>
/// <param name="Limit">Page size, 1..200, default 50.</param>
/// <param name="Cursor"><c>nextCursor</c> of the previous page.</param>
public sealed record ListRecordsRequest(Guid? CollectionId = null, Guid? SpaceId = null, bool IncludeArchived = false, int? Limit = null, string? Cursor = null);

/// <summary>
/// Simple list of the records of a collection, or of the free pages of a space, oldest first (by creation time, then
/// id), without bodies. Requires <c>read</c>. Filtering and sorting come with the query endpoint (AP-009). Unknown
/// collection or space: 404.
/// </summary>
public sealed class ListRecords(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<ListRecordsRequest, Page<RecordDto>>
{
    public async Task<Result<Page<RecordDto>>> HandleAsync(ListRecordsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        if ((request.CollectionId is null) == (request.SpaceId is null))
        {
            errors.Add("collectionId", ErrorCodes.Required, "Send exactly one of 'collectionId' (records of a collection) or 'spaceId' (free pages of a space).");
        }

        var limit = Paging.Limit(errors, request.Limit);
        var after = Paging.Decode<(DateTimeOffset CreatedAt, Guid Id)>(errors, request.Cursor, ParsePosition);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var query = db.Records.AsNoTracking();
        IReadOnlyList<Domain.Collections.Schema.PropertyDefinition>? schema = null;
        if (request.CollectionId is { } collectionId)
        {
            schema = await db.Collections.AsNoTracking().Where(c => c.Id == collectionId).Select(c => c.Schema).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (schema is null)
            {
                return new NotFoundError("collection");
            }

            query = query.Where(r => r.CollectionId == collectionId);
        }
        else
        {
            var spaceId = request.SpaceId!.Value;
            if (!await db.Spaces.AnyAsync(s => s.Id == spaceId, cancellationToken).ConfigureAwait(false))
            {
                return new NotFoundError("space");
            }

            query = query.Where(r => r.SpaceId == spaceId && r.CollectionId == null);
        }

        if (!request.IncludeArchived)
        {
            query = query.Where(r => r.ArchivedAt == null);
        }

        if (after is { } position)
        {
            var (createdAt, id) = position;
            query = query.Where(r => r.CreatedAt > createdAt || (r.CreatedAt == createdAt && r.Id > id));
        }

        var records = await query
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var next = records.Count > limit ? Paging.Encode(Position(records[limit - 1].CreatedAt, records[limit - 1].Id)) : null;
        return new Page<RecordDto>([.. records.Take(limit).Select(r => RecordMapper.ToDto(r, schema, includeBody: false))], next);
    }

    private static string Position(DateTimeOffset createdAt, Guid id) =>
        string.Create(CultureInfo.InvariantCulture, $"{createdAt.UtcTicks}:{id:N}");

    private static (DateTimeOffset, Guid)? ParsePosition(string position)
    {
        var parts = position.Split(':');
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks
            && Guid.TryParseExact(parts[1], "N", out var id)
                ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
    }
}
