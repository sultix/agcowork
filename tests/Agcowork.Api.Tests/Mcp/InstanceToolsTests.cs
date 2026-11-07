using System.Net;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Identity.Actors;
using Agcowork.Application.Workspaces;
using Agcowork.Application.Workspaces.Templates;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>Instance tools on <c>/mcp</c>: me, workspaces, templates, workspace creation (own DI scope) and members.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class InstanceToolsTests(AgcoworkApiFactory factory)
{
    private static string NewSlug(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    [Fact]
    public async Task Me_ApiKey_ActorScopesAndKey()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, slug: null);

        var me = await McpClientFixture.OkAsync<MeDto>(client, "me");

        Assert.Equal(t.AlphaOwner.ActorId, me.Id);
        Assert.Contains("workspaces:create", me.InstanceScopes);
        Assert.Equal(t.AlphaOwner.ApiKeyId, me.ApiKey?.Id);
    }

    [Fact]
    public async Task Me_RestrictedKey_ShowsRestrictionAndNoInstanceScopes()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.RestrictedToAlpha.Key, slug: null);

        var me = await McpClientFixture.OkAsync<MeDto>(client, "me");

        Assert.Equal(t.Alpha.Id, me.ApiKey?.WorkspaceId);
        Assert.Empty(me.InstanceScopes);
    }

    [Fact]
    public async Task WorkspacesList_RestrictedKey_OnlyItsWorkspace()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var owner = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, slug: null);
        await using var restricted = await McpClientFixture.ConnectAsync(factory, t.RestrictedToAlpha.Key, slug: null);

        var mine = await McpClientFixture.OkAsync<List<WorkspaceDto>>(owner, "workspaces_list");
        Assert.Contains(mine, w => w.Id == t.Alpha.Id && w.Role == Domain.Workspaces.WorkspaceRole.Owner);
        Assert.DoesNotContain(mine, w => w.Id == t.Beta.Id);

        var limited = await McpClientFixture.OkAsync<List<WorkspaceDto>>(restricted, "workspaces_list");
        Assert.Equal([t.Alpha.Id], limited.Select(w => w.Id));
    }

    [Fact]
    public async Task TemplatesList_ContainsDefaultSoftwareProject()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.Outsider.Key, slug: null);

        var templates = await McpClientFixture.OkAsync<List<WorkspaceTemplateDto>>(client, "templates_list");

        Assert.Contains(templates, x => x.Id == "software-projekt" && x.IsDefault);
        Assert.Contains(templates, x => x.Id == "empty" && !x.IsDefault);
    }

    [Fact]
    public async Task WorkspaceCreate_TwiceInOneSession_BothWithStandardTemplate()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, slug: null);

        var first = await McpClientFixture.OkAsync<WorkspaceDto>(client, "workspace_create", new { slug = NewSlug("mcpa"), name = "Erster" });
        var second = await McpClientFixture.OkAsync<WorkspaceDto>(client, "workspace_create", new { slug = NewSlug("mcpb"), name = "Zweiter", templateId = "empty" });

        Assert.Equal(Domain.Workspaces.WorkspaceRole.Owner, first.Role);
        Assert.NotEqual(first.Id, second.Id);

        await using var inFirst = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, first.Slug);
        var overview = await McpClientFixture.OkAsync<WorkspaceOverviewDto>(inFirst, "workspace_overview");
        Assert.Contains(overview.Spaces.SelectMany(s => s.Collections), c => c.Name == "Entscheidungen");

        await using var inSecond = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, second.Slug);
        Assert.Empty((await McpClientFixture.OkAsync<WorkspaceOverviewDto>(inSecond, "workspace_overview")).Spaces);
    }

    [Fact]
    public async Task WorkspaceCreate_TakenSlug_Conflict()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, slug: null);

        var problem = McpClientFixture.Error(
            await McpClientFixture.CallAsync(client, "workspace_create", new { slug = t.Beta.Slug, name = "Doppelt", templateId = "empty" }), 409, "slug_taken");
        Assert.Equal("slug", problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task WorkspaceCreate_WithoutInstanceScope_Forbidden()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.Outsider.Key, slug: null);

        var problem = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "workspace_create", new { slug = NewSlug("nope"), name = "Nein" }), 403, "forbidden");
        Assert.Contains("workspaces:create", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceAddMember_OwnWorkspace_AddsMember_ForeignWorkspaceNotFound()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(t.AlphaOwner.Client, "mcpm");
        var newcomer = await factory.CreateAgentAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, slug: null);

        var member = await McpClientFixture.OkAsync<MemberDto>(client, "workspace_add_member", new { workspaceId = workspace.Id, actorId = newcomer.ActorId, role = "viewer" });
        Assert.Equal(newcomer.ActorId, member.ActorId);
        Assert.Equal(["read"], member.Scopes);

        McpClientFixture.Error(
            await McpClientFixture.CallAsync(client, "workspace_add_member", new { workspaceId = workspace.Id, actorId = newcomer.ActorId, role = "viewer" }), 409, "already_member");
        McpClientFixture.Error(
            await McpClientFixture.CallAsync(client, "workspace_add_member", new { workspaceId = workspace.Id, actorId = newcomer.ActorId, role = "boss" }), 400, "validation_failed");

        // Beta exists but Alpha's owner is no member: indistinguishable from an unknown id.
        var foreign = McpClientFixture.Error(
            await McpClientFixture.CallAsync(client, "workspace_add_member", new { workspaceId = t.Beta.Id, actorId = newcomer.ActorId, role = "viewer" }), 404, "not_found");
        Assert.False(foreign.TryGetProperty("detail", out _));
        McpClientFixture.Error(
            await McpClientFixture.CallAsync(client, "workspace_add_member", new { workspaceId = Guid.NewGuid(), actorId = newcomer.ActorId, role = "viewer" }), 404, "not_found");

        // The newcomer can read now, through its own workspace endpoint.
        await using var asNewcomer = await McpClientFixture.ConnectAsync(factory, newcomer.Key, workspace.Slug);
        Assert.Equal(workspace.Id, (await McpClientFixture.OkAsync<WorkspaceOverviewDto>(asNewcomer, "workspace_overview")).Workspace.Id);
        newcomer.Dispose();
    }

    [Fact]
    public async Task WorkspaceAddMember_RestrictedKeyForOtherWorkspace_NotFound()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.RestrictedToAlpha.Key, slug: null);

        McpClientFixture.Error(
            await McpClientFixture.CallAsync(client, "workspace_add_member", new { workspaceId = t.Beta.Id, actorId = t.Outsider.ActorId, role = "viewer" }), 404, "not_found");
    }

    [Fact]
    public async Task InstanceEndpoint_CookieOrNoCredentials_Unauthorized()
    {
        var t = await TenancyFixture.GetAsync(factory);

        using (var cookie = await McpClientFixture.PostRawAsync(t.AlphaHuman.Client, McpClientFixture.InstancePath, "tools/list"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, cookie.StatusCode);
        }

        using var anonymous = factory.CreateClientWithoutCookies();
        using var response = await McpClientFixture.PostRawAsync(anonymous, McpClientFixture.InstancePath, "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
