using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Activity;
using Agcowork.Application.Common.Paging;

namespace Agcowork.Api.Tests.Activity;

[Collection(ApiTestGroup.Name)]
public sealed class ActivityEndpointsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Activity_ShowsRecordSpaceCollectionAndMemberEvents_FilterableAndPaginated()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "act");
        var editor = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, editor.ActorId, "editor");
        var (space, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var record = await RecordsApi.CreateInAsync(editor.Client, workspace.Slug, collection.Id);
        using (var patch = await editor.Client.PatchAsJsonAsync(TenancyFixture.Path(workspace.Slug, $"/records/{record.Id}"), new { properties = new { status = "done" }, body = "Neu" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }

        string Feed(string query) => TenancyFixture.Path(workspace.Slug, $"/activity{query}");
        var all = await editor.Client.GetFromJsonAsync<Page<ActivityEventDto>>(Feed(string.Empty), AgcoworkApiFactory.Json, Ct);

        Assert.Equal(["record.updated", "record.created", "collection.created", "space.created", "member.added"], all!.Items.Select(e => e.Type));
        var updated = all.Items[0];
        Assert.Equal((editor.ActorId, "record", record.Id, space.Id, (Guid?)collection.Id), (updated.ActorId, updated.TargetType, updated.TargetId, updated.SpaceId, updated.CollectionId));
        Assert.Equal("done", updated.Diff["properties"]!["status"]!["new"]!.GetValue<string>());
        Assert.True(updated.Diff["bodyChanged"]!.GetValue<bool>());
        Assert.Equal((owner.ActorId, editor.ActorId), (all.Items[4].ActorId, all.Items[4].TargetId));

        var records = await editor.Client.GetFromJsonAsync<Page<ActivityEventDto>>(Feed("?types=record.created&types=record.updated"), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(2, records!.Items.Count);
        var byOwner = await editor.Client.GetFromJsonAsync<Page<ActivityEventDto>>(Feed($"?actorId={owner.ActorId}"), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(["collection.created", "space.created", "member.added"], byOwner!.Items.Select(e => e.Type));
        var future = await editor.Client.GetFromJsonAsync<Page<ActivityEventDto>>(Feed($"?since={DateTimeOffset.UtcNow.AddHours(1):O}".Replace("+", "%2B", StringComparison.Ordinal)), AgcoworkApiFactory.Json, Ct);
        Assert.Empty(future!.Items);

        var first = await editor.Client.GetFromJsonAsync<Page<ActivityEventDto>>(Feed("?limit=3"), AgcoworkApiFactory.Json, Ct);
        var second = await editor.Client.GetFromJsonAsync<Page<ActivityEventDto>>(Feed($"?limit=3&cursor={first!.NextCursor}"), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(all.Items.Select(e => e.Seq), first.Items.Concat(second!.Items).Select(e => e.Seq));
        Assert.Null(second.NextCursor);

        using var invalid = await editor.Client.GetAsync(new Uri(Feed("?types=record.deleted"), UriKind.Relative), Ct);
        var problem = await ProblemAssert.IsProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_failed");
        Assert.True(ProblemAssert.Field(problem, "types[0]").GetProperty("allowed").GetArrayLength() > 10);
    }

    [Fact]
    public async Task Activity_TargetTitle_CurrentTitleAfterRename()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "acttitle");
        var (space, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, "Vorher");
        using (var patch = await owner.Client.PatchAsJsonAsync(TenancyFixture.Path(workspace.Slug, $"/records/{record.Id}"), new { title = "Nachher" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }

        var feed = await owner.Client.GetFromJsonAsync<Page<ActivityEventDto>>(TenancyFixture.Path(workspace.Slug, "/activity"), AgcoworkApiFactory.Json, Ct);

        Assert.Equal(["Nachher", "Nachher"], feed!.Items.Where(e => e.TargetId == record.Id).Select(e => e.TargetTitle));
        Assert.Equal(collection.Name, feed.Items.Single(e => e.TargetId == collection.Id).TargetTitle);
        Assert.Equal(space.Name, feed.Items.Single(e => e.TargetId == space.Id).TargetTitle);
    }

    [Fact]
    public async Task ListActivity_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Get, "/activity", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task Activity_ShowsOnlyOwnWorkspace()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, betaCollection) = await RecordsApi.SeedCollectionAsync(t.BetaOwner.Client, t.Beta.Slug);
        var beta = await RecordsApi.CreateInAsync(t.BetaOwner.Client, t.Beta.Slug, betaCollection.Id);

        var feed = await t.AlphaOwner.Client.GetFromJsonAsync<Page<ActivityEventDto>>(TenancyFixture.Path(t.Alpha.Slug, "/activity?limit=200"), AgcoworkApiFactory.Json, Ct);

        Assert.DoesNotContain(feed!.Items, e => e.TargetId == beta.Id || e.CollectionId == betaCollection.Id);
    }

    [Fact]
    public async Task Activity_AfterSeq_ForwardAscendingWithLimitAndLastSeq_TargetIdFilter()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "actfwd");
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var first = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, "Erste");
        var second = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, "Zweite");
        using (var patch = await owner.Client.PatchAsJsonAsync(TenancyFixture.Path(workspace.Slug, $"/records/{first.Id}"), new { title = "Erste neu" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        }

        string Feed(string query) => TenancyFixture.Path(workspace.Slug, $"/activity{query}");
        var newestFirst = (await owner.Client.GetFromJsonAsync<ActivityPage>(Feed(string.Empty), AgcoworkApiFactory.Json, Ct))!;
        var seen = new List<ActivityEventDto>();
        var lastSeq = 0L;
        ActivityPage page;
        do
        {
            page = (await owner.Client.GetFromJsonAsync<ActivityPage>(Feed($"?afterSeq={lastSeq}&limit=2"), AgcoworkApiFactory.Json, Ct))!;
            Assert.True(page.Items.Count <= 2);
            Assert.Null(page.NextCursor);
            seen.AddRange(page.Items);
            lastSeq = page.LastSeq;
        }
        while (page.Items.Count > 0);

        Assert.Equal(newestFirst.Items.Select(e => e.Seq).Reverse(), seen.Select(e => e.Seq));
        Assert.Equal(seen[^1].Seq, lastSeq);
        Assert.Equal(lastSeq, newestFirst.LastSeq);
        Assert.Equal("Erste neu", seen.Single(e => e.Type == "record.updated").TargetTitle);

        var history = (await owner.Client.GetFromJsonAsync<ActivityPage>(Feed($"?afterSeq=0&targetId={first.Id}"), AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal(["record.created", "record.updated"], history.Items.Select(e => e.Type));
        var newestFirstHistory = (await owner.Client.GetFromJsonAsync<ActivityPage>(Feed($"?targetId={second.Id}"), AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal(second.Id, Assert.Single(newestFirstHistory.Items).TargetId);
    }

    [Fact]
    public async Task Activity_AfterSeq_InvalidOrForeign_400WithField()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, betaCollection) = await RecordsApi.SeedCollectionAsync(t.BetaOwner.Client, t.Beta.Slug);
        await RecordsApi.CreateInAsync(t.BetaOwner.Client, t.Beta.Slug, betaCollection.Id);
        var betaSeq = (await t.BetaOwner.Client.GetFromJsonAsync<ActivityPage>(TenancyFixture.Path(t.Beta.Slug, "/activity?limit=1"), AgcoworkApiFactory.Json, Ct))!.Items[0].Seq;

        async Task<string> FieldCodeAsync(string query, string field)
        {
            using var response = await t.AlphaOwner.Client.GetAsync(new Uri(TenancyFixture.Path(t.Alpha.Slug, $"/activity{query}"), UriKind.Relative), Ct);
            var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
            return ProblemAssert.Field(problem, field).GetProperty("code").GetString()!;
        }

        Assert.Equal("out_of_range", await FieldCodeAsync("?afterSeq=-1", "afterSeq"));
        Assert.Equal("not_allowed", await FieldCodeAsync("?afterSeq=0&cursor=MQ", "cursor"));
        Assert.Equal("invalid_cursor", await FieldCodeAsync($"?afterSeq={long.MaxValue}", "afterSeq"));
        // A seq of another workspace is unknown here, exactly like one that does not exist.
        Assert.Equal("invalid_cursor", await FieldCodeAsync($"?afterSeq={betaSeq}", "afterSeq"));
    }

    [Fact]
    public async Task ListActivity_AfterSeqAndTargetId_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Get, "/activity?afterSeq=0", homeStatus: HttpStatusCode.OK);
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/activity?targetId={t.Alpha.Id}", homeStatus: HttpStatusCode.OK);
    }
}
