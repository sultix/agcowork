using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Collections;
using Agcowork.Application.Records;
using Agcowork.Application.Spaces;

namespace Agcowork.Api.Tests.Records;

/// <summary>Every record endpoint against the isolation fixture, plus foreign record ids in the own workspace.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class RecordsIsolationTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TenancyFixture T, SpaceDto Space, CollectionDto Collection, RecordDto Record)> AlphaAsync()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (space, collection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var record = await RecordsApi.CreateInAsync(t.AlphaOwner.Client, t.Alpha.Slug, collection.Id, body: "## Log\n");
        return (t, space, collection, record);
    }

    [Fact]
    public async Task ListRecords_Isolated()
    {
        var (t, _, c, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/records?collectionId={c.Id}", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateRecord_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Post, "/records", new { }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateRecord_SkipTemplate_Isolated_ForeignCollectionNotFound_BetaUntouched()
    {
        var (t, _, alphaCollection, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Post, "/records", new { collectionId = alphaCollection.Id, title = "X", skipTemplate = true }, homeStatus: HttpStatusCode.Created);
        var (_, betaCollection) = await RecordsApi.SeedCollectionAsync(t.BetaOwner.Client, t.Beta.Slug);

        using var response = await t.AlphaOwner.Client.PostAsJsonAsync(
            TenancyFixture.Path(t.Alpha.Slug, "/records"), new { collectionId = betaCollection.Id, title = "Gekapert", skipTemplate = true }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("not_found", ProblemAssert.Field(problem, "collectionId").GetProperty("code").GetString());
        var betaRecords = await t.BetaOwner.Client.GetFromJsonAsync<Agcowork.Application.Common.Paging.Page<RecordDto>>(
            TenancyFixture.Path(t.Beta.Slug, $"/records?collectionId={betaCollection.Id}"), AgcoworkApiFactory.Json, Ct);
        Assert.Empty(betaRecords!.Items);
    }

    [Fact]
    public async Task GetRecord_Isolated()
    {
        var (t, _, _, r) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/records/{r.Id}", homeStatus: HttpStatusCode.OK);
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/records/{r.Id}?format=markdown", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateRecord_Isolated()
    {
        var (t, _, _, r) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Patch, $"/records/{r.Id}", new { title = "" }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AppendToRecord_Isolated()
    {
        var (t, _, _, r) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/records/{r.Id}/append", new { }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ArchiveRecord_Isolated()
    {
        var (t, _, _, r) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/records/{r.Id}/archive", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task RestoreRecord_Isolated()
    {
        var (t, _, _, r) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/records/{r.Id}/restore", homeStatus: HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task GetRecordHistory_Isolated()
    {
        var (t, _, _, r) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/records/{r.Id}/history", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetRevision_Isolated()
    {
        var (t, _, _, r) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/records/{r.Id}/revisions/1", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForeignRecordIds_InOwnWorkspace_NotFound_BetaUntouched()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (betaSpace, betaCollection) = await RecordsApi.SeedCollectionAsync(t.BetaOwner.Client, t.Beta.Slug);
        var beta = await RecordsApi.CreateInAsync(t.BetaOwner.Client, t.Beta.Slug, betaCollection.Id, "Beta", "Geheim");
        var client = t.AlphaOwner.Client;
        string Alpha(string relative) => TenancyFixture.Path(t.Alpha.Slug, relative);

        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/records/{beta.Id}"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/records/{beta.Id}?format=markdown"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Patch, Alpha($"/records/{beta.Id}"), new { title = "Gekapert" });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, Alpha($"/records/{beta.Id}/append"), new { markdown = "x" });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, Alpha($"/records/{beta.Id}/archive"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, Alpha($"/records/{beta.Id}/restore"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/records/{beta.Id}/history"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/records/{beta.Id}/revisions/1"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/records?collectionId={betaCollection.Id}"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/records?spaceId={betaSpace.Id}"));

        // Writing into Beta's collection, space or below Beta's record is invalid input, not a way in.
        foreach (var (field, body) in new (string, object)[]
                 {
                     ("collectionId", new { collectionId = betaCollection.Id, title = "T" }),
                     ("spaceId", new { spaceId = betaSpace.Id, title = "T" }),
                 })
        {
            using var response = await client.PostAsJsonAsync(Alpha("/records"), body, Ct);
            var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Equal("not_found", ProblemAssert.Field(problem, field).GetProperty("code").GetString());
        }

        var (alphaSpace, _) = await RecordsApi.SeedCollectionAsync(client, t.Alpha.Slug);
        using (var nested = await client.PostAsJsonAsync(Alpha("/records"), new { spaceId = alphaSpace.Id, title = "T", parentRecordId = beta.Id }, Ct))
        {
            var problem = await ProblemAssert.IsProblemAsync(nested, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Equal("not_found", ProblemAssert.Field(problem, "parentRecordId").GetProperty("code").GetString());
        }

        var stillThere = await RecordsApi.GetAsync(t.BetaOwner.Client, t.Beta.Slug, beta.Id);
        Assert.Equal(("Beta", "Geheim", 1, (DateTimeOffset?)null), (stillThere.Title, stillThere.Body, stillThere.Revision, stillThere.ArchivedAt));
    }

    [Fact]
    public async Task Relation_ToForeignRecord_NotFound()
    {
        var (t, space, collection, _) = await AlphaAsync();
        var (_, betaCollection) = await RecordsApi.SeedCollectionAsync(t.BetaOwner.Client, t.Beta.Slug);
        var beta = await RecordsApi.CreateInAsync(t.BetaOwner.Client, t.Beta.Slug, betaCollection.Id);
        var linking = await Collections.SchemaApi.CreateCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug, space.Id, $"Links {Guid.NewGuid():N}",
            [new { key = "ref", name = "Ref", type = "relation", targetCollectionId = collection.Id }]);

        using var response = await t.AlphaOwner.Client.PostAsJsonAsync(
            TenancyFixture.Path(t.Alpha.Slug, "/records"), new { collectionId = linking.Id, title = "T", properties = new { @ref = beta.Id } }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("not_found", ProblemAssert.Field(problem, "properties.ref").GetProperty("code").GetString());
    }
}
