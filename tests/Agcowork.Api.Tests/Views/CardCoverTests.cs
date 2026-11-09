using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Mcp;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Workspaces;

namespace Agcowork.Api.Tests.Views;

/// <summary>Cover types of gallery views (AP-042): only types the UI can show; schema type change removes a cover.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CardCoverTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestAgent Owner, WorkspaceDto Workspace, Guid CollectionId)> ArrangeAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "cover");
        var space = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug, $"Space {Guid.NewGuid():N}");
        var collection = await SchemaApi.CreateCollectionAsync(owner.Client, workspace.Slug, space.Id, schema:
        [
            new { key = "summary", name = "Summary", type = "text" },
            new { key = "payload", name = "Payload", type = "json" },
            new { key = "owner", name = "Owner", type = "actor" },
            new { key = "due", name = "Due", type = "date" },
        ]);
        return (owner, workspace, collection.Id);
    }

    private static async Task AssertInvalidCoverAsync(HttpResponseMessage response)
    {
        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        var error = ProblemAssert.Field(problem, "cardCoverProperty");
        Assert.Equal("invalid_type", error.GetProperty("code").GetString());
        Assert.Equal(["summary", "due"], error.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Rest_CreateAndUpdateView_CoverOfJsonOrActor_BadRequestWithAllowed()
    {
        var (owner, workspace, collectionId) = await ArrangeAsync();
        var path = TenancyFixture.Path(workspace.Slug, $"/collections/{collectionId}/views");
        var view = await ViewsApi.CreateAsync(owner.Client, workspace.Slug, collectionId, new { name = "Galerie", type = "gallery", cardCoverProperty = "due" });

        using var create = await owner.Client.PostAsJsonAsync(path, new { name = "Falsch", type = "gallery", cardCoverProperty = "payload" }, Ct);
        using var update = await owner.Client.PatchAsJsonAsync(TenancyFixture.Path(workspace.Slug, $"/views/{view.Id}"), new { cardCoverProperty = "owner" }, Ct);

        await AssertInvalidCoverAsync(create);
        await AssertInvalidCoverAsync(update);
        Assert.Equal("due", (await ViewsApi.GetAsync(owner.Client, workspace.Slug, view.Id)).CardCoverProperty);
    }

    [Fact]
    public async Task Mcp_ViewCreate_CoverOfJson_ToolErrorWithAllowed()
    {
        var (owner, workspace, collectionId) = await ArrangeAsync();
        await using var client = await McpClientFixture.ConnectAsync(factory, owner.Key, workspace.Slug);

        var result = await McpClientFixture.CallAsync(client, "view_create", new { collectionId, name = "Falsch", type = "gallery", cardCoverProperty = "payload" });

        var problem = McpClientFixture.Error(result, 400, "validation_failed");
        var error = problem.GetProperty("errors").EnumerateArray().Single(e => e.GetProperty("field").GetString() == "cardCoverProperty");
        Assert.Equal("invalid_type", error.GetProperty("code").GetString());
        Assert.Equal(["summary", "due"], error.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Rest_SchemaTypeChangeToMarkdown_RemovesCover()
    {
        var (owner, workspace, collectionId) = await ArrangeAsync();
        var view = await ViewsApi.CreateAsync(owner.Client, workspace.Slug, collectionId, new { name = "Galerie", type = "gallery", cardCoverProperty = "summary" });

        using var response = await owner.Client.PatchAsJsonAsync(
            TenancyFixture.Path(workspace.Slug, $"/collections/{collectionId}/schema"), new { update = new[] { new { key = "summary", type = "markdown" } } }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await ViewsApi.GetAsync(owner.Client, workspace.Slug, view.Id)).CardCoverProperty);
    }
}
