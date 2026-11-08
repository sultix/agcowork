using System.Globalization;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Query;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Views;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Records;

/// <param name="RecordId">The record whose backlinks to list.</param>
/// <param name="Limit">Records per group, 1..200, default 50.</param>
/// <param name="Cursor">
/// Either the <c>nextCursor</c> of one group (continues only that group; the answer then has that one group) or the
/// <c>nextGroupsCursor</c> of the answer (the next groups, first page each). Without a cursor: the first groups, first page each.
/// A cursor belongs to its record and its <paramref name="IncludeArchived"/> setting.
/// </param>
/// <param name="IncludeArchived">
/// Also archived source records, including records of archived collections and spaces (as in search). Default false.
/// Send the same value with a cursor as on the first page.
/// </param>
public sealed record GetBacklinksRequest(Guid RecordId, int? Limit = null, string? Cursor = null, bool IncludeArchived = false);

/// <summary>
/// "Who references this record?" (architecture §2 "Relationen", AP-037). Relations are stored one-sided, at the source
/// record; backlinks are derived: records of every collection whose schema has a relation property targeting the record's
/// collection (self-relations included) and whose value holds the record's id. Grouped by source collection and property,
/// newest first within a group, each group paginated on its own, at most <see cref="Backlinks.MaxGroups"/> groups per call.
/// Requires <c>read</c>. Unknown record or one of another workspace: 404. Free pages (no collection) cannot be relation
/// targets: no groups.
/// </summary>
public sealed class GetBacklinks(IAgcoworkDbContext db, ITenantContext tenant, IRecordQueryTranslator translator) : IUseCase<GetBacklinksRequest, RecordBacklinksDto>
{
    public async Task<Result<RecordBacklinksDto>> HandleAsync(GetBacklinksRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        var limit = Paging.Limit(errors, request.Limit);
        var cursor = Paging.Decode<BacklinkCursor>(errors, request.Cursor, BacklinkCursor.Parse);
        if (cursor is { } given && (given.RecordId != request.RecordId || given.IncludeArchived != request.IncludeArchived))
        {
            errors.Add(
                "cursor",
                ErrorCodes.InvalidFormat,
                "The cursor does not belong to this record or includeArchived setting. Pass a cursor of the previous answer for the same "
                + "record with the same includeArchived value, or omit it for the first page.");
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        var record = await db.Records.AsNoTracking()
            .Where(r => r.Id == request.RecordId)
            .Select(r => new { r.Id, r.CollectionId })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return new NotFoundError("record");
        }

        var backlinks = await Backlinks.ReadAsync(db, translator, record.Id, record.CollectionId, limit, cursor, request.IncludeArchived, cancellationToken).ConfigureAwait(false);
        if (backlinks is null)
        {
            errors.Add(
                "cursor",
                ErrorCodes.InvalidFormat,
                "The group of this cursor no longer exists (its relation property or collection was removed or archived). Start again without a cursor.");
            return errors.ToError();
        }

        return backlinks;
    }
}

/// <summary>The backlinks of a record, grouped by source collection and relation property.</summary>
/// <param name="RecordId">The referenced record.</param>
/// <param name="Groups">
/// One group per source collection and relation property that has at least one (listed) referencing record; ordered by
/// collection name, then by the property's position in the schema. Empty when nothing references the record.
/// </param>
/// <param name="NextGroupsCursor">
/// Set when more groups exist than one answer holds (at most 50 are looked at per call): pass it as <c>cursor</c> to get the
/// next groups. Null when all groups were looked at.
/// </param>
public sealed record RecordBacklinksDto(Guid RecordId, IReadOnlyList<BacklinkGroupDto> Groups, string? NextGroupsCursor);

