using System.ComponentModel;
using Agcowork.Application.Collections;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Application.Workspaces;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Mcp;

/// <summary>
/// Resources of a workspace endpoint (architecture §4.2): records as Markdown, collection schemas as JSON Schema, space
/// overviews as Markdown. Reads go through the same use cases as the tools, so tenancy and scopes apply identically. A
/// failure is a JSON-RPC error; an id of another workspace reads exactly like an unknown one.
/// </summary>
[McpServerResourceType]
internal static class WorkspaceResources
{
    public const string Scheme = "agcowork://";
    public const string RecordTemplate = Scheme + "record/{id}";
    public const string SchemaTemplate = Scheme + "collection/{id}/schema";
    public const string SpaceOverviewTemplate = Scheme + "space/{id}/overview";
    public const string MarkdownMimeType = "text/markdown";
    public const string JsonSchemaMimeType = "application/schema+json";

    public static string RecordUri(Guid id) => $"{Scheme}record/{id}";

    public static string SchemaUri(Guid id) => $"{Scheme}collection/{id}/schema";

    public static string SpaceOverviewUri(Guid id) => $"{Scheme}space/{id}/overview";

    [McpServerResource(UriTemplate = RecordTemplate, Name = "record", MimeType = MarkdownMimeType)]
    [Description("A record as Markdown: YAML frontmatter with id, collection, title, all properties and revision, then the body.")]
    public static async Task<ResourceContents> Record(GetRecordMarkdown useCase, string id, CancellationToken cancellationToken)
    {
        var recordId = ParseId(id);
        var result = await useCase.HandleAsync(new GetRecordMarkdownRequest(recordId), cancellationToken);
        return new TextResourceContents { Uri = RecordUri(recordId), MimeType = MarkdownMimeType, Text = Unwrap(result) };
    }

    [McpServerResource(UriTemplate = SchemaTemplate, Name = "collection_schema", MimeType = JsonSchemaMimeType)]
    [Description("JSON Schema (draft 2020-12) of the 'properties' object of a collection's records: types, enum values, required keys, descriptions.")]
    public static async Task<ResourceContents> Schema(GetCollectionSchema useCase, McpOutput output, string id, CancellationToken cancellationToken)
    {
        var collectionId = ParseId(id);
        var result = await useCase.HandleAsync(new GetCollectionSchemaRequest(collectionId), cancellationToken);
        return new TextResourceContents
        {
            Uri = SchemaUri(collectionId),
            MimeType = JsonSchemaMimeType,
            Text = Unwrap(result).JsonSchema.ToJsonString(output.Json),
        };
    }

    [McpServerResource(UriTemplate = SpaceOverviewTemplate, Name = "space_overview", MimeType = MarkdownMimeType)]
    [Description("A space as Markdown: description and its collections with descriptions and record counts.")]
    public static async Task<ResourceContents> SpaceOverview(GetWorkspaceOverview useCase, string id, CancellationToken cancellationToken)
    {
        var spaceId = ParseId(id);
        var result = await useCase.HandleAsync(new GetWorkspaceOverviewRequest(spaceId), cancellationToken);
        return new TextResourceContents
        {
            Uri = SpaceOverviewUri(spaceId),
            MimeType = MarkdownMimeType,
            Text = McpText.Overview(Unwrap(result), includeWorkspace: false).TrimStart(),
        };
    }

    /// <summary>
    /// <c>resources/list</c>: the schema of every collection and the overview of every space (records are too many to list;
    /// they are reachable through the <see cref="RecordTemplate"/> template and the tools).
    /// </summary>
    public static async ValueTask<ListResourcesResult> ListAsync(RequestContext<ListResourcesRequestParams> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var useCase = request.Services!.GetRequiredService<GetWorkspaceOverview>();
        var overview = Unwrap(await useCase.HandleAsync(new GetWorkspaceOverviewRequest(), cancellationToken));
        var result = new ListResourcesResult();
        foreach (var space in overview.Spaces)
        {
            result.Resources.Add(new Resource
            {
                Uri = SpaceOverviewUri(space.Id),
                Name = $"space_{space.Id:N}_overview",
                Title = $"Space {space.Name}",
                Description = space.Description,
                MimeType = MarkdownMimeType,
            });
            foreach (var collection in space.Collections)
            {
                result.Resources.Add(new Resource
                {
                    Uri = SchemaUri(collection.Id),
                    Name = $"collection_{collection.Id:N}_schema",
                    Title = $"Schema of {collection.Name}",
                    Description = collection.Description,
                    MimeType = JsonSchemaMimeType,
                });
            }
        }

        return result;
    }

    /// <summary>A malformed id cannot exist: answered like an unknown one.</summary>
    private static Guid ParseId(string id) =>
        Guid.TryParse(id, out var value) ? value : throw NotFound();

    private static T Unwrap<T>(Result<T> result) => result.IsSuccess
        ? result.Value
        : throw result.Error switch
        {
            NotFoundError => NotFound(),
            var error => new McpProtocolException(
                $"{error.Code}: {Agcowork.Api.Http.ProblemDetailsMapping.ToProblemDetails(error).Detail ?? error.Code}", McpErrorCode.InvalidRequest),
        };

    // No id, no hint whether the resource exists elsewhere: the same message for every miss.
    private static McpProtocolException NotFound() => new("Resource not found.", McpErrorCode.ResourceNotFound);
}
