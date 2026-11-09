using System.Net;
using System.Text.Json;

namespace Agcowork.Api.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class HealthAndOpenApiTests(AgcoworkApiFactory factory)
{
    [Fact]
    public async Task GetHealth_ProcessRunning_Returns200()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetHealthReady_DatabaseReachable_Returns200()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetOpenApiDocument_Default_Returns200WithValidJson()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(document.RootElement.TryGetProperty("openapi", out var version));
        Assert.StartsWith("3.", version.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOpenApiDocument_OptionalBodyFields_AreNotRequired()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        string[] Required(string schema) => schemas.GetProperty(schema).TryGetProperty("required", out var list)
            ? [.. list.EnumerateArray().Select(e => e.GetString()!)]
            : [];
        Assert.True(schemas.GetProperty("CreateRecordBody").GetProperty("properties").TryGetProperty("skipTemplate", out _));
        Assert.DoesNotContain("skipTemplate", Required("CreateRecordBody"));
        // A schema default would make generated clients treat the field as always present; nullable and optional keeps old clients compiling.
        var skipTemplate = schemas.GetProperty("CreateRecordBody").GetProperty("properties").GetProperty("skipTemplate");
        Assert.False(skipTemplate.TryGetProperty("default", out _));
        Assert.DoesNotContain("templateId", Required("CreateRecordBody"));
        Assert.True(schemas.GetProperty("PropertyChangeInput").GetProperty("properties").TryGetProperty("clearPrecision", out _));
        Assert.Equal(["key"], Required("PropertyChangeInput"));
    }

    [Fact]
    public async Task McpRecordCreate_SkipTemplate_IsOptionalInInputSchema()
    {
        var t = await Tenancy.TenancyFixture.GetAsync(factory);
        await using var mcp = await Mcp.McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        var tool = (await mcp.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Single(x => x.Name == "record_create");

        Assert.True(tool.JsonSchema.GetProperty("properties").TryGetProperty("skipTemplate", out _));
        var required = tool.JsonSchema.TryGetProperty("required", out var list) ? list.EnumerateArray().Select(e => e.GetString()).ToList() : [];
        Assert.DoesNotContain("skipTemplate", required);
    }
}
