using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Identity;
using Agcowork.Application.Workspaces;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Api.Tests.Tenancy;

[Collection(ApiTestGroup.Name)]
public sealed class WorkspacesEndpointsTests(AgcoworkApiFactory factory)
{
    private static readonly string[] ReadOnly = ["read"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_AsInstanceAdmin_CreatedAndListedAsOwner()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var slug = UniqueSlug("adm");

        using var response = await admin.PostAsJsonAsync("/api/v1/workspaces", new { slug, name = "Admin space", description = "For tests", templateId = "empty" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/v1/w/{slug}", response.Headers.Location?.OriginalString);
        var created = (await response.Content.ReadFromJsonAsync<WorkspaceDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal(WorkspaceRole.Owner, created.Role);
        Assert.Equal("For tests", created.Description);

        var mine = await admin.GetFromJsonAsync<List<WorkspaceDto>>("/api/v1/workspaces", AgcoworkApiFactory.Json, Ct);
        var listed = Assert.Single(mine!, w => w.Slug == slug);
        Assert.Equal(WorkspaceRole.Owner, listed.Role);
        Assert.Equal(["admin", "read", "schema", "write"], listed.Scopes);
    }

    [Fact]
    public async Task Create_AsAgentWithWorkspacesCreate_AgentIsOwner()
    {
        var agent = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);

        var created = await TenancyFixture.CreateWorkspaceAsync(agent.Client, "agt");

        Assert.Equal(agent.ActorId, created.CreatedBy);
        var fetched = await agent.Client.GetFromJsonAsync<WorkspaceDto>(TenancyFixture.Path(created.Slug, ""), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(WorkspaceRole.Owner, fetched!.Role);
        var members = await agent.Client.GetFromJsonAsync<List<MemberDto>>(TenancyFixture.Path(created.Slug, "/members"), AgcoworkApiFactory.Json, Ct);
        var owner = Assert.Single(members!);
        Assert.Equal(agent.ActorId, owner.ActorId);
        Assert.Equal(WorkspaceRole.Owner, owner.Role);
    }

    [Fact]
    public async Task Create_AsAgentWithoutRight_Forbidden()
    {
        using var agent = await factory.CreateAgentClientAsync();

        using var response = await agent.PostAsJsonAsync("/api/v1/workspaces", new { slug = UniqueSlug("no"), name = "Nope" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires workspaces:create", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Create_UnknownTemplate_ValidationErrorWithAllowed()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.PostAsJsonAsync("/api/v1/workspaces", new { slug = UniqueSlug("tpl"), name = "T", templateId = "software-projekt" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        var field = ProblemAssert.Field(problem, "templateId");
        Assert.Equal("not_in_options", field.GetProperty("code").GetString());
        Assert.Equal(["empty"], field.GetProperty("allowed").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task Create_InvalidSlug_ValidationError()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);

        using var response = await admin.PostAsJsonAsync("/api/v1/workspaces", new { slug = "Not Valid", name = "X" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("invalid_format", ProblemAssert.Field(problem, "slug").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Create_SlugTaken_Conflict()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var existing = await TenancyFixture.CreateWorkspaceAsync(admin, "dup");

        using var response = await admin.PostAsJsonAsync("/api/v1/workspaces", new { slug = existing.Slug, name = "Again" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "slug_taken");
        Assert.Equal("slug", ProblemAssert.Field(problem, "slug").GetProperty("field").GetString());
    }

    [Fact]
    public async Task Get_Viewer_OkWithViewerScopes()
    {
        var (workspace, _, viewer) = await WorkspaceWithMemberAsync("viewer");

        var fetched = await viewer.Client.GetFromJsonAsync<WorkspaceDto>(TenancyFixture.Path(workspace.Slug, ""), AgcoworkApiFactory.Json, Ct);

        Assert.Equal(WorkspaceRole.Viewer, fetched!.Role);
        Assert.Equal(["read"], fetched.Scopes);
    }

    [Fact]
    public async Task Patch_Viewer_ForbiddenWithProblemDetails()
    {
        var (workspace, _, viewer) = await WorkspaceWithMemberAsync("viewer");

        using var response = await viewer.Client.PatchAsJsonAsync(TenancyFixture.Path(workspace.Slug, ""), new { name = "Mine now" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires admin", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Patch_Owner_UpdatesNameAndClearsDescription()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "upd");

        using var response = await owner.Client.PatchAsJsonAsync(TenancyFixture.Path(workspace.Slug, ""), new { name = "Renamed", description = "" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<WorkspaceDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal("Renamed", updated.Name);
        Assert.Null(updated.Description);
        Assert.Equal(workspace.Slug, updated.Slug);
    }

    [Fact]
    public async Task Patch_OwnerWithReadOnlyKey_Forbidden()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "ro");
        using var readOnly = await TenancyFixture.CreateKeyClientAsync(factory, owner.ActorId, new { scopes = ReadOnly });

        using var response = await readOnly.PatchAsJsonAsync(TenancyFixture.Path(workspace.Slug, ""), new { name = "X" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires admin", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Archive_Owner_ReadsWorkWritesConflict()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "arc");
        var other = await factory.CreateAgentAsync(cancellationToken: Ct);
        var member = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, member.ActorId, "editor");

        using var archive = await owner.Client.PostAsync(new Uri(TenancyFixture.Path(workspace.Slug, "/archive"), UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        var archived = (await archive.Content.ReadFromJsonAsync<WorkspaceDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.NotNull(archived.ArchivedAt);

        using var get = await owner.Client.GetAsync(new Uri(TenancyFixture.Path(workspace.Slug, ""), UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var members = await owner.Client.GetAsync(new Uri(TenancyFixture.Path(workspace.Slug, "/members"), UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, members.StatusCode);

        using var patch = await owner.Client.PatchAsJsonAsync(TenancyFixture.Path(workspace.Slug, ""), new { name = "X" }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(patch, HttpStatusCode.Conflict, "workspace_archived");
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));

        using var add = await owner.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/members"), new { actorId = other.ActorId, role = "viewer" }, Ct);
        await ProblemAssert.IsProblemAsync(add, HttpStatusCode.Conflict, "workspace_archived");

        // 409 comes from each writing use case (RequireWritable), not from the HTTP verb: one check per endpoint.
        var memberPath = TenancyFixture.Path(workspace.Slug, $"/members/{member.ActorId}");
        using var changeMember = await owner.Client.PatchAsJsonAsync(memberPath, new { role = "viewer" }, Ct);
        await ProblemAssert.IsProblemAsync(changeMember, HttpStatusCode.Conflict, "workspace_archived");

        using var removeMember = await owner.Client.DeleteAsync(new Uri(memberPath, UriKind.Relative), Ct);
        await ProblemAssert.IsProblemAsync(removeMember, HttpStatusCode.Conflict, "workspace_archived");

        using var again = await owner.Client.PostAsync(new Uri(TenancyFixture.Path(workspace.Slug, "/archive"), UriKind.Relative), null, Ct);
        await ProblemAssert.IsProblemAsync(again, HttpStatusCode.Conflict, "workspace_archived");

        var mine = await owner.Client.GetFromJsonAsync<List<WorkspaceDto>>("/api/v1/workspaces", AgcoworkApiFactory.Json, Ct);
        Assert.NotNull(Assert.Single(mine!, w => w.Id == workspace.Id).ArchivedAt);
    }

    [Fact]
    public async Task Archive_Admin_ForbiddenOwnerOnly()
    {
        var (workspace, _, admin) = await WorkspaceWithMemberAsync("admin");

        using var response = await admin.Client.PostAsync(new Uri(TenancyFixture.Path(workspace.Slug, "/archive"), UriKind.Relative), null, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires role owner", problem.GetProperty("detail").GetString());
    }

    private async Task<(WorkspaceDto Workspace, TestAgent Owner, TestAgent Member)> WorkspaceWithMemberAsync(string role)
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, role);
        var member = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, member.ActorId, role);
        return (workspace, owner, member);
    }

    private static string UniqueSlug(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];
}
