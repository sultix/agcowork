using System.Net;
using System.Net.Http.Json;
using Agcowork.Application.Workspaces;

namespace Agcowork.Api.Tests.Tenancy;

/// <summary>
/// Every workspace endpoint of AP-005 against the isolation fixture. New endpoints add one line here
/// (or in their own module's tests) via <see cref="TenancyFixture.AssertIsolatedAsync"/>.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class IsolationTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetWorkspace_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Get, "", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateWorkspace_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Patch, "", new { name = "" }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ArchiveWorkspace_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Post, "/archive");

        // Positive control on an own workspace: archiving Alpha would break the shared fixture.
        var own = await TenancyFixture.CreateWorkspaceAsync(t.AlphaOwner.Client, "iso");
        using var response = await TenancyFixture.SendAsync(t.AlphaOwner.Client, HttpMethod.Post, TenancyFixture.Path(own.Slug, "/archive"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListMembers_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Get, "/members", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task AddMember_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Post, "/members", new { }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateMember_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        // In Alpha this would demote the sole owner: 409, nothing changes.
        await t.AssertIsolatedAsync(HttpMethod.Patch, $"/members/{t.AlphaOwner.ActorId}", new { role = "viewer" }, homeStatus: HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RemoveMember_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Delete, $"/members/{t.AlphaOwner.ActorId}", homeStatus: HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task GetWorkspace_BetaWithAlphaKey_NotFound()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Get, TenancyFixture.Path(t.Beta.Slug, ""));
    }

    [Fact]
    public async Task ListMembers_BetaWithAlphaKey_NotFound()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Get, TenancyFixture.Path(t.Beta.Slug, "/members"));
    }

    [Fact]
    public async Task RestrictedKey_HomeWorkspaceWorks_OtherNotFound()
    {
        var t = await TenancyFixture.GetAsync(factory);

        using var home = await t.RestrictedToAlpha.Client.GetAsync(new Uri(TenancyFixture.Path(t.Alpha.Slug, ""), UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        await TenancyFixture.AssertNotFoundAsync(t.RestrictedToAlpha.Client, HttpMethod.Get, TenancyFixture.Path(t.Beta.Slug, ""));
    }

    [Fact]
    public async Task HumanSession_HomeWorkspaceWorks_OtherNotFound()
    {
        var t = await TenancyFixture.GetAsync(factory);

        using var home = await t.AlphaHuman.Client.GetAsync(new Uri(TenancyFixture.Path(t.Alpha.Slug, "/members"), UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        await TenancyFixture.AssertNotFoundAsync(t.AlphaHuman.Client, HttpMethod.Get, TenancyFixture.Path(t.Beta.Slug, ""));
    }

    [Fact]
    public async Task NonMember_NotFound()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await TenancyFixture.AssertNotFoundAsync(t.Outsider.Client, HttpMethod.Get, TenancyFixture.Path(t.Alpha.Slug, ""));
        await TenancyFixture.AssertNotFoundAsync(t.Outsider.Client, HttpMethod.Get, TenancyFixture.Path(t.Beta.Slug, "/members"));
    }

    [Fact]
    public async Task ForeignMemberId_InOwnWorkspace_NotFound()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var path = TenancyFixture.Path(t.Alpha.Slug, $"/members/{t.BetaOwner.ActorId}");

        await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Patch, path, new { role = "viewer" });
        await TenancyFixture.AssertNotFoundAsync(t.AlphaOwner.Client, HttpMethod.Delete, path);
    }

    [Fact]
    public async Task ListMembers_ShowsOnlyOwnWorkspace()
    {
        var t = await TenancyFixture.GetAsync(factory);

        var members = await t.AlphaOwner.Client.GetFromJsonAsync<List<MemberDto>>(TenancyFixture.Path(t.Alpha.Slug, "/members"), AgcoworkApiFactory.Json, Ct);

        Assert.Equal(
            new HashSet<Guid> { t.AlphaOwner.ActorId, t.RestrictedToAlpha.ActorId, t.AlphaHuman.ActorId },
            members!.Select(m => m.ActorId).ToHashSet());
    }

    [Fact]
    public async Task ListMyWorkspaces_RestrictedKey_SeesOnlyItsWorkspace()
    {
        var t = await TenancyFixture.GetAsync(factory);

        var workspaces = await t.RestrictedToAlpha.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/v1/workspaces", AgcoworkApiFactory.Json, Ct);

        Assert.Equal([t.Alpha.Id], workspaces!.Select(w => w.Id));
    }

    [Fact]
    public async Task ListMyWorkspaces_Outsider_Empty()
    {
        var t = await TenancyFixture.GetAsync(factory);

        var workspaces = await t.Outsider.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/v1/workspaces", AgcoworkApiFactory.Json, Ct);

        Assert.Empty(workspaces!);
    }

    [Fact]
    public async Task WorkspaceEndpoint_Anonymous_UnauthorizedBeforeTenancy()
    {
        var t = await TenancyFixture.GetAsync(factory);
        using var anonymous = factory.CreateClientWithoutCookies();

        using var response = await anonymous.GetAsync(new Uri(TenancyFixture.Path(t.Alpha.Slug, ""), UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
