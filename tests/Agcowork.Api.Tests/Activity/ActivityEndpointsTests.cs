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
}
