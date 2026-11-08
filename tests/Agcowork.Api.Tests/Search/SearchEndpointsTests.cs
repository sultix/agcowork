using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Mcp;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Search;
using DomainRecord = Agcowork.Domain.Records.Record;

namespace Agcowork.Api.Tests.Search;

/// <summary><c>GET /search</c> and the MCP tool <c>search</c> end to end (AP-026), including isolation.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SearchEndpointsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A word no other test uses (the fixture workspaces are shared).</summary>
    private static string Word(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 12)].Replace('-', 'x');

    private static async Task<Page<SearchHitDto>> SearchAsync(HttpClient client, string slug, string query)
    {
        using var response = await client.GetAsync(new Uri(TenancyFixture.Path(slug, "/search?" + query), UriKind.Relative), Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<Page<SearchHitDto>>(AgcoworkApiFactory.Json, Ct))!;
    }

    private static string Q(string q) => "q=" + Uri.EscapeDataString(q);

    [Fact]
    public async Task Search_Rest_FindsAndReflectsPatch_WireShape()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var client = t.AlphaOwner.Client;
        var (space, collection) = await RecordsApi.SeedCollectionAsync(client, t.Alpha.Slug);
        var oldWord = Word("alt");
        var newWord = Word("neu");
        var record = await RecordsApi.CreateAsync(client, t.Alpha.Slug, new { collectionId = collection.Id, title = $"Titel {oldWord}", body = $"Text mit {oldWord}." });

        using (var raw = await client.GetAsync(new Uri(TenancyFixture.Path(t.Alpha.Slug, "/search?" + Q(oldWord)), UriKind.Relative), Ct))
        {
            var json = await SchemaApi.ReadJsonAsync(raw);
            var hit = Assert.Single(json.GetProperty("items").EnumerateArray());
            Assert.Equal(
                ["archived", "collectionId", "rank", "recordId", "snippet", "spaceId", "title", "updatedAt"],
                ProblemAssert.PropertyNames(hit).Order(StringComparer.Ordinal));
            Assert.Equal(record.Id, hit.GetProperty("recordId").GetGuid());
            Assert.Equal(space.Id, hit.GetProperty("spaceId").GetGuid());
            Assert.Equal(collection.Id, hit.GetProperty("collectionId").GetGuid());
            Assert.Contains($"⟦{oldWord}⟧", hit.GetProperty("snippet").GetString(), StringComparison.Ordinal);
            // Required members travel also as null (AP-033): nextCursor is null on the last page.
            Assert.Equal(JsonValueKind.Null, json.GetProperty("nextCursor").ValueKind);
        }

        using var patch = await client.PatchAsJsonAsync(
            TenancyFixture.Path(t.Alpha.Slug, $"/records/{record.Id}"), new { title = $"Titel {newWord}", body = "Anderer Text." }, AgcoworkApiFactory.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        Assert.Empty((await SearchAsync(client, t.Alpha.Slug, Q(oldWord))).Items);
        Assert.Equal([record.Id], (await SearchAsync(client, t.Alpha.Slug, Q(newWord))).Items.Select(h => h.RecordId));
        Assert.Equal([record.Id], (await SearchAsync(client, t.Alpha.Slug, Q(newWord[..6]) + $"&collectionId={collection.Id}&limit=5")).Items.Select(h => h.RecordId));
    }

    [Fact]
    public async Task Search_Rest_MaximalBody_CreateAndPatch()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var client = t.AlphaOwner.Client;
        var (_, collection) = await RecordsApi.SeedCollectionAsync(client, t.Alpha.Slug);
        var first = Word("gross");
        var record = await RecordsApi.CreateAsync(client, t.Alpha.Slug, new { collectionId = collection.Id, title = "Groß", body = MaxBody(first) });
        Assert.Equal([record.Id], (await SearchAsync(client, t.Alpha.Slug, Q(first))).Items.Select(h => h.RecordId));

        var second = Word("riesig");
        using var patch = await client.PatchAsJsonAsync(
            TenancyFixture.Path(t.Alpha.Slug, $"/records/{record.Id}"), new { body = MaxBody(second) }, AgcoworkApiFactory.Json, Ct);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal([record.Id], (await SearchAsync(client, t.Alpha.Slug, Q(second))).Items.Select(h => h.RecordId));
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("q=", "empty_query")]
    [InlineData("q=%20%20", "empty_query")]
    [InlineData("q=!!!%20...", "empty_query")]
    [InlineData("q=-kafka", "empty_query")]
    [InlineData("q=a%00b", "invalid_value")]
    [InlineData("q=a%01b", "invalid_value")]
    public async Task Search_Rest_UnusableQuery_400WithCode(string query, string code)
    {
        var t = await TenancyFixture.GetAsync(factory);

        using var response = await t.AlphaOwner.Client.GetAsync(new Uri(TenancyFixture.Path(t.Alpha.Slug, "/search?" + query), UriKind.Relative), Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(code, ProblemAssert.Field(problem, "q").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Search_Rest_TooLongLimitCursor_400()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var client = t.AlphaOwner.Client;

        async Task AssertFieldAsync(string query, string field, string code)
        {
            using var response = await client.GetAsync(new Uri(TenancyFixture.Path(t.Alpha.Slug, "/search?" + query), UriKind.Relative), Ct);
            var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Equal(code, ProblemAssert.Field(problem, field).GetProperty("code").GetString());
        }

        await AssertFieldAsync(Q(new string('a', 201)), "q", "too_long");
        await AssertFieldAsync(Q("wort") + "&limit=51", "limit", "out_of_range");
        await AssertFieldAsync(Q("wort") + "&cursor=kaputt", "cursor", "invalid_cursor");
    }

    [Fact]
    public async Task Search_Rest_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var word = Word("geheim");
        var betaSpace = await SchemaApi.CreateSpaceAsync(t.BetaOwner.Client, t.Beta.Slug, $"Space {Guid.NewGuid():N}");
        await RecordsApi.CreateAsync(t.BetaOwner.Client, t.Beta.Slug, new { spaceId = betaSpace.Id, title = $"Beta {word}", body = word });
        Assert.Single((await SearchAsync(t.BetaOwner.Client, t.Beta.Slug, Q(word))).Items);

        await t.AssertIsolatedAsync(HttpMethod.Get, "/search?" + Q(word), homeStatus: HttpStatusCode.OK);
        await t.AssertIsolatedAsync(HttpMethod.Get, "/search", homeStatus: HttpStatusCode.BadRequest);
        Assert.Empty((await SearchAsync(t.AlphaOwner.Client, t.Alpha.Slug, Q(word) + "&includeArchived=true")).Items);
        Assert.Empty((await SearchAsync(t.RestrictedToAlpha.Client, t.Alpha.Slug, Q(word))).Items);

        // Beta's space id in Alpha: 404 without a hint, like an unknown id.
        await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Get, TenancyFixture.Path(t.Alpha.Slug, $"/search?{Q(word)}&spaceId={betaSpace.Id}"));
    }

    [Fact]
    public async Task Search_Mcp_FilterResultAndStructuredErrors_BetaInvisible()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, collection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var (_, other) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var word = Word("agent");
        var inCollection = await RecordsApi.CreateInAsync(t.AlphaOwner.Client, t.Alpha.Slug, collection.Id, $"Treffer {word}", "Body <script>x</script>");
        await RecordsApi.CreateInAsync(t.AlphaOwner.Client, t.Alpha.Slug, other.Id, "Anderer", $"Im Text {word}");
        var betaSpace = await SchemaApi.CreateSpaceAsync(t.BetaOwner.Client, t.Beta.Slug, $"Space {Guid.NewGuid():N}");
        await RecordsApi.CreateAsync(t.BetaOwner.Client, t.Beta.Slug, new { spaceId = betaSpace.Id, title = $"Beta {word}" });
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        var all = await McpClientFixture.OkAsync<Page<SearchHitDto>>(client, "search", new { query = word });
        Assert.Equal(2, all.Items.Count);
        Assert.Equal(inCollection.Id, all.Items[0].RecordId);

        var result = await McpClientFixture.CallAsync(client, "search", new { query = word[..8], collectionId = collection.Id, limit = 5 });
        var filtered = McpClientFixture.Ok(result).Deserialize<Page<SearchHitDto>>(AgcoworkApiFactory.Json)!;
        Assert.Equal([inCollection.Id], filtered.Items.Select(h => h.RecordId));
        Assert.Contains(inCollection.Id.ToString(), McpClientFixture.Text(result), StringComparison.Ordinal);

        var empty = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "search", new { query = "!!!" }), 400, "validation_failed");
        var error = Assert.Single(empty.GetProperty("errors").EnumerateArray());
        Assert.Equal("query", error.GetProperty("field").GetString());
        Assert.Equal("empty_query", error.GetProperty("code").GetString());
        Assert.Contains("'query'", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        McpClientFixture.Error(await McpClientFixture.CallAsync(client, "search", new { query = "a\0b" }), 400, "validation_failed");
        McpClientFixture.Error(await McpClientFixture.CallAsync(client, "search", new { query = word, spaceId = betaSpace.Id }), 404, "not_found");
        McpClientFixture.Error(await McpClientFixture.CallAsync(client, "search", new { }), 400, "validation_failed");
    }

    private static string MaxBody(string first)
    {
        var text = new StringBuilder(first).Append(' ');
        while (text.Length + 12 <= DomainRecord.BodyMaxBytes)
        {
            text.Append("lorem ipsum ");
        }

        text.Append('x', DomainRecord.BodyMaxBytes - text.Length);
        return text.ToString();
    }
}
