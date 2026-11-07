using System.Net;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Api.Tests.Views;

namespace Agcowork.Api.Tests.Records;

/// <summary>Both query endpoints against the isolation fixture, plus foreign ids and foreign property keys in the own workspace.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class QueryIsolationTests(AgcoworkApiFactory factory)
{
    [Fact]
    public async Task QueryRecords_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, collection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        await RecordsApi.CreateInAsync(t.AlphaOwner.Client, t.Alpha.Slug, collection.Id);

        await t.AssertIsolatedAsync(HttpMethod.Post, $"/collections/{collection.Id}/query", new { }, homeStatus: HttpStatusCode.OK);
        // Invalid input does not reveal existence either: 404 wins over 400.
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/collections/{collection.Id}/query", new { limit = 0 }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QueryView_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, board) = await ViewsApi.SeedAsync(t.AlphaOwner.Client, t.Alpha.Slug);

        await t.AssertIsolatedAsync(HttpMethod.Post, $"/views/{board.Id}/query", homeStatus: HttpStatusCode.OK);
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/views/{board.Id}/query", new { limit = 0 }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ForeignIdsAndKeys_InOwnWorkspace_NotFoundOr400_NoBetaData()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var betaSpace = await Collections.SchemaApi.CreateSpaceAsync(t.BetaOwner.Client, t.Beta.Slug, $"Space {Guid.NewGuid():N}");
        var betaCollection = await Collections.SchemaApi.CreateCollectionAsync(t.BetaOwner.Client, t.Beta.Slug, betaSpace.Id, $"Geheim {Guid.NewGuid():N}",
            [new { key = "secret", name = "Secret", type = "text" }]);
        await RecordsApi.CreateAsync(t.BetaOwner.Client, t.Beta.Slug, new { collectionId = betaCollection.Id, title = "Beta", properties = new { secret = "x" } });
        var (_, betaBoard) = await ViewsApi.SeedAsync(t.BetaOwner.Client, t.Beta.Slug);
        var (_, alphaCollection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var client = t.AlphaOwner.Client;

        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, TenancyFixture.Path(t.Alpha.Slug, $"/collections/{betaCollection.Id}/query"), new { });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, TenancyFixture.Path(t.Alpha.Slug, $"/views/{betaBoard.Id}/query"), new { });

        // Beta's property key in Alpha's collection: unknown, and 'allowed' lists only Alpha's keys.
        using var response = await TenancyFixture.SendAsync(
            client, HttpMethod.Post, TenancyFixture.Path(t.Alpha.Slug, $"/collections/{alphaCollection.Id}/query"), new { filter = new { prop = "secret", op = "eq", value = "x" } });
        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        var error = ProblemAssert.Field(problem, "filter.prop");
        Assert.Equal("unknown_property", error.GetProperty("code").GetString());
        Assert.DoesNotContain("secret", error.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
    }
}
