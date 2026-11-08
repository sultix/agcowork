using System.Text.Json;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Records;

namespace Agcowork.Application.Query;

/// <summary>
/// Translates parsed queries into database queries over <see cref="Record"/>s (implemented in Infrastructure: JSONB
/// operators, GIN index). Every method composes on the given source, so the tenant query filter stays in force; values
/// are always parameters, never SQL text.
/// </summary>
public interface IRecordQueryTranslator
{
    /// <summary>Records that match <paramref name="filter"/>.</summary>
    IQueryable<Record> Filter(IQueryable<Record> records, FilterNode filter);

    /// <summary>Records after <paramref name="position"/> in the order of <see cref="Order"/> (keyset pagination).</summary>
    IQueryable<Record> After(IQueryable<Record> records, IReadOnlyList<QuerySort> sorts, QueryCursor position);

    /// <summary>
    /// Sorted by <paramref name="sorts"/>, typed per field (numbers numerically, dates as dates, text by the database collation),
    /// empty values last in both directions, then by <c>(created_at, id)</c> so the order is total and stable.
    /// </summary>
    IQueryable<Record> Order(IQueryable<Record> records, IReadOnlyList<QuerySort> sorts);

    /// <summary>The rows to return, with their sort values as the database computed them (for the next cursor).</summary>
    IQueryable<RecordRow> Project(IQueryable<Record> records, IReadOnlyList<QuerySort> sorts, bool includeBody);

    /// <summary>
    /// Collections whose schema has a relation property targeting <paramref name="targetCollectionId"/> (the sources of
    /// backlinks, AP-037): JSONB containment on the stored schema, so other schemas are never loaded.
    /// </summary>
    IQueryable<Collection> RelationSources(IQueryable<Collection> collections, Guid targetCollectionId);
}

/// <summary>A record as read by a query: the columns of <see cref="Record"/> (body only on request) plus its sort values.</summary>
public sealed class RecordRow
{
    public Guid Id { get; init; }

    public Guid SpaceId { get; init; }

    public Guid? CollectionId { get; init; }

    public Guid? ParentRecordId { get; init; }

    public string Title { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, JsonElement> Properties { get; init; } = new Dictionary<string, JsonElement>();

    public string? Body { get; init; }

    public int RevisionNo { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid UpdatedBy { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>One value per sort criterion, typed as <see cref="QueryCursor.KeyType"/>; null when the record has none.</summary>
    public IReadOnlyList<object?> SortValues { get; init; } = [];
}
