using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>
/// NUL and unpaired surrogates in text over MCP (AP-028): a tool error <c>validation_failed</c> with <c>invalid_value</c> on the
/// argument (or the path inside it) that carries them, like REST; never <c>internal_error</c> or a bare JSON-RPC error. Sent as
/// raw JSON-RPC, because a client's JSON writer replaces unpaired surrogates before they leave it.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class UnstorableTextToolsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>(tool, arguments, field). Tokens: <c>{X}</c> the bad text (a JSON escape), <c>{S}</c>, <c>{C}</c>, <c>{R}</c>, <c>{M}</c> (a comment on the record).</summary>
    private static readonly (string Tool, string Arguments, string Field)[] Cases =
    [
        ("record_create", """{ "collectionId": "{C}", "title": "a{X}" }""", "title"),
        ("record_create", """{ "collectionId": "{C}", "title": "Ok", "body": "{X}" }""", "body"),
        ("record_create", """{ "collectionId": "{C}", "title": "Ok", "properties": { "notes": "{X}" } }""", "properties.notes"),
        ("record_create", """{ "collectionId": "{C}", "properties": { "title": "{X}" } }""", "properties.title"),
        ("record_update", """{ "recordId": "{R}", "title": "{X}" }""", "title"),
        ("record_update", """{ "recordId": "{R}", "body": "{X}" }""", "body"),
        ("record_update", """{ "recordId": "{R}", "properties": { "notes": "x{X}" } }""", "properties.notes"),
        ("record_append", """{ "recordId": "{R}", "markdown": "- {X}" }""", "markdown"),
        ("record_append", """{ "recordId": "{R}", "markdown": "- ok", "section": "{X}" }""", "section"),
        ("space_create", """{ "name": "{X}" }""", "name"),
        ("collection_create", """{ "spaceId": "{S}", "name": "{X}", "description": "Desc." }""", "name"),
        ("collection_create", """{ "spaceId": "{S}", "name": "Neu", "description": "Desc.", "schema": [{ "key": "p", "name": "P", "type": "select", "options": [{ "value": "a", "label": "{X}" }] }] }""", "schema[0].options[0].label"),
        ("collection_update_schema", """{ "collectionId": "{C}", "add": [{ "key": "extra", "name": "{X}", "type": "text" }] }""", "add[0].name"),
        ("view_create", """{ "collectionId": "{C}", "name": "{X}", "type": "table" }""", "name"),
        ("comment_add", """{ "recordId": "{R}", "markdown": "Frage {X}" }""", "markdown"),
        ("comment_update", """{ "commentId": "{M}", "markdown": "{X}" }""", "markdown"),
    ];

    [Theory]
    [InlineData("\\u0000")]
    [InlineData("\\ud800")]
    [InlineData("\\udfff")]
    public async Task Tools_NulOrUnpairedSurrogate_InvalidValueOnTheArgument(string escape)
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "utm");
        var space = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug, "Raum");
        var collection = await SchemaApi.CreateCollectionAsync(owner.Client, workspace.Slug, space.Id, schema:
        [
            new { key = "notes", name = "Notes", type = "text" },
        ]);
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, "Vorher", "Text");
        var comment = await Comments.CommentsApi.AddAsync(owner.Client, workspace.Slug, record.Id, "Kommentar");

        var failures = new List<string>();
        foreach (var (tool, arguments, field) in Cases)
        {
            var filled = arguments
                .Replace("{X}", escape, StringComparison.Ordinal)
                .Replace("{S}", space.Id.ToString(), StringComparison.Ordinal)
                .Replace("{C}", collection.Id.ToString(), StringComparison.Ordinal)
                .Replace("{R}", record.Id.ToString(), StringComparison.Ordinal)
                .Replace("{M}", comment.Id.ToString(), StringComparison.Ordinal);
            var result = await CallRawAsync(owner.Client, workspace.Slug, tool, filled);
            if (!IsInvalidValueOn(result, field))
            {
                failures.Add($"{tool} ({field}): {result.GetRawText()}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        var unchanged = await RecordsApi.GetAsync(owner.Client, workspace.Slug, record.Id);
        Assert.Equal(("Vorher", "Text", 1), (unchanged.Title, unchanged.Body, unchanged.Revision));
        Assert.Equal("Kommentar", Assert.Single((await Comments.CommentsApi.ListAsync(owner.Client, workspace.Slug, record.Id)).Threads).Markdown);
    }

    [Fact]
    public async Task Tools_UnpairedSurrogateInMemberName_InvalidValueOnTheObject_PairsAreFine()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "utm");
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);

        var bad = await CallRawAsync(owner.Client, workspace.Slug, "record_create", $$"""{ "collectionId": "{{collection.Id}}", "title": "Ok", "properties": { "a\ud800": 1 } }""");
        var paired = await CallRawAsync(owner.Client, workspace.Slug, "record_create", $$"""{ "collectionId": "{{collection.Id}}", "title": "Rakete 🚀" }""");

        Assert.True(IsInvalidValueOn(bad, "properties"), bad.GetRawText());
        Assert.False(paired.TryGetProperty("isError", out var isError) && isError.GetBoolean(), paired.GetRawText());
        Assert.Equal("Rakete 🚀", paired.GetProperty("structuredContent").GetProperty("title").GetString());
    }

    /// <summary><c>tools/call</c> as raw JSON-RPC (the server is stateless); returns the <c>result</c> of the answer.</summary>
    private static async Task<JsonElement> CallRawAsync(HttpClient client, string slug, string tool, string arguments)
    {
        var body = $$"""{ "jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": { "name": "{{tool}}", "arguments": {{arguments}} } }""";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(McpClientFixture.WorkspacePath(slug), UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {text}");

        // Streamable HTTP answers either JSON or one SSE event carrying it.
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? string.Concat(text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()))
            : text;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("result", out var result) ? result.Clone() : document.RootElement.Clone();
    }

    private static bool IsInvalidValueOn(JsonElement result, string field) =>
        result.TryGetProperty("isError", out var isError) && isError.GetBoolean()
        && result.TryGetProperty("structuredContent", out var problem)
        && problem.GetProperty("code").GetString() == "validation_failed"
        && problem.TryGetProperty("errors", out var errors)
        && errors.EnumerateArray().Any(e => e.GetProperty("field").GetString() == field && e.GetProperty("code").GetString() == "invalid_value");
}
