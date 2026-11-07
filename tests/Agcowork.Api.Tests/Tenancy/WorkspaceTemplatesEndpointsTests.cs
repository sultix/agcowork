using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Identity;
using Agcowork.Application.Collections;
using Agcowork.Application.Spaces;
using Agcowork.Application.Workspaces;
using Agcowork.Application.Workspaces.Templates;

namespace Agcowork.Api.Tests.Tenancy;

[Collection(ApiTestGroup.Name)]
public sealed class WorkspaceTemplatesEndpointsTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_WithoutTemplateId_AppliesSoftwareProject()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var slug = Slug("def");

        using var response = await admin.PostAsJsonAsync("/api/v1/workspaces", new { slug, name = "Default" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var spaces = await admin.GetFromJsonAsync<List<SpaceDto>>(TenancyFixture.Path(slug, "/spaces"), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(["Produkt", "Technik", "Notizen"], spaces!.Select(s => s.Name));
        var collections = await admin.GetFromJsonAsync<List<CollectionDto>>(TenancyFixture.Path(slug, "/collections"), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(["Analysen", "Aufgaben", "Entscheidungen", "Komponenten"], collections!.Select(c => c.Name).Order());

        foreach (var collection in collections!)
        {
            var description = await admin.GetFromJsonAsync<CollectionDescriptionDto>(
                TenancyFixture.Path(slug, $"/collections/{collection.Id}/describe"), AgcoworkApiFactory.Json, Ct);
            Assert.Equal("object", description!.JsonSchema["type"]?.GetValue<string>());
            Assert.Single(description.Templates);
            Assert.False(string.IsNullOrWhiteSpace(description.Collection.Description));
        }
    }

    [Fact]
    public async Task Create_EmptyTemplate_CreatesEmptyWorkspace()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var slug = Slug("emp");

        using var response = await admin.PostAsJsonAsync("/api/v1/workspaces", new { slug, name = "Empty", templateId = "empty" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty((await admin.GetFromJsonAsync<List<SpaceDto>>(TenancyFixture.Path(slug, "/spaces"), AgcoworkApiFactory.Json, Ct))!);
        Assert.Empty((await admin.GetFromJsonAsync<List<CollectionDto>>(TenancyFixture.Path(slug, "/collections"), AgcoworkApiFactory.Json, Ct))!);
    }

    [Fact]
    public async Task Create_UnknownTemplate_BadRequestWithAllowedAndNothingCreated()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var slug = Slug("unk");

        using var response = await admin.PostAsJsonAsync("/api/v1/workspaces", new { slug, name = "Unknown", templateId = "nope" }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        var field = ProblemAssert.Field(problem, "templateId");
        Assert.Equal(["software-projekt", "empty"], field.GetProperty("allowed").EnumerateArray().Select(v => v.GetString()));
        var mine = await admin.GetFromJsonAsync<List<WorkspaceDto>>("/api/v1/workspaces", AgcoworkApiFactory.Json, Ct);
        Assert.DoesNotContain(mine!, w => w.Slug == slug);
    }

    [Fact]
    public async Task List_AsAuthenticatedCaller_ListsBothTemplatesWithDefault()
    {
        using var agent = await factory.CreateAgentClientAsync();

        var templates = await agent.GetFromJsonAsync<List<WorkspaceTemplateDto>>("/api/v1/workspace-templates", AgcoworkApiFactory.Json, Ct);

        Assert.Equal(["software-projekt", "empty"], templates!.Select(t => t.Id));
        Assert.Equal([true, false], templates!.Select(t => t.IsDefault));
        Assert.All(templates!, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Fact]
    public async Task List_Anonymous_Unauthorized()
    {
        using var anonymous = factory.CreateClientWithoutCookies();

        using var response = await anonymous.GetAsync("/api/v1/workspace-templates", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_TwoWorkspacesInARow_StructuresAreSeparated()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var first = Slug("one");
        var second = Slug("two");
        using var r1 = await admin.PostAsJsonAsync("/api/v1/workspaces", new { slug = first, name = "One" }, Ct);
        using var r2 = await admin.PostAsJsonAsync("/api/v1/workspaces", new { slug = second, name = "Two" }, Ct);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);

        var c1 = await admin.GetFromJsonAsync<List<CollectionDto>>(TenancyFixture.Path(first, "/collections"), AgcoworkApiFactory.Json, Ct);
        var c2 = await admin.GetFromJsonAsync<List<CollectionDto>>(TenancyFixture.Path(second, "/collections"), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(4, c1!.Count);
        Assert.Equal(4, c2!.Count);
        Assert.Empty(c1.Select(c => c.Id).Intersect(c2.Select(c => c.Id)));

        // A collection of the first workspace is invisible through the second one.
        using var foreign = await admin.GetAsync(TenancyFixture.Path(second, $"/collections/{c1[0].Id}"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..16];
}
