using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Mcp;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Records;

namespace Agcowork.Api.Tests.Records;

/// <summary>
/// Relations over REST and MCP (AP-037): <c>POST /records/lookup</c>, <c>GET /records/{id}/backlinks</c>, <c>expandRelations</c>
/// and <c>includeBacklinks</c> on <c>GET /records/{id}</c> and <c>record_get</c>, <c>target_archived</c>, and isolation of all of them.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class RelationsEndpointsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>In Alpha: "Entscheidungen" (self-relation <c>supersedes</c>), "Aufgaben" (<c>decisions</c>, multiple), records linking them.</summary>
    private sealed record Linked(TenancyFixture T, Guid Space, Guid Decisions, Guid Tasks, RecordDto Target, RecordDto Successor, RecordDto Task);

    private async Task<Linked> LinkedAsync()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var client = t.AlphaOwner.Client;
        var slug = t.Alpha.Slug;
        var space = await SchemaApi.CreateSpaceAsync(client, slug, $"Space {Guid.NewGuid():N}");
        var decisions = await SchemaApi.CreateCollectionAsync(client, slug, space.Id, "Entscheidungen");
        using (var self = await client.PatchAsJsonAsync(
                   TenancyFixture.Path(slug, $"/collections/{decisions.Id}/schema"),
                   new { add = new[] { new { key = "supersedes", name = "Supersedes", type = "relation", targetCollectionId = decisions.Id } } },
                   Ct))
        {
            Assert.True(self.IsSuccessStatusCode, await self.Content.ReadAsStringAsync(Ct));
        }

        var tasks = await SchemaApi.CreateCollectionAsync(client, slug, space.Id, "Aufgaben",
            [new { key = "decisions", name = "Decisions", type = "relation", targetCollectionId = decisions.Id, multiple = true }]);
        var target = await RecordsApi.CreateInAsync(client, slug, decisions.Id, "Postgres");
        var successor = await RecordsApi.CreateAsync(client, slug, new { collectionId = decisions.Id, title = "Postgres 2", properties = new { supersedes = target.Id } });
        var task = await RecordsApi.CreateAsync(client, slug, new { collectionId = tasks.Id, title = "Migrieren", properties = new { decisions = new[] { target.Id, successor.Id } } });
        return new Linked(t, space.Id, decisions.Id, tasks.Id, target, successor, task);
    }

    private static string Alpha(TenancyFixture t, string relative) => TenancyFixture.Path(t.Alpha.Slug, relative);

    [Fact]
    public async Task Lookup_TitlesInRequestOrder_ForeignAndUnknownMissing_TooMany400()
    {
        var l = await LinkedAsync();
        var (_, betaCollection) = await RecordsApi.SeedCollectionAsync(l.T.BetaOwner.Client, l.T.Beta.Slug);
        var beta = await RecordsApi.CreateInAsync(l.T.BetaOwner.Client, l.T.Beta.Slug, betaCollection.Id, "Beta geheim");
        var client = l.T.AlphaOwner.Client;

        using var response = await client.PostAsJsonAsync(
            Alpha(l.T, "/records/lookup"), new { ids = new[] { l.Task.Id, beta.Id, Guid.NewGuid(), l.Target.Id, l.Task.Id } }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lookup = (await response.Content.ReadFromJsonAsync<RecordLookupDto>(AgcoworkApiFactory.Json, Ct))!;

        Assert.Equal([(l.Task.Id, "Migrieren", (Guid?)l.Tasks), (l.Target.Id, "Postgres", l.Decisions)], lookup.Items.Select(i => (i.Id, i.Title, i.CollectionId)));
        Assert.All(lookup.Items, i => Assert.Equal((l.Space, false), (i.SpaceId, i.Archived)));

        using var tooMany = await client.PostAsJsonAsync(Alpha(l.T, "/records/lookup"), new { ids = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()) }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(tooMany, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("too_long", ProblemAssert.Field(problem, "ids").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Backlinks_GroupedPaginated_IncludeArchived()
    {
        var l = await LinkedAsync();
        var client = l.T.AlphaOwner.Client;
        var second = await RecordsApi.CreateAsync(client, l.T.Alpha.Slug, new { collectionId = l.Tasks, title = "Zweite", properties = new { decisions = new[] { l.Target.Id } } });
        using (var archive = await client.PostAsJsonAsync(Alpha(l.T, $"/records/{second.Id}/archive"), new { }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        }

        var first = (await client.GetFromJsonAsync<RecordBacklinksDto>(Alpha(l.T, $"/records/{l.Target.Id}/backlinks?limit=1"), AgcoworkApiFactory.Json, Ct))!;
        var archived = (await client.GetFromJsonAsync<RecordBacklinksDto>(Alpha(l.T, $"/records/{l.Target.Id}/backlinks?includeArchived=true"), AgcoworkApiFactory.Json, Ct))!;

        Assert.Equal([("Aufgaben", "decisions", "Decisions"), ("Entscheidungen", "supersedes", "Supersedes")], first.Groups.Select(g => (g.CollectionName, g.PropertyKey, g.PropertyName)));
        Assert.Equal([l.Task.Id], first.Groups[0].Items.Select(i => i.Id));
        Assert.Null(first.Groups[0].NextCursor);
        Assert.Equal([l.Successor.Id], first.Groups[1].Items.Select(i => i.Id));
        Assert.Equal([(second.Id, true), (l.Task.Id, false)], archived.Groups[0].Items.Select(i => (i.Id, i.Archived)));

        var paged = (await client.GetFromJsonAsync<RecordBacklinksDto>(Alpha(l.T, $"/records/{l.Target.Id}/backlinks?limit=1&includeArchived=true"), AgcoworkApiFactory.Json, Ct))!;
        var cursor = paged.Groups[0].NextCursor!;
        var rest = (await client.GetFromJsonAsync<RecordBacklinksDto>(Alpha(l.T, $"/records/{l.Target.Id}/backlinks?limit=1&includeArchived=true&cursor={cursor}"), AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal([second.Id], paged.Groups[0].Items.Select(i => i.Id));
        Assert.Equal([l.Task.Id], Assert.Single(rest.Groups).Items.Select(i => i.Id));
        Assert.Null(rest.Groups[0].NextCursor);

        Assert.Equal(l.Tasks, paged.Groups[0].More!.CollectionId);
        Assert.Equal(("decisions", "contains", l.Target.Id), (paged.Groups[0].More!.Filter.Prop, paged.Groups[0].More!.Filter.Op, paged.Groups[0].More!.Filter.Value));
        Assert.Null(rest.Groups[0].More);

        using var bad = await client.GetAsync(Alpha(l.T, $"/records/{l.Target.Id}/backlinks?cursor=xyz"), Ct);
        await ProblemAssert.IsProblemAsync(bad, HttpStatusCode.BadRequest, "validation_failed");

        // The cursor belongs to its record and includeArchived setting.
        using var otherSetting = await client.GetAsync(Alpha(l.T, $"/records/{l.Target.Id}/backlinks?limit=1&cursor={cursor}"), Ct);
        using var otherRecord = await client.GetAsync(Alpha(l.T, $"/records/{l.Successor.Id}/backlinks?limit=1&includeArchived=true&cursor={cursor}"), Ct);
        foreach (var response in new[] { otherSetting, otherRecord })
        {
            var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Equal("invalid_format", ProblemAssert.Field(problem, "cursor").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task GetRecord_ExpandRelations_IncludeBacklinks_JsonAndMarkdown()
    {
        var l = await LinkedAsync();
        var client = l.T.AlphaOwner.Client;

        var json = await SchemaApi.ReadJsonAsync(await client.GetAsync(Alpha(l.T, $"/records/{l.Task.Id}?expandRelations=true"), Ct));
        var plain = await SchemaApi.ReadJsonAsync(await client.GetAsync(Alpha(l.T, $"/records/{l.Task.Id}"), Ct));
        var target = await RecordsApi.GetAsync(client, l.T.Alpha.Slug, l.Target.Id, "?includeBacklinks=true");
        var markdown = await client.GetStringAsync(Alpha(l.T, $"/records/{l.Task.Id}?format=markdown&expandRelations=true"), Ct);
        var markdownBacklinks = await client.GetStringAsync(Alpha(l.T, $"/records/{l.Target.Id}?format=markdown&includeBacklinks=true"), Ct);

        var decisions = json.GetProperty("relations").GetProperty("decisions");
        Assert.Equal(["Postgres", "Postgres 2"], decisions.EnumerateArray().Select(d => d.GetProperty("title").GetString()));
        Assert.All(decisions.EnumerateArray(), d => Assert.False(d.GetProperty("archived").GetBoolean()));
        Assert.False(plain.TryGetProperty("relations", out _));
        Assert.False(plain.TryGetProperty("backlinks", out _));
        Assert.Equal(["decisions", "supersedes"], target.Backlinks!.Groups.Select(g => g.PropertyKey));
        Assert.Contains($"  - {l.Target.Id} # \"Postgres\"\n  - {l.Successor.Id} # \"Postgres 2\"\n", markdown, StringComparison.Ordinal);
        Assert.Contains("# Backlinks", markdownBacklinks, StringComparison.Ordinal);
        Assert.Contains($"  - {l.Task.Id} \"Migrieren\"\n", markdownBacklinks, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordGet_Mcp_ExpandRelations_IncludeBacklinks_SameAsRest()
    {
        var l = await LinkedAsync();
        await using var mcp = await McpClientFixture.ConnectAsync(factory, l.T.AlphaOwner.Key, l.T.Alpha.Slug);

        var markdown = await McpClientFixture.CallAsync(mcp, "record_get", new { recordId = l.Task.Id, expandRelations = true, includeBacklinks = true });
        var json = McpClientFixture.Ok(await McpClientFixture.CallAsync(mcp, "record_get", new { recordId = l.Target.Id, format = "json", expandRelations = true, includeBacklinks = true }));
        var rest = await SchemaApi.ReadJsonAsync(await l.T.AlphaOwner.Client.GetAsync(Alpha(l.T, $"/records/{l.Target.Id}?includeBody=true&expandRelations=true&includeBacklinks=true"), Ct));

        var text = McpClientFixture.Text(markdown);
        var structured = McpClientFixture.Ok(markdown);
        Assert.Contains($"  - {l.Target.Id} # \"Postgres\"\n", text, StringComparison.Ordinal);
        Assert.Contains("No active records reference this record.", text, StringComparison.Ordinal);
        Assert.Equal("Postgres 2", structured.GetProperty("relations").GetProperty("decisions")[1].GetProperty("title").GetString());
        Assert.Empty(structured.GetProperty("backlinks").GetProperty("groups").EnumerateArray());
        Assert.True(JsonElement.DeepEquals(rest.GetProperty("backlinks"), json.GetProperty("backlinks")));
        Assert.True(JsonElement.DeepEquals(rest.GetProperty("relations"), json.GetProperty("relations")));
        Assert.Equal(2, json.GetProperty("backlinks").GetProperty("groups").GetArrayLength());
    }

    [Fact]
    public async Task RecordGet_Mcp_GroupWithMore_RecordsQueryListsAll()
    {
        var l = await LinkedAsync();
        for (var i = 0; i < 11; i++)
        {
            await RecordsApi.CreateAsync(l.T.AlphaOwner.Client, l.T.Alpha.Slug, new { collectionId = l.Tasks, title = $"Aufgabe {i}", properties = new { decisions = new[] { l.Target.Id } } });
        }

        await using var mcp = await McpClientFixture.ConnectAsync(factory, l.T.AlphaOwner.Key, l.T.Alpha.Slug);
        var json = McpClientFixture.Ok(await McpClientFixture.CallAsync(mcp, "record_get", new { recordId = l.Target.Id, format = "json", includeBacklinks = true }));
        var markdown = McpClientFixture.Ok(await McpClientFixture.CallAsync(mcp, "record_get", new { recordId = l.Target.Id, includeBacklinks = true }));

        var group = json.GetProperty("backlinks").GetProperty("groups")[0];
        Assert.Equal(10, group.GetProperty("items").GetArrayLength());
        var more = group.GetProperty("more");
        Assert.True(JsonElement.DeepEquals(more, markdown.GetProperty("backlinks").GetProperty("groups")[0].GetProperty("more")));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("backlinks").GetProperty("groups")[1].GetProperty("more").ValueKind);

        var all = McpClientFixture.Ok(await McpClientFixture.CallAsync(mcp, "records_query", new
        {
            collectionId = more.GetProperty("collectionId").GetGuid(),
            filter = more.GetProperty("filter"),
            limit = 100,
        }));
        Assert.Equal(12, all.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task NewRelation_ToArchivedCollection_400TargetArchived()
    {
        var l = await LinkedAsync();
        var client = l.T.AlphaOwner.Client;
        using (var archive = await client.PostAsJsonAsync(Alpha(l.T, $"/collections/{l.Decisions}/archive"), new { }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        }

        using var create = await client.PostAsJsonAsync(Alpha(l.T, "/collections"), new
        {
            spaceId = l.Space,
            name = $"Neu {Guid.NewGuid():N}",
            description = "Links to archived decisions.",
            schema = new[] { new { key = "ref", name = "Ref", type = "relation", targetCollectionId = l.Decisions } },
        }, Ct);
        using var add = await client.PatchAsJsonAsync(Alpha(l.T, $"/collections/{l.Tasks}/schema"), new
        {
            add = new[] { new { key = "other", name = "Other", type = "relation", targetCollectionId = l.Decisions } },
        }, Ct);

        var created = await ProblemAssert.IsProblemAsync(create, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("target_archived", ProblemAssert.Field(created, "schema[0].targetCollectionId").GetProperty("code").GetString());
        var added = await ProblemAssert.IsProblemAsync(add, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("target_archived", ProblemAssert.Field(added, "add[0].targetCollectionId").GetProperty("code").GetString());

        // The existing relation still reads and resolves (archived).
        var task = await SchemaApi.ReadJsonAsync(await client.GetAsync(Alpha(l.T, $"/records/{l.Task.Id}?expandRelations=true"), Ct));
        Assert.All(task.GetProperty("relations").GetProperty("decisions").EnumerateArray(), d => Assert.True(d.GetProperty("archived").GetBoolean()));
    }

    // ---- Isolation ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LookupRecords_Isolated()
    {
        var l = await LinkedAsync();
        await l.T.AssertIsolatedAsync(HttpMethod.Post, "/records/lookup", new { ids = new[] { l.Task.Id } }, homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetRecordBacklinks_Isolated()
    {
        var l = await LinkedAsync();
        await l.T.AssertIsolatedAsync(HttpMethod.Get, $"/records/{l.Target.Id}/backlinks", homeStatus: HttpStatusCode.OK);
        await l.T.AssertIsolatedAsync(HttpMethod.Get, $"/records/{l.Target.Id}?expandRelations=true&includeBacklinks=true", homeStatus: HttpStatusCode.OK);
        await l.T.AssertIsolatedAsync(HttpMethod.Get, $"/records/{l.Target.Id}?format=markdown&expandRelations=true&includeBacklinks=true", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForeignRecordIds_Lookup_Missing_Backlinks_NotFound_RestAndMcp()
    {
        var l = await LinkedAsync();
        var (_, betaCollection) = await RecordsApi.SeedCollectionAsync(l.T.BetaOwner.Client, l.T.Beta.Slug);
        var beta = await RecordsApi.CreateInAsync(l.T.BetaOwner.Client, l.T.Beta.Slug, betaCollection.Id, "Beta geheim");
        var client = l.T.AlphaOwner.Client;

        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha(l.T, $"/records/{beta.Id}/backlinks"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha(l.T, $"/records/{beta.Id}?expandRelations=true&includeBacklinks=true"));
        using (var lookup = await client.PostAsJsonAsync(Alpha(l.T, "/records/lookup"), new { ids = new[] { beta.Id } }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
            Assert.Empty((await lookup.Content.ReadFromJsonAsync<RecordLookupDto>(AgcoworkApiFactory.Json, Ct))!.Items);
        }

        await using var mcp = await McpClientFixture.ConnectAsync(factory, l.T.AlphaOwner.Key, l.T.Alpha.Slug);
        foreach (var args in new object[]
                 {
                     new { recordId = beta.Id, expandRelations = true, includeBacklinks = true },
                     new { recordId = beta.Id, format = "json", expandRelations = true, includeBacklinks = true },
                 })
        {
            var problem = McpClientFixture.Error(await McpClientFixture.CallAsync(mcp, "record_get", args), 404, "not_found");
            Assert.False(problem.TryGetProperty("detail", out _));
        }

        await McpClientFixture.AssertToolIsolatedAsync(l.T, mcp, "record_get", new { recordId = l.Target.Id, expandRelations = true, includeBacklinks = true });
    }
}