/// <summary>Records of one collection that reference the record through one relation property.</summary>
/// <param name="CollectionId">Source collection.</param>
/// <param name="CollectionName">Name of the source collection.</param>
/// <param name="SpaceId">Space of the source collection.</param>
/// <param name="PropertyKey">Key of the relation property that holds the reference.</param>
/// <param name="PropertyName">Display name of that property.</param>
/// <param name="Items">The referencing records of this page, newest first.</param>
/// <param name="NextCursor">Cursor of the next page of this group (GET …/backlinks?cursor=…); null when the group is complete.</param>
/// <param name="More">
/// Null when the group is complete. Otherwise the query that lists all records of this group: call records_query (or POST
/// /collections/{collectionId}/query) with exactly this <c>collectionId</c> and <c>filter</c>; it can also sort and filter further.
/// </param>
public sealed record BacklinkGroupDto(
    Guid CollectionId,
    string CollectionName,
    Guid SpaceId,
    string PropertyKey,
    string PropertyName,
    IReadOnlyList<BacklinkItemDto> Items,
    string? NextCursor,
    BacklinkQueryDto? More);

/// <summary>A records_query call that lists all backlinks of one group.</summary>
/// <param name="CollectionId">The source collection to query.</param>
/// <param name="Filter">Filter DSL node: the relation property contains the referenced record.</param>
public sealed record BacklinkQueryDto(Guid CollectionId, BacklinkFilterDto Filter);

/// <summary>The filter of a <see cref="BacklinkQueryDto"/>: <c>{ "prop": key, "op": "contains", "value": recordId }</c>.</summary>
/// <param name="Prop">Key of the relation property.</param>
/// <param name="Op">Always <c>contains</c>.</param>
/// <param name="Value">The referenced record.</param>
public sealed record BacklinkFilterDto(string Prop, string Op, Guid Value);

/// <summary>One referencing record.</summary>
/// <param name="Id">Record id.</param>
/// <param name="Title">Current title.</param>
/// <param name="Archived">True when the record, its collection or its space is archived (listed only with includeArchived).</param>
public sealed record BacklinkItemDto(Guid Id, string Title, bool Archived);

/// <summary>
/// Position in the backlinks of one record with one includeArchived setting: either the next groups (from
/// <see cref="GroupOffset"/>, <see cref="CollectionId"/> null) or the next page of one group (after the record's created_at, id).
/// </summary>
internal readonly record struct BacklinkCursor(
    Guid RecordId,
    bool IncludeArchived,
    int GroupOffset,
    Guid? CollectionId,
    string? PropertyKey,
    DateTimeOffset CreatedAt,
    Guid Id)
{
    private const string GroupsKind = "g";
    private const string ItemsKind = "i";

    public static string Groups(Guid recordId, bool includeArchived, int offset) =>
        Paging.Encode(string.Create(CultureInfo.InvariantCulture, $"{GroupsKind}:{recordId:N}:{Flag(includeArchived)}:{offset}"));

    public static string Items(Guid recordId, bool includeArchived, Guid collectionId, string propertyKey, DateTimeOffset createdAt, Guid id) =>
        Paging.Encode(string.Create(
            CultureInfo.InvariantCulture,
            $"{ItemsKind}:{recordId:N}:{Flag(includeArchived)}:{collectionId:N}:{propertyKey}:{createdAt.UtcTicks}:{id:N}"));

    public static BacklinkCursor? Parse(string position)
    {
        var parts = position.Split(':');
        if (parts.Length < 4 || !Guid.TryParseExact(parts[1], "N", out var recordId) || parts[2] is not ("0" or "1"))
        {
            return null;
        }

        var includeArchived = parts[2] == "1";
        if (parts[0] == GroupsKind && parts.Length == 4)
        {
            return int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset > 0
                ? new BacklinkCursor(recordId, includeArchived, offset, null, null, default, Guid.Empty)
                : null;
        }

        return parts[0] == ItemsKind
            && parts.Length == 7
            && Guid.TryParseExact(parts[3], "N", out var collectionId)
            && PropertyDefinition.IsValidKey(parts[4])
            && long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks
            && Guid.TryParseExact(parts[6], "N", out var id)
                ? new BacklinkCursor(recordId, includeArchived, 0, collectionId, parts[4], new DateTimeOffset(ticks, TimeSpan.Zero), id)
                : null;
    }

    private static string Flag(bool value) => value ? "1" : "0";
}

