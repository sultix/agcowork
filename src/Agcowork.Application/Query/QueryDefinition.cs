using System.Text.Json;
using System.Text.Json.Serialization;
using Agcowork.Application.Views;
using Agcowork.Domain.Views;

namespace Agcowork.Application.Query;

/// <summary>
/// A query over the records of a collection as sent by a caller (REST body, MCP arguments). The parser
/// (<see cref="QueryParser"/>) checks it against the collection schema and turns it into a <see cref="RecordQuery"/>.
/// </summary>
/// <param name="Filter">
/// Filter DSL: <c>{ "and": [ … ] }</c>, <c>{ "or": [ … ] }</c>, <c>{ "not": { … } }</c> or a condition <c>{ "prop", "op", "value" }</c>,
/// nested freely (max. 8 levels, 100 conditions). Omit for all records.
/// </param>
/// <param name="Sorts">[ { "prop", "dir": "asc"|"desc" } ] in order of priority (max. 10); ties are broken by creation time, then id. Empty values sort last.</param>
/// <param name="Limit">Page size, 1..500, default 50.</param>
/// <param name="Cursor"><c>nextCursor</c> of the previous page, unchanged; it only fits the same sorts.</param>
/// <param name="Fields">Property keys to return (default: all). <c>body</c> adds the Markdown body, which is otherwise omitted.</param>
/// <param name="IncludeArchived">Also return archived records.</param>
/// <param name="IncludeCount">Also count all matches (up to 10 000).</param>
public sealed record QueryDefinition(
    ViewFilterNode? Filter = null,
    IReadOnlyList<ViewSortInput>? Sorts = null,
    int? Limit = null,
    string? Cursor = null,
    IReadOnlyList<string>? Fields = null,
    bool IncludeArchived = false,
    bool IncludeCount = false)
{
    /// <summary>Members of the wire form, the only ones a query may have.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["filter", "sorts", "limit", "cursor", "fields", "includeArchived", "includeCount"];

    /// <summary>Members that are not part of the query; kept only so the parser can reject them (e.g. <c>sort</c> instead of <c>sorts</c>).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>A parsed, schema-checked query.</summary>
/// <param name="Filter">Filter tree; null = all records.</param>
/// <param name="Sorts">Sort criteria; the tie-breaker (created_at, id) is implicit.</param>
/// <param name="Limit">Page size.</param>
/// <param name="After">Position after which the page starts; null for the first page.</param>
/// <param name="Fields">Property keys to return; null = all. May contain <c>body</c>.</param>
/// <param name="IncludeArchived">Also archived records.</param>
/// <param name="IncludeCount">Count the matches.</param>
public sealed record RecordQuery(
    FilterNode? Filter,
    IReadOnlyList<QuerySort> Sorts,
    int Limit,
    QueryCursor? After,
    IReadOnlyList<string>? Fields,
    bool IncludeArchived,
    bool IncludeCount);

/// <summary>One page of query results.</summary>
/// <param name="Items">Matching records in sort order; properties reduced to the requested fields.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> to get the next page; null when there are no further matches.</param>
/// <param name="Count">Number of all matches when <c>includeCount</c> was set, at most 10 000 (10 000 means "10 000 or more").</param>
public sealed record QueryResult<T>(IReadOnlyList<T> Items, string? NextCursor, int? Count = null);
