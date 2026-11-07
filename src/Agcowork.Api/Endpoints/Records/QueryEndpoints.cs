using System.Text.Json;
using System.Text.Json.Serialization;
using Agcowork.Api.Http;
using Agcowork.Application.Query;
using Agcowork.Application.Records;
using Agcowork.Application.Views;
using Agcowork.Domain.Views;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Agcowork.Api.Endpoints.Records;

/// <summary>
/// Querying records with the filter DSL: <c>POST /collections/{id}/query</c> and <c>POST /views/{id}/query</c>. The
/// descriptions carry the operator matrix, generated from <see cref="OperatorMatrix"/>, so agents read the same rules the parser applies.
/// </summary>
internal static class QueryEndpoints
{
    /// <summary>The operator matrix as text, one line per value type.</summary>
    public static string MatrixDescription { get; } = string.Join(
        "; ",
        new (string Types, FilterValueType Type)[]
        {
            ("text, markdown, url, email, title", FilterValueType.Text),
            ("number", FilterValueType.Number),
            ("checkbox", FilterValueType.Checkbox),
            ("select, status", FilterValueType.Option),
            ("multi_select", FilterValueType.MultiOption),
            ("date", FilterValueType.Date),
            ("datetime, created_at, updated_at", FilterValueType.DateTime),
            ("relation", FilterValueType.Relation),
            ("actor, created_by, updated_by", FilterValueType.Actor),
        }.Select(row => $"{row.Types}: {string.Join(", ", OperatorMatrix.WireNamesFor(row.Type))}"))
        + "; json: not filterable";

    private static readonly string DslDescription =
        "Filter DSL: a node is { \"and\": [ … ] }, { \"or\": [ … ] }, { \"not\": { … } } or a condition { \"prop\", \"op\", \"value\" }; nest freely "
        + $"(max. {QueryParser.MaxFilterDepth} levels, {QueryParser.MaxConditions} conditions). 'prop' is a property key of the collection or a pseudo-field "
        + "(title, created_at, updated_at, created_by, updated_by; a schema property with the same key wins). Operators per type: "
        + MatrixDescription + ". "
        + "'in'/'nin' take a list (max. 100), 'is_empty'/'is_not_empty' no value (empty = missing, null, \"\" or []). 'contains'/'starts_with' on text are "
        + "case-insensitive; 'contains' on multi_select/relation tests one value; 'in' on them means any of the values; 'neq'/'nin' also match records "
        + "without a value. Values: numbers as JSON numbers, dates 'YYYY-MM-DD', date-times ISO 8601 with offset (or a date = start of day UTC), "
        + "relation values record ids, actor values ids or 'actor:<id>', option values must exist in the schema. Example: "
        + "{ \"and\": [ { \"prop\": \"status\", \"op\": \"in\", \"value\": [\"todo\", \"in_progress\"] }, { \"or\": [ { \"prop\": \"due\", \"op\": \"lt\", "
        + "\"value\": \"2026-12-01\" }, { \"prop\": \"priority\", \"op\": \"eq\", \"value\": \"high\" } ] } ] }. ";

    private static readonly string PagingDescription =
        "Results: 'items' (records without body unless requested), 'nextCursor' (pass as 'cursor' for the next page; null when there are no further "
        + $"matches) and, with 'includeCount': true, 'count' (all matches, at most {QueryParser.MaxCount}). 'limit' 1..{QueryParser.MaxLimit}, "
        + $"default {QueryParser.DefaultLimit}. Archived records only with 'includeArchived': true. Invalid input: 400 with 'errors[]' on the path "
        + "(e.g. 'filter.and[1].op'), codes unknown_property, unknown_operator, operator_not_applicable, invalid_value, not_in_options, not_sortable, "
        + "invalid_cursor, each with 'allowed' where there is a closed set; unknown body members (e.g. 'sort' instead of 'sorts') are "
        + "'unknown_property' with the accepted members as 'allowed'. Requires read.";

    public static RouteGroupBuilder MapQueryEndpoints(this RouteGroupBuilder workspace)
    {
        workspace.MapPost("/collections/{collectionId:guid}/query", QueryCollectionAsync)
            .WithTags("Records")
            .WithName("QueryRecords")
            .WithSummary("Query the records of a collection")
            .WithDescription(
                "Filters, sorts and pages the records of a collection. Body (all optional): 'filter', 'sorts' [ { \"prop\", \"dir\": \"asc\"|\"desc\" } ] "
                + $"(max. {QueryParser.MaxSorts}; numbers numerically, dates chronologically, text alphabetically; empty values last; ties by creation time; "
                + "multi_select, relation and json cannot be sorted), 'fields' (property keys to return; add \"body\" for the Markdown body), 'limit', "
                + "'cursor', 'includeArchived', 'includeCount'. " + DslDescription + PagingDescription)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        workspace.MapPost("/views/{viewId:guid}/query", QueryViewAsync)
            .WithTags("Views")
            .WithName("QueryView")
            .WithSummary("Run a view")
            .WithDescription(
                "Returns the records of the view's collection with the view's filter and sorts; properties are reduced to its visibleProperties "
                + "(empty = all). Body (optional): 'filter' (combined with the view's filter by and; both together at most "
                + $"{QueryParser.MaxCombinedConditions} conditions), 'limit', 'cursor', 'includeArchived', 'includeCount'; sorts and fields come from the view. "
                + "A stored view definition that does not fit the schema any more is reported as 400 on 'view.filter…'/'view.sorts…'; fix it with "
                + "PATCH /views/{id}. " + DslDescription + PagingDescription)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return workspace;
    }

    private static async Task<Results<Ok<QueryResult<RecordDto>>, ProblemHttpResult>> QueryCollectionAsync(
        Guid collectionId,
        [FromBody] QueryDefinition? body,
        ExecuteQuery useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ExecuteQueryRequest(collectionId, body ?? new QueryDefinition()), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<QueryResult<RecordDto>>, ProblemHttpResult>> QueryViewAsync(
        Guid viewId,
        [FromBody] QueryViewBody? body,
        ExecuteView useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(
            new ExecuteViewRequest(
                viewId, body?.Filter, body?.Cursor, body?.Limit, body?.IncludeArchived ?? false, body?.IncludeCount ?? false, body?.Unknown?.Keys.ToList()),
            cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}

/// <summary>Body of <c>POST /views/{viewId}/query</c>; everything optional.</summary>
/// <param name="Filter">Additional filter, combined with the view's filter by and.</param>
/// <param name="Cursor">nextCursor of the previous page of this view.</param>
/// <param name="Limit">Page size, 1..500, default 50.</param>
/// <param name="IncludeArchived">Also return archived records.</param>
/// <param name="IncludeCount">Also count all matches (up to 10 000).</param>
internal sealed record QueryViewBody(
    ViewFilterNode? Filter = null,
    string? Cursor = null,
    int? Limit = null,
    bool? IncludeArchived = null,
    bool? IncludeCount = null)
{
    /// <summary>Members that are not part of the body; rejected with <c>unknown_property</c> (e.g. <c>sorts</c>: a view has its own).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; init; }
}
