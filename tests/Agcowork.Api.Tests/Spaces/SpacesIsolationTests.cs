using System.Net;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Tenancy;

namespace Agcowork.Api.Tests.Spaces;

/// <summary>Every space endpoint against the isolation fixture, plus foreign ids inside the own workspace.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SpacesIsolationTests(AgcoworkApiFactory factory)
{
    [Fact]
    public async Task ListSpaces_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Get, "/spaces", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateSpace_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Post, "/spaces", new { }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetSpace_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var space = await SchemaApi.CreateSpaceAsync(t.AlphaOwner.Client, t.Alpha.Slug, "Iso get");
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/spaces/{space.Id}", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateSpace_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var space = await SchemaApi.CreateSpaceAsync(t.AlphaOwner.Client, t.Alpha.Slug, "Iso update");
        await t.AssertIsolatedAsync(HttpMethod.Patch, $"/spaces/{space.Id}", new { name = "" }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ArchiveSpace_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var space = await SchemaApi.CreateSpaceAsync(t.AlphaOwner.Client, t.Alpha.Slug, "Iso archive");
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/spaces/{space.Id}/archive", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForeignSpaceId_InOwnWorkspace_NotFound()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var beta = await SchemaApi.CreateSpaceAsync(t.BetaOwner.Client, t.Beta.Slug, "Beta space");
        var path = TenancyFixture.Path(t.Alpha.Slug, $"/spaces/{beta.Id}");

        await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Get, path);
        await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Patch, path, new { name = "Taken over" });
        await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Post, $"{path}/archive");
        await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Get, TenancyFixture.Path(t.Alpha.Slug, $"/collections?spaceId={beta.Id}"));
        // The restricted key is a member of Beta, but its key only works in Alpha.
        await TenancyFixture.AssertNotFoundAsync(t.RestrictedToAlpha.Client, HttpMethod.Get, path);
    }
}
