using System.Text.Json;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Records;

/// <param name="Ids">Record ids to resolve, 1..200; duplicates are allowed and answered once.</param>
public sealed record LookupRecordsRequest(IReadOnlyList<Guid>? Ids);

/// <summary>
/// Titles of many records in one call (architecture §2 "Relationen", AP-037): for relation values, chips and caches, so
/// clients never ask record by record. Requires <c>read</c>. One database query through the tenant filter: ids of other
/// workspaces and unknown ids are simply missing from the answer (no hint at their existence). Archived records are
/// included and marked.
/// </summary>
public sealed class LookupRecords(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<LookupRecordsRequest, RecordLookupDto>
{
    public const int MaxIds = 200;

    public async Task<Result<RecordLookupDto>> HandleAsync(LookupRecordsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        if (request.Ids is not { Count: > 0 } ids)
        {
            errors.Add("ids", ErrorCodes.Required, $"'ids' is required: 1 to {MaxIds} record ids.");
            return errors.ToError();
        }

        if (ids.Count > MaxIds)
        {
            errors.Add("ids", ErrorCodes.TooLong, $"'ids' allows at most {MaxIds} ids per request; split larger sets into several requests.");
            return errors.ToError();
        }

        var found = await RecordTitles.LookupAsync(db, ids, cancellationToken).ConfigureAwait(false);
        return new RecordLookupDto([.. ids.Distinct().Where(found.ContainsKey).Select(id => found[id])]);
    }
}

/// <summary>Answer of <c>POST /records/lookup</c>.</summary>
/// <param name="Items">The records found, in the order of the request (each id once); unknown ids and ids of other workspaces are missing.</param>
public sealed record RecordLookupDto(IReadOnlyList<RecordLookupItemDto> Items);

/// <summary>Title and place of one record.</summary>
/// <param name="Id">Record id.</param>
/// <param name="Title">Current title.</param>
/// <param name="CollectionId">Collection of the record; null for a free page.</param>
/// <param name="SpaceId">Space of the record.</param>
/// <param name="Archived">True when the record, its collection or its space is archived (as in search). References to it stay valid.</param>
public sealed record RecordLookupItemDto(Guid Id, string Title, Guid? CollectionId, Guid SpaceId, bool Archived);

/// <summary>A relation value resolved to its target record.</summary>
/// <param name="Id">Id of the referenced record, as stored.</param>
/// <param name="Title">Current title of the referenced record; null when it is not available (unknown id).</param>
/// <param name="Archived">True when the referenced record, its collection or its space is archived.</param>
public sealed record RelationTargetDto(Guid Id, string? Title, bool Archived);

/// <summary>Bulk title resolution shared by <see cref="LookupRecords"/> and <c>expandRelations</c> of the record reads.</summary>
internal static class RecordTitles
{
    /// <summary>
    /// The records among <paramref name="ids"/> with title, container and archived state, in one query (archived state of
    /// collection and space as <c>EXISTS</c> subqueries). Runs through the tenant filter, so ids of other workspaces are absent.
    /// </summary>
    public static async Task<Dictionary<Guid, RecordLookupItemDto>> LookupAsync(IAgcoworkDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        var rows = await db.Records.AsNoTracking()
            .Where(r => distinct.Contains(r.Id))
            .Select(r => new RecordLookupItemDto(
                r.Id,
                r.Title,
                r.CollectionId,
                r.SpaceId,
                r.ArchivedAt != null
                    || db.Spaces.Any(s => s.Id == r.SpaceId && s.ArchivedAt != null)
                    || db.Collections.Any(c => c.Id == r.CollectionId && c.ArchivedAt != null)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.ToDictionary(r => r.Id);
    }

    /// <summary>
    /// The relation values of a record resolved to their targets (<c>expandRelations</c>): one entry per relation property of
    /// <paramref name="schema"/> that has a value, the referenced records in stored order with title and archived state. All
    /// properties together cost one query. A value that is no record of this workspace keeps its id with title null.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<RelationTargetDto>>> ExpandAsync(
        IAgcoworkDbContext db,
        IReadOnlyList<PropertyDefinition>? schema,
        IReadOnlyDictionary<string, JsonElement> properties,
        CancellationToken cancellationToken)
    {
        var references = new List<(string Key, IReadOnlyList<Guid> Ids)>();
        foreach (var property in (schema ?? []).Where(p => p.Type == PropertyType.Relation))
        {
            if (properties.TryGetValue(property.Key, out var value) && Ids(value) is { Count: > 0 } ids)
            {
                references.Add((property.Key, ids));
            }
        }

        var found = await LookupAsync(db, references.SelectMany(r => r.Ids), cancellationToken).ConfigureAwait(false);
        var expanded = new Dictionary<string, IReadOnlyList<RelationTargetDto>>(StringComparer.Ordinal);
        foreach (var (key, ids) in references)
        {
            expanded[key] = [.. ids.Select(id => found.TryGetValue(id, out var item)
                ? new RelationTargetDto(id, item.Title, item.Archived)
                : new RelationTargetDto(id, null, false))];
        }

        return expanded;
    }

    /// <summary>Record ids of a stored relation value: one id (single, or multiple from before the switch) or an array of ids.</summary>
    private static List<Guid> Ids(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when Guid.TryParse(value.GetString(), out var id) => [id],
        JsonValueKind.Array => [.. value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => Guid.TryParse(e.GetString(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)],
        _ => [],
    };
}