/// <summary>Reading backlinks; shared by <see cref="GetBacklinks"/> and <c>includeBacklinks</c> of the record reads.</summary>
internal static class Backlinks
{
    /// <summary>Records per group that <c>includeBacklinks</c> of the record reads attaches.</summary>
    public const int PerGroupInRecord = 10;

    /// <summary>
    /// Groups looked at per call: bounds the statement (one <c>UNION ALL</c> branch per group) whatever the schemas hold;
    /// further groups come with <see cref="RecordBacklinksDto.NextGroupsCursor"/>.
    /// </summary>
    public const int MaxGroups = 50;

    /// <summary>
    /// The backlink groups of a record in two queries: the collections of the workspace whose schema has a relation to
    /// <paramref name="collectionId"/> (JSONB containment on the schema, so other schemas are not even read), then one statement for
    /// up to <see cref="MaxGroups"/> groups: per group <c>collection_id = … AND properties @&gt; {"key": "id"}</c> (multiple
    /// relations also <c>{"key": ["id"]}</c>), each ordered and limited on its own, combined with <c>UNION ALL</c>. The containment
    /// conditions are served by the GIN index <c>(workspace_id, properties jsonb_path_ops)</c>; everything runs through the
    /// tenant filter (and row-level security). Returns null when <paramref name="cursor"/> names a group that no longer exists.
    /// </summary>
    public static async Task<RecordBacklinksDto?> ReadAsync(
        IAgcoworkDbContext db,
        IRecordQueryTranslator translator,
        Guid recordId,
        Guid? collectionId,
        int limit,
        BacklinkCursor? cursor,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        if (collectionId is not { } targetCollectionId)
        {
            return cursor is { CollectionId: not null } ? null : new RecordBacklinksDto(recordId, [], null);
        }

        var groups = await GroupsAsync(db, translator, targetCollectionId, includeArchived, cancellationToken).ConfigureAwait(false);
        List<BacklinkGroup> selected;
        string? nextGroups = null;
        if (cursor is { CollectionId: { } cursorCollection } position)
        {
            if (groups.Find(g => g.CollectionId == cursorCollection && g.Property.Key == position.PropertyKey) is not { } group)
            {
                return null;
            }

            selected = [group];
        }
        else
        {
            var offset = cursor?.GroupOffset ?? 0;
            selected = [.. groups.Skip(offset).Take(MaxGroups)];
            if (offset + MaxGroups < groups.Count)
            {
                nextGroups = BacklinkCursor.Groups(recordId, includeArchived, offset + MaxGroups);
            }
        }

        if (selected.Count == 0)
        {
            return new RecordBacklinksDto(recordId, [], nextGroups);
        }

        var itemsAfter = cursor is { CollectionId: not null } c ? (c.CreatedAt, c.Id) : ((DateTimeOffset, Guid)?)null;
        IQueryable<BacklinkRow>? union = null;
        for (var i = 0; i < selected.Count; i++)
        {
            var page = GroupPage(db, translator, selected[i], i, recordId, limit, itemsAfter, includeArchived);
            union = union is null ? page : union.Concat(page);
        }

        var rows = (await union!.ToListAsync(cancellationToken).ConfigureAwait(false)).ToLookup(r => r.Group);
        var result = new List<BacklinkGroupDto>();
        for (var i = 0; i < selected.Count; i++)
        {
            var group = selected[i];
            var items = rows[i].OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            var more = items.Count > limit;
            result.Add(new BacklinkGroupDto(
                group.CollectionId,
                group.CollectionName,
                group.SpaceId,
                group.Property.Key,
                group.Property.Name,
                [.. items.Take(limit).Select(r => new BacklinkItemDto(r.Id, r.Title, r.ArchivedAt is not null || group.Archived))],
                more
                    ? BacklinkCursor.Items(recordId, includeArchived, group.CollectionId, group.Property.Key, items[limit - 1].CreatedAt, items[limit - 1].Id)
                    : null,
                more
                    ? new BacklinkQueryDto(group.CollectionId, new BacklinkFilterDto(group.Property.Key, ViewFilterOperators.Contains, recordId))
                    : null));
        }

        return new RecordBacklinksDto(recordId, result, nextGroups);
    }

