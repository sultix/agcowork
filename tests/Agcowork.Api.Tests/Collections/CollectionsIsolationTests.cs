using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Collections;

namespace Agcowork.Api.Tests.Collections;

/// <summary>Every collection, schema and template endpoint against the isolation fixture, plus foreign ids.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CollectionsIsolationTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TenancyFixture T, CollectionDto Collection, Guid TemplateId)> AlphaAsync()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, collection, template) = await SchemaApi.SeedAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        return (t, collection, template.Id);
    }

    [Fact]
    public async Task ListCollections_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Get, "/collections", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateCollection_Isolated()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await t.AssertIsolatedAsync(HttpMethod.Post, "/collections", new { }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetCollection_Isolated()
    {
        var (t, c, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/collections/{c.Id}", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateCollection_Isolated()
    {
        var (t, c, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Patch, $"/collections/{c.Id}", new { name = "" }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetSchema_Isolated()
    {
        var (t, c, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/collections/{c.Id}/schema", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateSchema_Isolated()
    {
        var (t, c, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Patch, $"/collections/{c.Id}/schema", new { }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DescribeCollection_Isolated()
    {
        var (t, c, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/collections/{c.Id}/describe", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task ArchiveCollection_Isolated()
    {
        var (t, c, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/collections/{c.Id}/archive", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListTemplates_Isolated()
    {
        var (t, c, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Get, $"/collections/{c.Id}/templates", homeStatus: HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateTemplate_Isolated()
    {
        var (t, c, _) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Post, $"/collections/{c.Id}/templates", new { }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateTemplate_Isolated()
    {
        var (t, c, templateId) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Patch, $"/collections/{c.Id}/templates/{templateId}", new { name = "" }, homeStatus: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteTemplate_Isolated()
    {
        var (t, c, templateId) = await AlphaAsync();
        await t.AssertIsolatedAsync(HttpMethod.Delete, $"/collections/{c.Id}/templates/{templateId}", homeStatus: HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ForeignIds_InOwnWorkspace_NotFound()
    {
        var (t, alpha, _) = await AlphaAsync();
        var (betaSpace, beta, betaTemplate) = await SchemaApi.SeedAsync(t.BetaOwner.Client, t.Beta.Slug);
        var client = t.AlphaOwner.Client;
        string Alpha(string relative) => TenancyFixture.Path(t.Alpha.Slug, relative);

        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/collections/{beta.Id}"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Patch, Alpha($"/collections/{beta.Id}"), new { name = "Taken over" });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/collections/{beta.Id}/schema"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Patch, Alpha($"/collections/{beta.Id}/schema"), new { add = new[] { new { key = "x", name = "X", type = "text" } } });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/collections/{beta.Id}/describe"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, Alpha($"/collections/{beta.Id}/archive"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Get, Alpha($"/collections/{beta.Id}/templates"));
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Post, Alpha($"/collections/{beta.Id}/templates"), new { name = "Mine" });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Patch, Alpha($"/collections/{beta.Id}/templates/{betaTemplate.Id}"), new { name = "Mine" });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Delete, Alpha($"/collections/{beta.Id}/templates/{betaTemplate.Id}"));
        // A foreign template under an own collection id is just as invisible.
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Patch, Alpha($"/collections/{alpha.Id}/templates/{betaTemplate.Id}"), new { name = "Mine" });
        await TenancyFixture.AssertNotFoundAsync(client, HttpMethod.Delete, Alpha($"/collections/{alpha.Id}/templates/{betaTemplate.Id}"));

        // Beta's data is untouched.
        var stillThere = await t.BetaOwner.Client.GetFromJsonAsync<CollectionDto>(TenancyFixture.Path(t.Beta.Slug, $"/collections/{beta.Id}"), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(beta.Name, stillThere!.Name);
        Assert.Null(stillThere.ArchivedAt);
        Assert.Equal(betaSpace.Id, stillThere.SpaceId);
    }

    [Fact]
    public async Task CreateCollection_InForeignSpace_ValidationNotFound_RelationToForeignCollection_NotFound()
    {
        var (t, _, _) = await AlphaAsync();
        var (betaSpace, beta, _) = await SchemaApi.SeedAsync(t.BetaOwner.Client, t.Beta.Slug);
        var alphaSpace = await SchemaApi.CreateSpaceAsync(t.AlphaOwner.Client, t.Alpha.Slug, "Rel");

        using var inForeignSpace = await t.AlphaOwner.Client.PostAsJsonAsync(
            TenancyFixture.Path(t.Alpha.Slug, "/collections"), new { spaceId = betaSpace.Id, name = "X", description = "D." }, Ct);
        using var foreignTarget = await t.AlphaOwner.Client.PostAsJsonAsync(
            TenancyFixture.Path(t.Alpha.Slug, "/collections"),
            new { spaceId = alphaSpace.Id, name = "X", description = "D.", schema = new[] { new { key = "rel", name = "Rel", type = "relation", targetCollectionId = beta.Id } } },
            Ct);

        var problem = await Identity.ProblemAssert.IsProblemAsync(inForeignSpace, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("not_found", Identity.ProblemAssert.Field(problem, "spaceId").GetProperty("code").GetString());
        problem = await Identity.ProblemAssert.IsProblemAsync(foreignTarget, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("not_found", Identity.ProblemAssert.Field(problem, "schema[0].targetCollectionId").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ListCollections_ShowsOnlyOwnWorkspace()
    {
        var (t, alpha, _) = await AlphaAsync();
        var (_, beta, _) = await SchemaApi.SeedAsync(t.BetaOwner.Client, t.Beta.Slug);

        var list = await t.AlphaOwner.Client.GetFromJsonAsync<List<CollectionDto>>(TenancyFixture.Path(t.Alpha.Slug, "/collections"), AgcoworkApiFactory.Json, Ct);

        Assert.Contains(list!, c => c.Id == alpha.Id);
        Assert.DoesNotContain(list!, c => c.Id == beta.Id);
    }
}
