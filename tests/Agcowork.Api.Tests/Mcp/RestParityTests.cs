using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>REST and MCP call the same use cases: same input, same result (apart from ids and timestamps), same errors.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RestParityTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Volatile = ["id", "createdAt", "updatedAt", "traceId"];

    [Fact]
    public async Task RecordCreate_RestAndMcp_SameResult()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, collection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var input = new { collectionId = collection.Id, title = "Parität", properties = new { status = "doing", tags = new[] { "db", "api" } }, body = "## Text\n" };

        using var rest = await t.AlphaOwner.Client.PostAsJsonAsync(TenancyFixture.Path(t.Alpha.Slug, "/records"), input, AgcoworkApiFactory.Json, Ct);
        Assert.Equal(HttpStatusCode.Created, rest.StatusCode);
        var restJson = JsonNode.Parse(await rest.Content.ReadAsStringAsync(Ct))!;

        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);
        var mcpJson = JsonNode.Parse(McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "record_create", input)).GetRawText())!;

        Assert.Equal(Normalize(restJson), Normalize(mcpJson));
    }

    [Fact]
    public async Task RecordCreate_InvalidInput_RestAndMcp_SameProblem()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, collection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var input = new { collectionId = collection.Id, title = "Falsch", properties = new { status = "later", colour = "red" } };

        using var rest = await t.AlphaOwner.Client.PostAsJsonAsync(TenancyFixture.Path(t.Alpha.Slug, "/records"), input, AgcoworkApiFactory.Json, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, rest.StatusCode);
        var restProblem = JsonNode.Parse(await rest.Content.ReadAsStringAsync(Ct))!.AsObject();

        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);
        var mcpProblem = JsonNode.Parse(McpClientFixture.Error(await McpClientFixture.CallAsync(client, "record_create", input), 400, "validation_failed").GetRawText())!.AsObject();

        foreach (var member in new[] { "status", "code", "detail", "errors" })
        {
            Assert.True(JsonNode.DeepEquals(restProblem[member], mcpProblem[member]), $"'{member}' differs: REST {restProblem[member]} vs MCP {mcpProblem[member]}");
        }
    }

    /// <summary>Drops ids and timestamps (they differ by nature) and compares the rest.</summary>
    private static string Normalize(JsonNode node)
    {
        var copy = node.DeepClone().AsObject();
        foreach (var name in Volatile)
        {
            copy.Remove(name);
        }

        return copy.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
