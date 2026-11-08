using System.ComponentModel;
using System.Globalization;
using Agcowork.Api.Http;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Search;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Agcowork.Api.Endpoints.Search;

/// <summary>Full-text search over the records of the workspace (<c>search</c>).</summary>
internal static class SearchEndpoints
{
    /// <summary>Syntax, markers and limits; shared with the MCP tool <c>search</c> (without the name of the input parameter).</summary>
    public static readonly string SyntaxDescription =
        "Syntax (web search style, case-insensitive, no stemming): words separated by spaces must all occur; \"exact phrase\" in double "
        + "quotes; -word excludes; 'or' between alternatives (e.g. postgres or mongodb). The last word, if it has at least 2 letters or "
        + "digits, also matches as a prefix (entsch finds Entscheidung) unless it is quoted or excluded; in a hyphenated word only the "
        + "last part is a prefix (foo-ba does not find foo-bar). At least one word must not be excluded. 1.."
        + Invariant(RecordSearch.MaxQueryLength) + " characters. Searched: title (ranks highest), text, markdown, url and email properties (the "
        + "first " + Invariant(RecordSearch.PropertiesPrefixLength) + " characters together), the labels and values of select, status and "
        + "multi_select options, and the first " + Invariant(RecordSearch.BodyPrefixLength) + " characters of the body (the rest of a long body "
        + "is not indexed). E-mail addresses and hosts are single words (kontakt finds kontakt@firma.example, firma does not). Not searched: "
        + "relations, actors, numbers, dates, json, comments. Records in an archived collection or space count as archived. Each hit has "
        + "'snippet': plain text with every match between " + RecordSearch.HighlightStart + " and " + RecordSearch.HighlightEnd
        + " (U+27E6, U+27E7). The snippet can contain <, > and markup from the content: render it as text, never as HTML. Only these two "
        + "markers come from the server (in content they appear as [ and ]). Without markers when only the title matched or the match lies "
        + "beyond the first " + Invariant(RecordSearch.SnippetSourceLength) + " characters. 'rank': higher is better, 1 or more when the title "
        + "matches the whole query. ";

    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder workspace)
    {
        workspace.MapGet("/search", SearchAsync)
            .WithTags("Search")
            .WithName("SearchRecords")
            .WithSummary("Full-text search over all records of the workspace")
            .WithDescription(
                "Finds records by words in title, properties and body across all spaces and collections, without knowing the collection "
                + "or the filter DSL (for exact property conditions use POST /collections/{id}/query). Input: 'q'. " + SyntaxDescription
                + "Ordered by rank, then id. Filters: spaceId, collectionId (unknown: 404), includeArchived (default false; also records of archived collections and spaces). 'limit' 1.."
                + Invariant(RecordSearch.MaxLimit) + ", default " + Invariant(RecordSearch.DefaultLimit)
                + "; 'nextCursor' with the same 'q' gives the next page, null when there are no further hits. "
                + "Invalid input: 400 with errors[] on 'q' (required, empty_query when nothing is left to search for, too_long, invalid_value "
                + "for control characters), 'limit' (out_of_range) or 'cursor' (invalid_cursor). Requires read.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return workspace;
    }

    private static async Task<Results<Ok<Page<SearchHitDto>>, ProblemHttpResult>> SearchAsync(
        SearchRecords useCase,
        [FromQuery, Description("Search input, see the endpoint description, e.g. \"event sourcing\" -kafka.")] string? q,
        [FromQuery, Description("Only records of this space.")] Guid? spaceId,
        [FromQuery, Description("Only records of this collection.")] Guid? collectionId,
        [FromQuery, Description("Also archived records, including records of archived collections and spaces. Default false.")] bool? includeArchived,
        [FromQuery, Description("Page size 1..50, default 20.")] int? limit,
        [FromQuery, Description("nextCursor of the previous page, with the same q.")] string? cursor,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(
            new SearchRecordsRequest(q, spaceId, collectionId, includeArchived ?? false, limit, cursor),
            cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
