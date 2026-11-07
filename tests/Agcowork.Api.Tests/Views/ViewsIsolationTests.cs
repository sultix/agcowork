using System.Net;
using Agcowork.Api.Tests.Tenancy;

namespace Agcowork.Api.Tests.Views;

/// <summary>Every view endpoint against the isolation fixture, plus foreign ids inside the own workspace.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ViewsIsolationTests(AgcoworkApiFactory factory)
{
    private async Task<(TenancyFixture T, Guid CollectionId, Guid ViewId)> AlphaAsync()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (collectionId, board) = await ViewsApi.SeedAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        return (t, collectionId, board.Id);
    }

    [Fact]
    public async Task ListViews_Isolated()
    {
        var (t, c, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/collections/{c}/views", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateView_Isolated()
    {
        var (t, c, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/collections/{c}/views", new { }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetView_Isolated()
    {
        var (t, _, v) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/views/{v}", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateView_Isolated()
    {
        var (t, _, v) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Patch, $"/views/{v}", new { name = "" }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteView_Isolated()
    {
        var (t, _, v) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Delete, $"/views/{v}", homeStatus: HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SetDefaultView_Isolated()
    {
        var (t, _, v) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/views/{v}/default", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForeignIds_InOwnWorkspace_NotFound_AndBetaIsUntouched()
    {
        var (t, alphaCollection, _) = await AlphaAsync();
        var (betaCollection, betaBoard) = await ViewsApi.SeedAsync(t.BetaOwner.Client, t.Beta.Slug);
        var client = t.AlphaOwner.Client;
        string Alpha(string relative) => TenancyFixture.Path(t.Alpha.Slug, relative);

        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/collections/{betaCollection}/views"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, Alpha($"/collections/{betaCollection}/views"), new { name = "Mine", type = "list" });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/views/{betaBoard.Id}"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Patch, Alpha($"/views/{betaBoard.Id}"), new { name = "Mine" });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Delete, Alpha($"/views/{betaBoard.Id}"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, Alpha($"/views/{betaBoard.Id}/default"));

        // Beta's data is untouched, and Alpha's own listing never shows Beta's views.
        var betaViews = await ViewsApi.ListAsync(t.BetaOwner.Client, t.Beta.Slug, betaCollection);
        Assert.Equal(3, betaViews.Count);
        Assert.Equal("Board", betaViews.Single(v => v.Id == betaBoard.Id).Name);
        Assert.DoesNotContain(await ViewsApi.ListAsync(client, t.Alpha.Slug, alphaCollection), v => v.CollectionId == betaCollection);
    }
}
