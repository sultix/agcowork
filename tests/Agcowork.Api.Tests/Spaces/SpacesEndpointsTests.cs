using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Spaces;

namespace Agcowork.Api.Tests.Spaces;

[Collection(ApiTestGroup.Name)]
public sealed class SpacesEndpointsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestAgent Owner, Application.Workspaces.WorkspaceDto Workspace)> OwnWorkspaceAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        return (owner, await TenancyFixture.CreateWorkspaceAsync(owner.Client, "spaces"));
    }

    [Fact]
    public async Task SpaceLifecycle_CreateGetListUpdateArchive()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var path = TenancyFixture.Path(workspace.Slug, "/spaces");

        using var create = await owner.Client.PostAsJsonAsync(path, new { name = " Produkt ", description = "Product work.", icon = "📦" }, Ct);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var space = (await create.Content.ReadFromJsonAsync<SpaceDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal($"{path}/{space.Id}", create.Headers.Location?.OriginalString);
        Assert.Equal(("Produkt", 0, owner.ActorId), (space.Name, space.SortOrder, space.CreatedBy));

        var fetched = await owner.Client.GetFromJsonAsync<SpaceDto>($"{path}/{space.Id}", AgcoworkApiFactory.Json, Ct);
        Assert.Equal(space, fetched);

        using var update = await owner.Client.PatchAsJsonAsync($"{path}/{space.Id}", new { name = "Product", icon = "", sortOrder = 5 }, Ct);
        var updated = (await update.Content.ReadFromJsonAsync<SpaceDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal(("Product", "Product work.", null, 5), (updated.Name, updated.Description, updated.Icon, updated.SortOrder));

        using var archive = await owner.Client.PostAsync(new Uri($"{path}/{space.Id}/archive", UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Empty((await owner.Client.GetFromJsonAsync<List<SpaceDto>>(path, AgcoworkApiFactory.Json, Ct))!);
        Assert.Single((await owner.Client.GetFromJsonAsync<List<SpaceDto>>($"{path}?includeArchived=true", AgcoworkApiFactory.Json, Ct))!);

        using var again = await owner.Client.PatchAsJsonAsync($"{path}/{space.Id}", new { name = "X" }, Ct);
        await ProblemAssert.IsProblemAsync(again, HttpStatusCode.Conflict, "space_archived");
    }

    [Fact]
    public async Task CreateSpace_Invalid_ValidationErrors()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();

        using var response = await owner.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/spaces"), new { name = "", icon = new string('x', 65) }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("required", ProblemAssert.Field(problem, "name").GetProperty("code").GetString());
        Assert.Equal("too_long", ProblemAssert.Field(problem, "icon").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Viewer_CannotCreate_EditorCannotArchive()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var viewer = await factory.CreateAgentAsync(cancellationToken: Ct);
        var editor = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, viewer.ActorId, "viewer");
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, editor.ActorId, "editor");

        using var create = await viewer.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/spaces"), new { name = "X" }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(create, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires write", problem.GetProperty("detail").GetString());

        using var created = await editor.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/spaces"), new { name = "X" }, Ct);
        var space = (await created.Content.ReadFromJsonAsync<SpaceDto>(AgcoworkApiFactory.Json, Ct))!;
        using var archive = await editor.Client.PostAsync(new Uri(TenancyFixture.Path(workspace.Slug, $"/spaces/{space.Id}/archive"), UriKind.Relative), null, Ct);
        problem = await ProblemAssert.IsProblemAsync(archive, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal("requires admin", problem.GetProperty("detail").GetString());

        Assert.Single((await viewer.Client.GetFromJsonAsync<List<SpaceDto>>(TenancyFixture.Path(workspace.Slug, "/spaces"), AgcoworkApiFactory.Json, Ct))!);
    }

    [Fact]
    public async Task ArchivedWorkspace_WritesConflict_ReadsWork()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var space = await Collections.SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug);
        using (var archive = await owner.Client.PostAsync(new Uri(TenancyFixture.Path(workspace.Slug, "/archive"), UriKind.Relative), null, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        }

        using var create = await owner.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/spaces"), new { name = "X" }, Ct);
        using var collection = await owner.Client.PostAsJsonAsync(TenancyFixture.Path(workspace.Slug, "/collections"), new { spaceId = space.Id, name = "X", description = "D." }, Ct);

        await ProblemAssert.IsProblemAsync(create, HttpStatusCode.Conflict, "workspace_archived");
        await ProblemAssert.IsProblemAsync(collection, HttpStatusCode.Conflict, "workspace_archived");
        Assert.Single((await owner.Client.GetFromJsonAsync<List<SpaceDto>>(TenancyFixture.Path(workspace.Slug, "/spaces"), AgcoworkApiFactory.Json, Ct))!);
    }
}
