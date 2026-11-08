using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Api.Tests.Views;
using Agcowork.Application.Collections;
using Agcowork.Application.Query;
using Agcowork.Application.Records;
using Agcowork.Application.Workspaces;

namespace Agcowork.Api.Tests.Records;

[Collection(ApiTestGroup.Name)]
public sealed class QueryEndpointsTests(AgcoworkApiFactory factory)
{
    private static readonly string[] Db = ["db"];
    private static readonly string[] Api = ["api"];
    private static readonly string[] Both = ["db", "api"];
    private static readonly string[] OpenStatuses = ["todo", "doing"];
    private static readonly string[] Tags = ["tags"];
    private static readonly string[] TitleStatus = ["title", "status"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestAgent Owner, WorkspaceDto Workspace)> OwnWorkspaceAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        return (owner, await TenancyFixture.CreateWorkspaceAsync(owner.Client, "qry"));
    }

    private static string Path(WorkspaceDto workspace, string relative) => TenancyFixture.Path(workspace.Slug, relative);

    /// <summary>Collection "Aufgaben" with five records A..E (status, tags), created in this order.</summary>
    private static async Task<(CollectionDto Collection, List<RecordDto> Records)> SeedAsync(HttpClient client, WorkspaceDto workspace)
    {
        var (_, collection) = await RecordsApi.SeedCollectionAsync(client, workspace.Slug);
        var records = new List<RecordDto>();
        foreach (var (title, status, tags) in new (string, string, string[]?)[]
                 {
                     ("A", "todo", Db), ("B", "doing", Api), ("C", "done", Both), ("D", "doing", null), ("E", "todo", Api),
                 })
        {
            records.Add(await RecordsApi.CreateAsync(client, workspace.Slug, new { collectionId = collection.Id, title, properties = new { status, tags } }));
        }

        return (collection, records);
    }

    private static async Task<QueryResult<RecordDto>> PostQueryAsync(HttpClient client, string path, object? body)
    {
        using var response = await TenancyFixture.SendAsync(client, HttpMethod.Post, path, body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<QueryResult<RecordDto>>(AgcoworkApiFactory.Json, Ct))!;
    }

    [Fact]
    public async Task QueryRecords_FilterSortFields_HappyPath()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collection, _) = await SeedAsync(owner.Client, workspace);

        var result = await PostQueryAsync(owner.Client, Path(workspace, $"/collections/{collection.Id}/query"), new
        {
            filter = new { or = new object[] { new { prop = "status", op = "in", value = OpenStatuses }, new { prop = "tags", op = "contains", value = "db" } } },
            sorts = new[] { new { prop = "title", dir = "desc" } },
            fields = Tags,
            includeCount = true,
        });

        Assert.Equal(["E", "D", "C", "B", "A"], result.Items.Select(r => r.Title));
        Assert.Equal(5, result.Count);
        Assert.Null(result.NextCursor);
        Assert.All(result.Items, r => Assert.DoesNotContain("status", r.Properties.Keys));
        Assert.Null(result.Items[0].Body);
    }

    [Fact]
    public async Task QueryRecords_EmptyBody_AllRecords_OldestFirst_PagesWithCursor()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collection, records) = await SeedAsync(owner.Client, workspace);
        var path = Path(workspace, $"/collections/{collection.Id}/query");

        using var empty = await owner.Client.PostAsync(new Uri(path, UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var json = await SchemaApi.ReadJsonAsync(empty);
        Assert.Equal(records.Select(r => r.Id.ToString()), json.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        // nextCursor is required in the contract: present as null on the last page; count is optional (only with includeCount).
        Assert.Equal(JsonValueKind.Null, json.GetProperty("nextCursor").ValueKind);
        Assert.False(json.TryGetProperty("count", out _));

        var walked = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await PostQueryAsync(owner.Client, path, new { sorts = new[] { new { prop = "status", dir = "asc" } }, limit = 2, cursor });
            walked.AddRange(page.Items.Select(r => r.Title));
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(["B", "D", "C", "A", "E"], walked);
    }

    [Fact]
    public async Task QueryRecords_InvalidQueries_400WithAllowed()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collection, _) = await SeedAsync(owner.Client, workspace);
        var path = Path(workspace, $"/collections/{collection.Id}/query");

        using var unknown = await TenancyFixture.SendAsync(owner.Client, HttpMethod.Post, path, new { filter = new { prop = "priority", op = "eq", value = "high" } });
        var problem = await ProblemAssert.IsProblemAsync(unknown, HttpStatusCode.BadRequest, "validation_failed");
        var error = ProblemAssert.Field(problem, "filter.prop");
        Assert.Equal("unknown_property", error.GetProperty("code").GetString());
        Assert.Equal(["title", "status", "tags", "created_at", "updated_at", "created_by", "updated_by"], error.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));

        using var notApplicable = await TenancyFixture.SendAsync(owner.Client, HttpMethod.Post, path, new { filter = new { and = new[] { new { prop = "tags", op = "eq", value = "db" } } } });
        problem = await ProblemAssert.IsProblemAsync(notApplicable, HttpStatusCode.BadRequest, "validation_failed");
        error = ProblemAssert.Field(problem, "filter.and[0].op");
        Assert.Equal("operator_not_applicable", error.GetProperty("code").GetString());
        Assert.Equal(["contains", "in", "nin", "is_empty", "is_not_empty"], error.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));

        using var option = await TenancyFixture.SendAsync(owner.Client, HttpMethod.Post, path, new { filter = new { prop = "status", op = "eq", value = "verworfen" } });
        problem = await ProblemAssert.IsProblemAsync(option, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(["todo", "doing", "done"], ProblemAssert.Field(problem, "filter.value").GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));

        using var cursor = await TenancyFixture.SendAsync(owner.Client, HttpMethod.Post, path, new { cursor = "kaputt" });
        problem = await ProblemAssert.IsProblemAsync(cursor, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("invalid_cursor", ProblemAssert.Field(problem, "cursor").GetProperty("code").GetString());

        using var limit = await TenancyFixture.SendAsync(owner.Client, HttpMethod.Post, path, new { limit = 501 });
        await ProblemAssert.IsProblemAsync(limit, HttpStatusCode.BadRequest, "validation_failed");

        using var unknownKey = await TenancyFixture.SendAsync(owner.Client, HttpMethod.Post, path, new { filter = new { prop = "status", op = "eq", value = "todo", negate = true } });
        problem = await ProblemAssert.IsProblemAsync(unknownKey, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("unknown_property", ProblemAssert.Field(problem, "filter.negate").GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string path, string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri(path, UriKind.Relative), content, Ct);
    }

    public static TheoryData<string> UnstorableFilters => new()
    {
        """{ "prop": "status", "op": "eq", "value": "\u0000" }""",
        """{ "prop": "title", "op": "eq", "value": "\u0000" }""",
        """{ "prop": "title", "op": "contains", "value": "a\u0000" }""",
        """{ "prop": "title", "op": "starts_with", "value": "\ud800" }""",
        """{ "prop": "title", "op": "eq", "value": "x\udfff" }""",
        """{ "prop": "tags", "op": "contains", "value": "\ud800" }""",
    };

    [Theory]
    [MemberData(nameof(UnstorableFilters))]
    public async Task NulOrUnpairedSurrogate_InFilter_400NeverServerError(string filter)
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collection, _) = await SeedAsync(owner.Client, workspace);
        var view = await ViewsApi.CreateAsync(owner.Client, workspace.Slug, collection.Id, new { name = "Alle", type = "table" });

        foreach (var (path, body) in new[]
                 {
                     (Path(workspace, $"/collections/{collection.Id}/query"), $$"""{ "filter": {{filter}} }"""),
                     (Path(workspace, $"/views/{view.Id}/query"), $$"""{ "filter": {{filter}} }"""),
                     (Path(workspace, $"/collections/{collection.Id}/views"), $$"""{ "name": "Kaputt", "type": "table", "filter": {{filter}} }"""),
                 })
        {
            using var response = await PostRawAsync(owner.Client, path, body);
            var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Equal("invalid_value", ProblemAssert.Field(problem, "filter.value").GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData("\\u0000")]
    [InlineData("\\ud800")]
    public async Task NulOrUnpairedSurrogate_InCursor_400InvalidCursor(string escaped)
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collection, _) = await SeedAsync(owner.Client, workspace);
        var json = $$"""{"v":1,"s":"title:asc","k":["{{escaped}}"],"c":"2026-11-07T12:00:00.0000000+00:00","i":"0190f5e2-7a4c-7000-8000-000000000001"}""";
        var cursor = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(json));

        using var response = await TenancyFixture.SendAsync(
            owner.Client, HttpMethod.Post, Path(workspace, $"/collections/{collection.Id}/query"), new { sorts = new[] { new { prop = "title", dir = "asc" } }, cursor });

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("invalid_cursor", ProblemAssert.Field(problem, "cursor").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("filters", false)]
    [InlineData("sort", false)]
    [InlineData("sorts", true)]
    public async Task UnknownTopLevelMembers_400UnknownPropertyWithAllowed(string member, bool validForCollections)
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collection, _) = await SeedAsync(owner.Client, workspace);
        var view = await ViewsApi.CreateAsync(owner.Client, workspace.Slug, collection.Id, new { name = "Alle", type = "table" });
        var body = member == "filters" ? """{ "filters": { "prop": "status", "op": "eq", "value": "todo" } }""" : $$"""{ "{{member}}": [ { "prop": "title", "dir": "asc" } ] }""";

        using (var onCollection = await PostRawAsync(owner.Client, Path(workspace, $"/collections/{collection.Id}/query"), body))
        {
            if (validForCollections)
            {
                Assert.Equal(HttpStatusCode.OK, onCollection.StatusCode);
            }
            else
            {
                var problem = await ProblemAssert.IsProblemAsync(onCollection, HttpStatusCode.BadRequest, "validation_failed");
                var error = ProblemAssert.Field(problem, member);
                Assert.Equal("unknown_property", error.GetProperty("code").GetString());
                Assert.Equal(QueryDefinition.Keys, error.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
            }
        }

        using var onView = await PostRawAsync(owner.Client, Path(workspace, $"/views/{view.Id}/query"), body);
        var viewProblem = await ProblemAssert.IsProblemAsync(onView, HttpStatusCode.BadRequest, "validation_failed");
        var viewError = ProblemAssert.Field(viewProblem, member);
        Assert.Equal("unknown_property", viewError.GetProperty("code").GetString());
        Assert.Equal(["filter", "cursor", "limit", "includeArchived", "includeCount"], viewError.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task QueryView_RunsFilterAndSorts_CombinesAdditionalFilterByAnd()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collection, _) = await SeedAsync(owner.Client, workspace);
        var view = await ViewsApi.CreateAsync(owner.Client, workspace.Slug, collection.Id, new
        {
            name = "Offen",
            type = "list",
            filter = new { not = new { prop = "status", op = "eq", value = "done" } },
            sorts = new[] { new { prop = "title", dir = "desc" } },
            visibleProperties = TitleStatus,
        });
        var path = Path(workspace, $"/views/{view.Id}/query");

        var all = await PostQueryAsync(owner.Client, path, null);
        var narrowed = await PostQueryAsync(owner.Client, path, new { filter = new { prop = "tags", op = "contains", value = "api" }, includeCount = true });

        Assert.Equal(["E", "D", "B", "A"], all.Items.Select(r => r.Title));
        Assert.All(all.Items, r => Assert.Equal(["status"], r.Properties.Keys));
        Assert.Equal(["E", "B"], narrowed.Items.Select(r => r.Title));
        Assert.Equal(2, narrowed.Count);
    }

    [Fact]
    public async Task Viewer_MayQueryBothEndpoints()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (collection, _) = await SeedAsync(owner.Client, workspace);
        var view = await ViewsApi.CreateAsync(owner.Client, workspace.Slug, collection.Id, new { name = "Alle", type = "table" });
        var viewer = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, viewer.ActorId, "viewer");

        Assert.Equal(5, (await PostQueryAsync(viewer.Client, Path(workspace, $"/collections/{collection.Id}/query"), new { })).Items.Count);
        Assert.Equal(5, (await PostQueryAsync(viewer.Client, Path(workspace, $"/views/{view.Id}/query"), new { })).Items.Count);
    }

    [Fact]
    public async Task OpenApi_QueryEndpoints_DescribeTheOperatorMatrix()
    {
        using var client = factory.CreateClient();
        var document = JsonDocument.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct)).RootElement;
        var paths = document.GetProperty("paths");

        foreach (var (path, method) in new[] { ("/api/v1/w/{workspaceSlug}/collections/{collectionId}/query", "post"), ("/api/v1/w/{workspaceSlug}/views/{viewId}/query", "post") })
        {
            var description = paths.GetProperty(path).GetProperty(method).GetProperty("description").GetString()!;
            Assert.Contains("multi_select: contains, in, nin, is_empty, is_not_empty", description, StringComparison.Ordinal);
            Assert.Contains("checkbox: eq, neq, is_empty;", description, StringComparison.Ordinal);
            Assert.Contains("operator_not_applicable", description, StringComparison.Ordinal);
            Assert.True(paths.GetProperty(path).GetProperty(method).GetProperty("responses").TryGetProperty("404", out _));
        }

        Assert.True(document.GetProperty("components").GetProperty("schemas").GetProperty("ViewFilterNode").GetProperty("properties").TryGetProperty("not", out _));
    }
}