    /// <summary>One page of one group, newest first (keyset on created_at, id), with the group's index as discriminator.</summary>
    private static IQueryable<BacklinkRow> GroupPage(
        IAgcoworkDbContext db,
        IRecordQueryTranslator translator,
        BacklinkGroup group,
        int index,
        Guid recordId,
        int limit,
        (DateTimeOffset CreatedAt, Guid Id)? after,
        bool includeArchived)
    {
        var sourceCollectionId = group.CollectionId;
        var holds = new FilterCondition(
            new QueryField(group.Property.Key, QueryFieldSource.Property, FilterValueType.Relation, group.Property),
            FilterOperator.Contains,
            [recordId]);
        var query = translator.Filter(db.Records.AsNoTracking().Where(r => r.CollectionId == sourceCollectionId), holds);
        if (!includeArchived)
        {
            query = query.Where(r => r.ArchivedAt == null);
        }

        if (after is var (createdAt, id))
        {
            query = query.Where(r => r.CreatedAt < createdAt || (r.CreatedAt == createdAt && r.Id < id));
        }

        return query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(limit + 1)
            .Select(r => new BacklinkRow { Group = index, Id = r.Id, Title = r.Title, CreatedAt = r.CreatedAt, ArchivedAt = r.ArchivedAt });
    }

    /// <summary>
    /// Relation properties of the workspace's collections that target <paramref name="targetCollectionId"/>, ordered by
    /// collection name, then schema order. Only collections whose schema has such a relation are read (prefiltered in SQL).
    /// Without <paramref name="includeArchived"/>, archived collections and collections in archived spaces are left out (all
    /// their records count as archived).
    /// </summary>
    private static async Task<List<BacklinkGroup>> GroupsAsync(
        IAgcoworkDbContext db,
        IRecordQueryTranslator translator,
        Guid targetCollectionId,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        var collections = await (
                from collection in translator.RelationSources(db.Collections.AsNoTracking(), targetCollectionId)
                join space in db.Spaces.AsNoTracking() on collection.SpaceId equals space.Id
                select new { collection.Id, collection.Name, collection.SpaceId, collection.Schema, Archived = collection.ArchivedAt != null || space.ArchivedAt != null })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. collections
            .Where(c => includeArchived || !c.Archived)
            .OrderBy(c => c.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(c => c.Id)
            .SelectMany(c => c.Schema
                .Where(p => p.Type == PropertyType.Relation && p.TargetCollectionId == targetCollectionId)
                .Select(p => new BacklinkGroup(c.Id, c.Name, c.SpaceId, p, c.Archived)))];
    }

    private sealed record BacklinkGroup(Guid CollectionId, string CollectionName, Guid SpaceId, PropertyDefinition Property, bool Archived);

    /// <summary>Row of the backlink statement; <see cref="Group"/> tells which group (UNION ALL operand) it came from.</summary>
    private sealed class BacklinkRow
    {
        public int Group { get; init; }

        public Guid Id { get; init; }

        public string Title { get; init; } = string.Empty;

        public DateTimeOffset CreatedAt { get; init; }

        public DateTimeOffset? ArchivedAt { get; init; }
    }
}
