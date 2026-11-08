using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Workspaces;

namespace Agcowork.Api.Tests.Views;

[Collection(ApiTestGroup.Name)]
public sealed class ViewsEndpointsTests(AgcoworkApiFactory factory)
{
    private static readonly string[] ViewTypes = ["table", "board", "gallery", "list", "document", "raw"];
    private static readonly string[] StatusOperators = ["eq", "neq", "in", "nin", "is_empty", "is_not_empty"];

    private static readonly int[] One = [1];
    private static readonly string[] Todo = ["todo"];
    private static readonly string[] StatusKey = ["status"];
    private static readonly string[] TitleDue = ["title", "due"];
    private static readonly string[] TitleStatusDue = ["title", "status", "due"];
    private static readonly object[] StatusSort = [new { prop = "status", dir = "asc" }];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestAgent Owner, WorkspaceDto Workspace)> OwnWorkspaceAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        return (owner, await TenancyFixture.CreateWorkspaceAsync(owner.Client, "views"));
    }

    private async Task<TestAgent> MemberAsync(TestAgent owner, WorkspaceDto workspace, string role)
    {
        var agent = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, agent.ActorId, role);
        return agent;
    }

    private static string Path(WorkspaceDto workspace, string relative) => TenancyFixture.Path(workspace.Slug, relative);

    [Fact]
    public async Task CreateCollection_CreatesTableAndRawSystemViews()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collectionId, _) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);

        var views = await ViewsApi.ListAsync(owner.Client, workspace.Slug, collectionId);

        Assert.Equal(["Tabelle", "Rohdaten", "Board"], views.Select(v => v.Name));
        Assert.Equal(["table", "raw", "board"], views.Select(v => v.Type));
        Assert.Equal([true, true, false], views.Select(v => v.IsSystem));
        Assert.Equal("Tabelle", Assert.Single(views, v => v.IsDefault).Name);
    }

    [Fact]
    public async Task CreateView_Board_Returns201WithLocationAndStoresFilterAndSorts()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collectionId, _) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);

        using var response = await owner.Client.PostAsJsonAsync(
            Path(workspace, $"/collections/{collectionId}/views"),
            new
            {
                name = "Offen nach Status",
                type = "board",
                groupBy = "status",
                filter = new
                {
                    and = new object[]
                    {
                        new { prop = "status", op = "in", value = Todo },
                        new { or = new object[] { new { prop = "due", op = "lt", value = "2026-12-01" }, new { prop = "priority", op = "eq", value = "high" } } },
                    },
                },
                sorts = new[] { new { prop = "due", dir = "asc" } },
                visibleProperties = TitleDue,
            },
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await SchemaJson(response);
        Assert.EndsWith($"/views/{created.GetProperty("id").GetString()}", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        var fetched = await ViewsApi.GetAsync(owner.Client, workspace.Slug, Guid.Parse(created.GetProperty("id").GetString()!));
        Assert.Equal(("board", "status", false, false), (fetched.Type, fetched.GroupBy, fetched.NeedsAttention, fetched.IsSystem));
        Assert.Equal("in", fetched.Filter!.And![0].Op);
        Assert.Equal("high", fetched.Filter.And[1].Or![1].Value!.Value.GetString());
        Assert.Equal(["title", "due"], fetched.VisibleProperties);
        Assert.Equal("due", Assert.Single(fetched.Sorts).Prop);
    }

    private static async Task<System.Text.Json.JsonElement> SchemaJson(HttpResponseMessage response) =>
        System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    [Fact]
    public async Task CreateView_InvalidDefinitions_400WithAllowed()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collectionId, _) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);
        var path = Path(workspace, $"/collections/{collectionId}/views");

        using var badOp = await owner.Client.PostAsJsonAsync(path, new { name = "A", type = "table", filter = new { and = new[] { new { prop = "status", op = "like", value = "x" } } } }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(badOp, HttpStatusCode.BadRequest, "validation_failed");
        var op = ProblemAssert.Field(problem, "filter.and[0].op");
        Assert.Equal("unknown_operator", op.GetProperty("code").GetString());
        Assert.Equal(StatusOperators, op.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));

        using var badProp = await owner.Client.PostAsJsonAsync(path, new { name = "B", type = "table", filter = new { and = new[] { new { prop = "nope", op = "eq", value = "x" } } } }, Ct);
        problem = await ProblemAssert.IsProblemAsync(badProp, HttpStatusCode.BadRequest, "validation_failed");
        var prop = ProblemAssert.Field(problem, "filter.and[0].prop");
        Assert.Equal("unknown_property", prop.GetProperty("code").GetString());
        Assert.Equal(["title", "status", "priority", "due", "created_at", "updated_at", "created_by", "updated_by"], prop.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));

        using var boardWithoutGroup = await owner.Client.PostAsJsonAsync(path, new { name = "C", type = "board" }, Ct);
        problem = await ProblemAssert.IsProblemAsync(boardWithoutGroup, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("required", ProblemAssert.Field(problem, "groupBy").GetProperty("code").GetString());

        using var badGroup = await owner.Client.PostAsJsonAsync(path, new { name = "C2", type = "board", groupBy = "due" }, Ct);
        problem = await ProblemAssert.IsProblemAsync(badGroup, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("invalid_type", ProblemAssert.Field(problem, "groupBy").GetProperty("code").GetString());

        using var timeline = await owner.Client.PostAsJsonAsync(path, new { name = "D", type = "timeline" }, Ct);
        problem = await ProblemAssert.IsProblemAsync(timeline, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(ViewTypes, ProblemAssert.Field(problem, "type").GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));

        using var noName = await owner.Client.PostAsJsonAsync(path, new { type = "table" }, Ct);
        await ProblemAssert.IsProblemAsync(noName, HttpStatusCode.BadRequest, "validation_failed");

        using var duplicate = await owner.Client.PostAsJsonAsync(path, new { name = "Tabelle", type = "list" }, Ct);
        await ProblemAssert.IsProblemAsync(duplicate, HttpStatusCode.Conflict, "view_name_taken");
    }

    [Fact]
    public async Task UpdateView_GetPatchDelete_Lifecycle()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collectionId, board) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);
        var path = Path(workspace, $"/views/{board.Id}");

        using var patch = await owner.Client.PatchAsJsonAsync(path, new { name = "Prio-Board", groupBy = "priority", sorts = new[] { new { prop = "due", dir = "desc" } } }, Ct);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var updated = await ViewsApi.GetAsync(owner.Client, workspace.Slug, board.Id);
        Assert.Equal(("Prio-Board", "priority", "board"), (updated.Name, updated.GroupBy, updated.Type));

        using var invalid = await owner.Client.PatchAsJsonAsync(path, new { sorts = new[] { new { prop = "due", dir = "sideways" } } }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(["asc", "desc"], ProblemAssert.Field(problem, "sorts[0].dir").GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));

        using var delete = await owner.Client.DeleteAsync(new Uri(path, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.DoesNotContain(await ViewsApi.ListAsync(owner.Client, workspace.Slug, collectionId), v => v.Id == board.Id);
        using var gone = await owner.Client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        await ProblemAssert.IsProblemAsync(gone, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task DeleteSystemView_409_SetDefaultSwitchesExactlyOneDefault()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collectionId, board) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);
        var raw = (await ViewsApi.ListAsync(owner.Client, workspace.Slug, collectionId)).Single(v => v.Type == "raw");

        using var delete = await owner.Client.DeleteAsync(new Uri(Path(workspace, $"/views/{raw.Id}"), UriKind.Relative), Ct);
        await ProblemAssert.IsProblemAsync(delete, HttpStatusCode.Conflict, "system_view");

        using var makeDefault = await owner.Client.PostAsync(new Uri(Path(workspace, $"/views/{board.Id}/default"), UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, makeDefault.StatusCode);
        var views = await ViewsApi.ListAsync(owner.Client, workspace.Slug, collectionId);
        Assert.Equal(board.Id, Assert.Single(views, v => v.IsDefault).Id);
        Assert.Equal(3, views.Count);
    }

    [Fact]
    public async Task RemovingSchemaProperty_CleansViews_BoardNeedsAttention()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collectionId, board) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);
        var table = (await ViewsApi.ListAsync(owner.Client, workspace.Slug, collectionId)).Single(v => v.Type == "table");
        using var configure = await owner.Client.PatchAsJsonAsync(
            Path(workspace, $"/views/{table.Id}"), new { visibleProperties = TitleStatusDue, sorts = StatusSort }, Ct);
        Assert.Equal(HttpStatusCode.OK, configure.StatusCode);

        using var remove = await owner.Client.PatchAsJsonAsync(Path(workspace, $"/collections/{collectionId}/schema"), new { remove = StatusKey, confirmRemoval = true }, Ct);
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);

        var cleanedBoard = await ViewsApi.GetAsync(owner.Client, workspace.Slug, board.Id);
        Assert.Equal(("board", null, true), (cleanedBoard.Type, cleanedBoard.GroupBy, cleanedBoard.NeedsAttention));
        var cleanedTable = await ViewsApi.GetAsync(owner.Client, workspace.Slug, table.Id);
        Assert.Equal(["title", "due"], cleanedTable.VisibleProperties);
        Assert.Empty(cleanedTable.Sorts);
        Assert.False(cleanedTable.NeedsAttention);
    }

    [Fact]
    public async Task Permissions_ViewerReadsButCannotWrite()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var viewer = await MemberAsync(owner, workspace, "viewer");
        var editor = await MemberAsync(owner, workspace, "editor");
        var (collectionId, board) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);

        async Task ExpectForbiddenAsync(HttpMethod method, string relative, object? body)
        {
            using var response = await TenancyFixture.SendAsync(viewer.Client, method, Path(workspace, relative), body);
            var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
            Assert.Equal("requires write", problem.GetProperty("detail").GetString());
        }

        await ExpectForbiddenAsync(HttpMethod.Post, $"/collections/{collectionId}/views", new { name = "X", type = "list" });
        await ExpectForbiddenAsync(HttpMethod.Patch, $"/views/{board.Id}", new { name = "Y" });
        await ExpectForbiddenAsync(HttpMethod.Delete, $"/views/{board.Id}", null);
        await ExpectForbiddenAsync(HttpMethod.Post, $"/views/{board.Id}/default", null);

        Assert.Equal(3, (await ViewsApi.ListAsync(viewer.Client, workspace.Slug, collectionId)).Count);
        Assert.Equal(board.Id, (await ViewsApi.GetAsync(viewer.Client, workspace.Slug, board.Id)).Id);

        // Views are presentation: editors (write) manage them without the schema scope.
        var created = await ViewsApi.CreateAsync(editor.Client, workspace.Slug, collectionId, new { name = "Liste", type = "list" });
        using var delete = await editor.Client.DeleteAsync(new Uri(Path(workspace, $"/views/{created.Id}"), UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task ArchivedCollection_WritesConflict_ReadsWork()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collectionId, board) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);
        using var archive = await owner.Client.PostAsync(new Uri(Path(workspace, $"/collections/{collectionId}/archive"), UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        async Task ExpectArchivedAsync(HttpMethod method, string relative, object? body)
        {
            using var response = await TenancyFixture.SendAsync(owner.Client, method, Path(workspace, relative), body);
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "collection_archived");
        }

        await ExpectArchivedAsync(HttpMethod.Post, $"/collections/{collectionId}/views", new { name = "X", type = "list" });
        await ExpectArchivedAsync(HttpMethod.Patch, $"/views/{board.Id}", new { name = "Y" });
        await ExpectArchivedAsync(HttpMethod.Delete, $"/views/{board.Id}", null);
        await ExpectArchivedAsync(HttpMethod.Post, $"/views/{board.Id}/default", null);
        Assert.Equal(3, (await ViewsApi.ListAsync(owner.Client, workspace.Slug, collectionId)).Count);
    }

    [Fact]
    public async Task ViewEvents_AreWrittenToTheActivityLog()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collectionId, board) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);
        using var patch = await owner.Client.PatchAsJsonAsync(Path(workspace, $"/views/{board.Id}"), new { name = "Neu" }, Ct);
        using var delete = await owner.Client.DeleteAsync(new Uri(Path(workspace, $"/views/{board.Id}"), UriKind.Relative), Ct);

        var types = (await Collections.SchemaApi.ReadJsonAsync(await owner.Client.GetAsync(new Uri(Path(workspace, $"/activity?collectionId={collectionId}&limit=50"), UriKind.Relative), Ct)))
            .GetProperty("items").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();

        Assert.Contains("view.created", types);
        Assert.Contains("view.updated", types);
        Assert.Contains("view.deleted", types);
    }

    [Fact]
    public async Task OpenApi_DocumentsAllViewEndpointsForAgents()
    {
        using var client = factory.CreateClient();
        var document = await Collections.SchemaApi.ReadJsonAsync(await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct));
        var paths = document.GetProperty("paths");

        var expected = new (string Path, string Method)[]
        {
            ("/collections/{collectionId}/views", "get"), ("/collections/{collectionId}/views", "post"),
            ("/views/{viewId}", "get"), ("/views/{viewId}", "patch"), ("/views/{viewId}", "delete"), ("/views/{viewId}/default", "post"),
        };
        foreach (var (relative, method) in expected)
        {
            var path = "/api/v1/w/{workspaceSlug}" + relative;
            Assert.True(paths.TryGetProperty(path, out var item), $"Missing path {path}");
            Assert.True(item.TryGetProperty(method, out var operation), $"Missing {method} {path}");
            Assert.True(operation.GetProperty("description").GetString()!.Length > 40, $"Description too short on {method} {path}");
            Assert.True(operation.GetProperty("responses").TryGetProperty("404", out _), $"No 404 on {method} {path}");
        }
    }

    [Fact]
    public async Task CreateView_UnknownFilterKeys_400WithAllowedKeys()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collectionId, _) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);
        var path = Path(workspace, $"/collections/{collectionId}/views");

        using var negated = await owner.Client.PostAsJsonAsync(path, new { name = "N", type = "table", filter = new { nicht = new { prop = "status", op = "eq", value = "todo" } } }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(negated, HttpStatusCode.BadRequest, "validation_failed");
        var error = ProblemAssert.Field(problem, "filter.nicht");
        Assert.Equal("unknown_property", error.GetProperty("code").GetString());
        Assert.Equal(["and", "or", "not", "prop", "op", "value"], error.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));

        using var not = await owner.Client.PostAsJsonAsync(path, new { name = "Nicht erledigt", type = "table", filter = new { not = new { prop = "status", op = "eq", value = "done" } } }, Ct);
        Assert.Equal(HttpStatusCode.Created, not.StatusCode);

        using var nested = await owner.Client.PostAsJsonAsync(path, new { name = "M", type = "table", filter = new { and = new object[] { new { all = One } } } }, Ct);
        problem = await ProblemAssert.IsProblemAsync(nested, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("unknown_property", ProblemAssert.Field(problem, "filter.and[0].all").GetProperty("code").GetString());

        using var emptyValue = await owner.Client.PostAsJsonAsync(path, new { name = "E", type = "table", filter = new { prop = "due", op = "is_empty", value = "x" } }, Ct);
        problem = await ProblemAssert.IsProblemAsync(emptyValue, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("not_allowed", ProblemAssert.Field(problem, "filter.value").GetProperty("code").GetString());

        using var emptyFilter = await owner.Client.PostAsJsonAsync(path, new { name = "OK", type = "table", filter = new { } }, Ct);
        Assert.Equal(HttpStatusCode.Created, emptyFilter.StatusCode);
    }

    [Fact]
    public async Task Responses_RequiredMembersAsNull_FilterNodeMembersOmitted()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collectionId, _) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);
        await ViewsApi.CreateAsync(owner.Client, workspace.Slug, collectionId, new { name = "F", type = "list", filter = new { prop = "due", op = "is_empty" } });

        var views = await Collections.SchemaApi.ReadJsonAsync(await owner.Client.GetAsync(new Uri(Path(workspace, $"/collections/{collectionId}/views"), UriKind.Relative), Ct));

        var filtered = views.EnumerateArray().Single(v => v.GetProperty("name").GetString() == "F");
        Assert.Equal(["op", "prop"], filtered.GetProperty("filter").EnumerateObject().Select(p => p.Name).Order());
        // filter, groupBy and cardCoverProperty are required (nullable) in the contract: present as null (AP-033).
        Assert.Equal(JsonValueKind.Null, views[0].GetProperty("filter").ValueKind);
        Assert.Equal(JsonValueKind.Null, views[0].GetProperty("groupBy").ValueKind);
        Assert.Equal(JsonValueKind.Null, views[0].GetProperty("cardCoverProperty").ValueKind);
    }

    [Fact]
    public async Task SetDefault_Concurrent_ExactlyOneDefaultRemains()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collectionId, board) = await ViewsApi.SeedAsync(owner.Client, workspace.Slug);
        var list = await ViewsApi.CreateAsync(owner.Client, workspace.Slug, collectionId, new { name = "Liste", type = "list" });

        var responses = await Task.WhenAll(
            owner.Client.PostAsync(new Uri(Path(workspace, $"/views/{board.Id}/default"), UriKind.Relative), null, Ct),
            owner.Client.PostAsync(new Uri(Path(workspace, $"/views/{list.Id}/default"), UriKind.Relative), null, Ct));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            await ProblemAssert.IsProblemAsync(conflict, HttpStatusCode.Conflict, "view_default_conflict");
        }

        Assert.Single(await ViewsApi.ListAsync(owner.Client, workspace.Slug, collectionId), v => v.IsDefault);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }
}
