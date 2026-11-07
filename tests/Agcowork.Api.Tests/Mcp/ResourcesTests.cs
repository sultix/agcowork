using System.Text.Json;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>Resources on <c>/w/{slug}/mcp</c>: templates, listing, reading, and foreign ids without a hint of existence.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ResourcesTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListResourceTemplates_TheThreeUriPatterns()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        var templates = await client.ListResourceTemplatesAsync(cancellationToken: Ct);

        Assert.Equal(
            ["agcowork://collection/{id}/schema", "agcowork://record/{id}", "agcowork://space/{id}/overview"],
            templates.Select(x => x.UriTemplate).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ListResources_CollectionSchemasAndSpaceOverviewsOfThisWorkspaceOnly()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (space, collection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var (betaSpace, betaCollection) = await RecordsApi.SeedCollectionAsync(t.BetaOwner.Client, t.Beta.Slug);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        var uris = (await client.ListResourcesAsync(cancellationToken: Ct)).Select(r => r.Uri).ToList();

        Assert.Contains($"agcowork://collection/{collection.Id}/schema", uris);
        Assert.Contains($"agcowork://space/{space.Id}/overview", uris);
        Assert.DoesNotContain($"agcowork://collection/{betaCollection.Id}/schema", uris);
        Assert.DoesNotContain($"agcowork://space/{betaSpace.Id}/overview", uris);
    }

    [Fact]
    public async Task ReadResource_RecordSchemaAndSpaceOverview()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (space, collection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var record = await RecordsApi.CreateInAsync(t.AlphaOwner.Client, t.Alpha.Slug, collection.Id, "Als Resource", body: "## Inhalt\nText");
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        var markdown = Single(await client.ReadResourceAsync($"agcowork://record/{record.Id}", cancellationToken: Ct));
        Assert.Equal("text/markdown", markdown.MimeType);
        Assert.StartsWith("---\n", markdown.Text, StringComparison.Ordinal);
        Assert.Contains($"id: {record.Id}", markdown.Text, StringComparison.Ordinal);
        Assert.Contains("## Inhalt", markdown.Text, StringComparison.Ordinal);

        var schema = Single(await client.ReadResourceAsync($"agcowork://collection/{collection.Id}/schema", cancellationToken: Ct));
        Assert.Equal("application/schema+json", schema.MimeType);
        var jsonSchema = JsonDocument.Parse(schema.Text).RootElement;
        Assert.Equal("object", jsonSchema.GetProperty("type").GetString());
        Assert.True(jsonSchema.GetProperty("properties").TryGetProperty("status", out _));

        var overview = Single(await client.ReadResourceAsync($"agcowork://space/{space.Id}/overview", cancellationToken: Ct));
        Assert.Contains(space.Name, overview.Text, StringComparison.Ordinal);
        Assert.Contains(collection.Id.ToString(), overview.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadResource_ForeignOrUnknownIds_SameNotFoundError()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (betaSpace, betaCollection) = await RecordsApi.SeedCollectionAsync(t.BetaOwner.Client, t.Beta.Slug);
        var betaRecord = await RecordsApi.CreateInAsync(t.BetaOwner.Client, t.Beta.Slug, betaCollection.Id);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        string[] uris =
        [
            $"agcowork://record/{betaRecord.Id}",
            $"agcowork://collection/{betaCollection.Id}/schema",
            $"agcowork://space/{betaSpace.Id}/overview",
            $"agcowork://record/{Guid.NewGuid()}",
            "agcowork://record/not-a-guid",
        ];
        foreach (var uri in uris)
        {
            var exception = await Assert.ThrowsAsync<McpProtocolException>(() => client.ReadResourceAsync(uri, cancellationToken: Ct).AsTask());
            Assert.Equal(McpErrorCode.ResourceNotFound, exception.ErrorCode);
            Assert.Contains("Resource not found.", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(betaRecord.Id.ToString(), exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReadResource_OtherWorkspaceEndpoint_TransportNotFound()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, collection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var read = new { uri = $"agcowork://collection/{collection.Id}/schema" };

        await McpClientFixture.AssertTransportNotFoundAsync(t.AlphaOwner.Client, McpClientFixture.WorkspacePath(t.Beta.Slug), read, "resources/read");
        await McpClientFixture.AssertTransportNotFoundAsync(t.Outsider.Client, McpClientFixture.WorkspacePath(t.Alpha.Slug), read, "resources/read");
        await McpClientFixture.AssertTransportNotFoundAsync(t.RestrictedToAlpha.Client, McpClientFixture.WorkspacePath(t.Beta.Slug), read, "resources/read");
        await McpClientFixture.AssertTransportNotFoundAsync(t.Outsider.Client, McpClientFixture.WorkspacePath(t.Alpha.Slug), new { }, "resources/list");
    }

    private static TextResourceContents Single(ReadResourceResult result) => Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
}
