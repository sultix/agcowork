using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Query;

/// <summary>Runs a parsed query over the records of one collection; shared by <see cref="ExecuteQuery"/> and <c>ExecuteView</c>.</summary>
internal static class RecordQueryRunner
{
    public static async Task<QueryResult<RecordDto>> RunAsync(
        IAgcoworkDbContext db,
        IRecordQueryTranslator translator,
        CollectionSchemaSnapshot schema,
        RecordQuery query,
        CancellationToken cancellationToken)
    {
        // Tenant filter of the context applies to every query below; nothing here bypasses it.
        var records = db.Records.AsNoTracking().Where(r => r.CollectionId == schema.CollectionId);
        if (!query.IncludeArchived)
        {
            records = records.Where(r => r.ArchivedAt == null);
        }

        if (query.Filter is { } filter)
        {
            records = translator.Filter(records, filter);
        }

        int? count = null;
        if (query.IncludeCount)
        {
            count = Math.Min(QueryParser.MaxCount, await records.Take(QueryParser.MaxCount + 1).CountAsync(cancellationToken).ConfigureAwait(false));
        }

        if (query.After is { } position)
        {
            records = translator.After(records, query.Sorts, position);
        }

        var includeBody = query.Fields?.Contains(QueryParser.BodyField, StringComparer.Ordinal) == true;
        var rows = await translator.Project(translator.Order(records, query.Sorts).Take(query.Limit + 1), query.Sorts, includeBody)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string? next = null;
        if (rows.Count > query.Limit)
        {
            var last = rows[query.Limit - 1];
            next = new QueryCursor(last.SortValues, last.CreatedAt, last.Id).Encode(query.Sorts);
        }

        return new QueryResult<RecordDto>([.. rows.Take(query.Limit).Select(r => RecordMapper.ToDto(r, schema.Properties, query.Fields))], next, count);
    }
}
