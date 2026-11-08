using System.ComponentModel;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Search;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Mcp.Tools;

/// <summary>Full-text search over the workspace (<c>/w/{workspaceSlug}/mcp</c>).</summary>
[McpServerToolType]
internal static class SearchTools
{
    [McpServerTool(Name = "search", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Full-text search over all records of the workspace: finds records by words in title, properties and body across all spaces "
        + "and collections. Use it when you do not know where something is or which collection holds it ('where did we decide about "
        + "Kafka?'); use records_query when you know the collection and want exact conditions on property values (status, dates, "
        + "relations) or a sort order. Read a hit with record_get(recordId). Input: 'query'. {{searchSyntax}}"
        + "Results best first: 'items' (recordId, title, collectionId or null for a free page, spaceId, snippet, rank, updatedAt, "
        + "archived) and 'nextCursor' (pass as 'cursor' with the same query for more; null when done). Filters spaceId, collectionId "
        + "(unknown: not_found), includeArchived. Errors are structured on 'query' (required, empty_query when nothing is left to "
        + "search for, too_long, invalid_value), 'limit' or 'cursor'. Requires read.")]
    public static async Task<CallToolResult> Search(
        SearchRecords useCase,
        McpOutput output,
        [Description("Search input, e.g. entsch, \"event sourcing\" -kafka, postgres or mongodb. 1..200 characters.")] string query,
        [Description("Only records of this space.")] Guid? spaceId = null,
        [Description("Only records of this collection.")] Guid? collectionId = null,
        [Description("Also archived records, including records of archived collections and spaces. Default false.")] bool? includeArchived = null,
        [Description("Page size 1..50, default 20.")] int? limit = null,
        [Description("nextCursor of the previous page, unchanged, with the same query.")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(
            new SearchRecordsRequest(query, spaceId, collectionId, includeArchived ?? false, limit, cursor),
            cancellationToken);
        return result.IsSuccess ? output.Success(result.Value, McpText.SearchHits(result.Value)) : output.Failure(Renamed(result.Error));
    }

    /// <summary>The use case names the input 'q' (REST query parameter); the tool parameter is 'query'.</summary>
    private static Error Renamed(Error error) =>
        error is ValidationError validation
            ? new ValidationError([.. validation.Errors.Select(e => e.Field == "q" ? e with { Field = "query", Message = e.Message.Replace("'q'", "'query'", StringComparison.Ordinal) } : e)])
            : error;
}
