using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Collections;
using Agcowork.Application.Workspaces;

namespace Agcowork.Api.Tests.Collections;

[Collection(ApiTestGroup.Name)]
public sealed class CollectionsEndpointsTests(AgcoworkApiFactory factory)
{
    private static readonly string[] ReadWrite = ["read", "write"];
    private static readonly string[] Summary = ["summary"];
    private static readonly string[] Db = ["db"];
    private static readonly string[] UnknownTag = ["x"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object[] DecisionSchema =
    [
        new
        {
            key = "status", name = "Status", type = "status", required = true, description = "Lifecycle of the decision.",
            options = new object[]
            {
                new { value = "proposed", label = "Vorgeschlagen", group = "todo" },
                new { value = "accepted", label = "Akzeptiert", color = "green", group = "done" },
                new { value = "in_review", label = "Im Review", group = "in_progress" },
            },
            @default = "proposed",
        },
        new { key = "tags", name = "Tags", type = "multi_select", options = new[] { new { value = "db" }, new { value = "api" } } },
        new { key = "date", name = "Datum", type = "date" },
        new { key = "decided_at", name = "Entschieden", type = "datetime" },
    ];

    private async Task<(TestAgent Owner, WorkspaceDto Workspace)> OwnWorkspaceAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        return (owner, await TenancyFixture.CreateWorkspaceAsync(owner.Client, "coll"));
    }

    private async Task<TestAgent> MemberAsync(TestAgent owner, WorkspaceDto workspace, string role)
    {
        var agent = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, agent.ActorId, role);
        return agent;
    }

    private static string Path(WorkspaceDto workspace, string relative) => TenancyFixture.Path(workspace.Slug, relative);

    [Fact]
    public async Task Writer_CreatesSpaceAndCollection_DescribeReturnsSchemaJsonSchemaAndTemplates()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var writer = await MemberAsync(owner, workspace, "editor");
        var space = await SchemaApi.CreateSpaceAsync(writer.Client, workspace.Slug, "Technik");

        using var create = await writer.Client.PostAsJsonAsync(
            Path(workspace, "/collections"),
            new { spaceId = space.Id, name = "Entscheidungen", description = "Architecture decisions (ADR). Add one before changing a component.", schema = DecisionSchema },
            Ct);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var collection = (await create.Content.ReadFromJsonAsync<CollectionDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal(Path(workspace, $"/collections/{collection.Id}"), create.Headers.Location?.OriginalString);
        Assert.Equal(["title", "status", "tags", "date", "decided_at"], collection.Schema.Select(p => p.Key));

        // Templates need the schema scope: the owner (admin) adds one.
        await SchemaApi.CreateTemplateAsync(owner.Client, workspace.Slug, collection.Id, "ADR", new { status = "proposed" });

        using var describe = await writer.Client.GetAsync(new Uri(Path(workspace, $"/collections/{collection.Id}/describe"), UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, describe.StatusCode);
        var body = await SchemaApi.ReadJsonAsync(describe);

        var wireSchema = body.GetProperty("collection").GetProperty("schema");
        Assert.Equal("status", wireSchema[1].GetProperty("type").GetString());
        Assert.Equal("multi_select", wireSchema[2].GetProperty("type").GetString());
        Assert.Equal("datetime", wireSchema[4].GetProperty("type").GetString());
        Assert.Equal("in_progress", wireSchema[1].GetProperty("options")[2].GetProperty("group").GetString());

        var jsonSchema = body.GetProperty("jsonSchema");
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", jsonSchema.GetProperty("$schema").GetString());
        Assert.False(jsonSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["title", "status"], jsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var status = jsonSchema.GetProperty("properties").GetProperty("status");
        Assert.Equal(["proposed", "accepted", "in_review"], status.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Lifecycle of the decision.", status.GetProperty("description").GetString());
        Assert.StartsWith("Architecture decisions (ADR). Add one before changing a component. ", jsonSchema.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("null", jsonSchema.GetProperty("description").GetString(), StringComparison.Ordinal);

        Assert.Equal("proposed", body.GetProperty("example").GetProperty("status").GetString());
        Assert.Equal("ADR", body.GetProperty("templates")[0].GetProperty("name").GetString());
        Assert.Equal("Technik", body.GetProperty("space").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("statistics").GetProperty("recordCount").ValueKind);
    }

    [Fact]
    public async Task CreateCollection_InvalidSchema_400WithErrorsAndAllowed()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var space = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug);

        using var response = await owner.Client.PostAsJsonAsync(Path(workspace, "/collections"), new
        {
            spaceId = space.Id,
            name = "Kaputt",
            description = "D.",
            schema = new object[]
            {
                new { key = "a", name = "A", type = "text" },
                new { key = "a", name = "A2", type = "text" },
                new { key = "title", name = "Titel", type = "number" },
                new { key = "b", name = "B", type = "varchar" },
            },
        }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("duplicate", ProblemAssert.Field(problem, "schema[1].key").GetProperty("code").GetString());
        Assert.Equal("reserved", ProblemAssert.Field(problem, "schema[2].type").GetProperty("code").GetString());
        var type = ProblemAssert.Field(problem, "schema[3].type");
        Assert.Equal("not_in_options", type.GetProperty("code").GetString());
        Assert.Contains("multi_select", type.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("datetime", type.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task CreateCollection_MissingDescription_ExplainsWhy_DuplicateName_Conflict()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var space = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug);
        await SchemaApi.CreateCollectionAsync(owner.Client, workspace.Slug, space.Id, "Aufgaben");

        using var missing = await owner.Client.PostAsJsonAsync(Path(workspace, "/collections"), new { spaceId = space.Id, name = "X" }, Ct);
        using var duplicate = await owner.Client.PostAsJsonAsync(Path(workspace, "/collections"), new { spaceId = space.Id, name = "Aufgaben", description = "D." }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(missing, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("agents read it", ProblemAssert.Field(problem, "description").GetProperty("message").GetString(), StringComparison.Ordinal);
        problem = await ProblemAssert.IsProblemAsync(duplicate, HttpStatusCode.Conflict, "collection_name_taken");
        Assert.Equal("conflict", ProblemAssert.Field(problem, "name").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpdateSchema_AddRenameTypeChangeRemove()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var space = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug);
        var collection = await SchemaApi.CreateCollectionAsync(owner.Client, workspace.Slug, space.Id, schema: [new { key = "summary", name = "Summary", type = "text" }]);
        var path = Path(workspace, $"/collections/{collection.Id}/schema");

        using var add = await owner.Client.PatchAsJsonAsync(path, new { add = new[] { new { key = "due", name = "Due", type = "date" } }, expectedSchemaVersion = 1 }, Ct);
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var added = (await add.Content.ReadFromJsonAsync<CollectionSchemaDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal(2, added.SchemaVersion);
        Assert.Equal(["title", "summary", "due"], added.Properties.Select(p => p.Key));

        using var stale = await owner.Client.PatchAsJsonAsync(path, new { add = new[] { new { key = "x", name = "X", type = "text" } }, expectedSchemaVersion = 1 }, Ct);
        await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.Conflict, "schema_version_conflict");

        using var rename = await owner.Client.PatchAsJsonAsync(path, new { update = new[] { new { key = "summary", newKey = "abstract" } } }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(rename, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("key_immutable", ProblemAssert.Field(problem, "update[0].newKey").GetProperty("code").GetString());

        using var typeChange = await owner.Client.PatchAsJsonAsync(path, new { update = new[] { new { key = "due", type = "number" } } }, Ct);
        problem = await ProblemAssert.IsProblemAsync(typeChange, HttpStatusCode.BadRequest, "validation_failed");
        var type = ProblemAssert.Field(problem, "update[0].type");
        Assert.Equal("type_change_not_allowed", type.GetProperty("code").GetString());
        Assert.Equal(["date"], type.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));

        using var unconfirmed = await owner.Client.PatchAsJsonAsync(path, new { remove = Summary }, Ct);
        problem = await ProblemAssert.IsProblemAsync(unconfirmed, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("removal_not_confirmed", ProblemAssert.Field(problem, "confirmRemoval").GetProperty("code").GetString());

        using var lowerPrecision = await owner.Client.PatchAsJsonAsync(path, new { add = new[] { new { key = "hours", name = "Hours", type = "number" } } }, Ct);
        Assert.Equal(HttpStatusCode.OK, lowerPrecision.StatusCode);
        using var unconfirmedPrecision = await owner.Client.PatchAsJsonAsync(path, new { update = new[] { new { key = "hours", precision = 0 } } }, Ct);
        problem = await ProblemAssert.IsProblemAsync(unconfirmedPrecision, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("precision", ProblemAssert.Field(problem, "confirmRemoval").GetProperty("message").GetString(), StringComparison.Ordinal);

        using var confirmed = await owner.Client.PatchAsJsonAsync(path, new { remove = Summary, confirmRemoval = true }, Ct);
        var removed = (await confirmed.Content.ReadFromJsonAsync<CollectionSchemaDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal(["title", "due", "hours"], removed.Properties.Select(p => p.Key));
        Assert.Equal(4, removed.SchemaVersion);

        var schema = await owner.Client.GetFromJsonAsync<CollectionSchemaDto>(path, AgcoworkApiFactory.Json, Ct);
        Assert.Equal(4, schema!.SchemaVersion);
    }

    [Fact]
    public async Task UpdateSchema_ConcurrentAdds_NoLostUpdate()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var space = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug);
        var collection = await SchemaApi.CreateCollectionAsync(owner.Client, workspace.Slug, space.Id);
        var path = Path(workspace, $"/collections/{collection.Id}/schema");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            owner.Client.PatchAsJsonAsync(path, new { add = new[] { new { key = $"p{i}", name = $"P{i}", type = "text" } } }, Ct)));
        try
        {
            Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
            var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
            var schema = (await owner.Client.GetFromJsonAsync<CollectionSchemaDto>(path, AgcoworkApiFactory.Json, Ct))!;
            Assert.Equal(1 + succeeded, schema.SchemaVersion);
            Assert.Equal(1 + succeeded, schema.Properties.Count);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Permissions_ViewerForbiddenOnWrites_SchemaScopeNeededForSchema()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var viewer = await MemberAsync(owner, workspace, "viewer");
        var editor = await MemberAsync(owner, workspace, "editor");
        var ownerReadWrite = await TenancyFixture.CreateKeyClientAsync(factory, owner.ActorId, new { scopes = ReadWrite });
        var space = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug);
        var collection = await SchemaApi.CreateCollectionAsync(owner.Client, workspace.Slug, space.Id);
        var template = await SchemaApi.CreateTemplateAsync(owner.Client, workspace.Slug, collection.Id);
        var addField = new { add = new[] { new { key = "due", name = "Due", type = "date" } } };

        async Task ExpectForbiddenAsync(HttpClient client, HttpMethod method, string relative, object? body, string scope)
        {
            using var response = await TenancyFixture.SendAsync(client, method, Path(workspace, relative), body);
            var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
            Assert.Equal($"requires {scope}", problem.GetProperty("detail").GetString());
        }

        await ExpectForbiddenAsync(viewer.Client, HttpMethod.Post, "/collections", new { spaceId = space.Id, name = "X", description = "D." }, "write");
        await ExpectForbiddenAsync(viewer.Client, HttpMethod.Patch, $"/collections/{collection.Id}/schema", addField, "schema");
        await ExpectForbiddenAsync(viewer.Client, HttpMethod.Post, $"/collections/{collection.Id}/templates", new { name = "T" }, "schema");
        await ExpectForbiddenAsync(editor.Client, HttpMethod.Patch, $"/collections/{collection.Id}/schema", addField, "schema");
        await ExpectForbiddenAsync(editor.Client, HttpMethod.Patch, $"/collections/{collection.Id}", new { name = "Y" }, "schema");
        await ExpectForbiddenAsync(editor.Client, HttpMethod.Post, $"/collections/{collection.Id}/archive", null, "schema");
        await ExpectForbiddenAsync(editor.Client, HttpMethod.Patch, $"/collections/{collection.Id}/templates/{template.Id}", new { name = "Y" }, "schema");
        await ExpectForbiddenAsync(editor.Client, HttpMethod.Delete, $"/collections/{collection.Id}/templates/{template.Id}", null, "schema");
        await ExpectForbiddenAsync(ownerReadWrite, HttpMethod.Patch, $"/collections/{collection.Id}/schema", addField, "schema");

        // Editors create collections (write) and read everything.
        using var editorCreates = await editor.Client.PostAsJsonAsync(Path(workspace, "/collections"), new { spaceId = space.Id, name = "Notizen", description = "D." }, Ct);
        Assert.Equal(HttpStatusCode.Created, editorCreates.StatusCode);
        using var viewerReads = await viewer.Client.GetAsync(new Uri(Path(workspace, $"/collections/{collection.Id}/templates"), UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, viewerReads.StatusCode);
        ownerReadWrite.Dispose();
    }

    [Fact]
    public async Task Templates_PresetNotMatchingSchema_400_LifecycleWorks()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var space = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug);
        var collection = await SchemaApi.CreateCollectionAsync(owner.Client, workspace.Slug, space.Id, schema: DecisionSchema);
        var path = Path(workspace, $"/collections/{collection.Id}/templates");

        using var invalid = await owner.Client.PostAsJsonAsync(path, new { name = "ADR", propertiesPreset = new { status = "done", owner = "x", date = "07.11.2026" } }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_failed");
        var status = ProblemAssert.Field(problem, "propertiesPreset.status");
        Assert.Equal("not_in_options", status.GetProperty("code").GetString());
        Assert.Equal(["proposed", "accepted", "in_review"], status.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("unknown_property", ProblemAssert.Field(problem, "propertiesPreset.owner").GetProperty("code").GetString());
        Assert.Equal("invalid_format", ProblemAssert.Field(problem, "propertiesPreset.date").GetProperty("code").GetString());

        var adr = await SchemaApi.CreateTemplateAsync(owner.Client, workspace.Slug, collection.Id, "ADR", new { status = "proposed", tags = Db });
        Assert.Equal("## Kontext\n", adr.BodyMarkdown);

        using var update = await owner.Client.PatchAsJsonAsync($"{path}/{adr.Id}", new { isDefault = true, propertiesPreset = new { status = "accepted" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var badUpdate = await owner.Client.PatchAsJsonAsync($"{path}/{adr.Id}", new { propertiesPreset = new { tags = UnknownTag } }, Ct);
        problem = await ProblemAssert.IsProblemAsync(badUpdate, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("not_in_options", ProblemAssert.Field(problem, "propertiesPreset.tags[0]").GetProperty("code").GetString());

        var list = await SchemaApi.ReadJsonAsync(await owner.Client.GetAsync(new Uri(path, UriKind.Relative), Ct));
        Assert.True(list[0].GetProperty("isDefault").GetBoolean());
        Assert.Equal("accepted", list[0].GetProperty("propertiesPreset").GetProperty("status").GetString());

        using var delete = await owner.Client.DeleteAsync(new Uri($"{path}/{adr.Id}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(0, (await SchemaApi.ReadJsonAsync(await owner.Client.GetAsync(new Uri(path, UriKind.Relative), Ct))).GetArrayLength());
    }

    [Fact]
    public async Task ListGetUpdateArchive_Collections()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var produkt = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug, "Produkt");
        var technik = await SchemaApi.CreateSpaceAsync(owner.Client, workspace.Slug, "Technik");
        var tasks = await SchemaApi.CreateCollectionAsync(owner.Client, workspace.Slug, produkt.Id, "Aufgaben");
        await SchemaApi.CreateCollectionAsync(owner.Client, workspace.Slug, technik.Id, "Komponenten");

        var bySpace = await owner.Client.GetFromJsonAsync<List<CollectionDto>>(Path(workspace, $"/collections?spaceId={produkt.Id}"), AgcoworkApiFactory.Json, Ct);
        Assert.Equal(["Aufgaben"], bySpace!.Select(c => c.Name));

        using var update = await owner.Client.PatchAsJsonAsync(Path(workspace, $"/collections/{tasks.Id}"), new { description = "Tasks of the team." }, Ct);
        Assert.Equal("Tasks of the team.", (await update.Content.ReadFromJsonAsync<CollectionDto>(AgcoworkApiFactory.Json, Ct))!.Description);

        using var archive = await owner.Client.PostAsync(new Uri(Path(workspace, $"/collections/{tasks.Id}/archive"), UriKind.Relative), null, Ct);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Equal(["Komponenten"], (await owner.Client.GetFromJsonAsync<List<CollectionDto>>(Path(workspace, "/collections"), AgcoworkApiFactory.Json, Ct))!.Select(c => c.Name));
        var archived = await owner.Client.GetFromJsonAsync<CollectionDto>(Path(workspace, $"/collections/{tasks.Id}"), AgcoworkApiFactory.Json, Ct);
        Assert.NotNull(archived!.ArchivedAt);

        using var schemaOnArchived = await owner.Client.PatchAsJsonAsync(
            Path(workspace, $"/collections/{tasks.Id}/schema"), new { add = new[] { new { key = "x", name = "X", type = "text" } } }, Ct);
        await ProblemAssert.IsProblemAsync(schemaOnArchived, HttpStatusCode.Conflict, "collection_archived");
    }

    [Fact]
    public async Task OpenApi_DocumentsAllSchemaEndpointsForAgents()
    {
        using var client = factory.CreateClient();
        var document = await SchemaApi.ReadJsonAsync(await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct));
        var paths = document.GetProperty("paths");

        var expected = new (string Path, string Method)[]
        {
            ("/spaces", "get"), ("/spaces", "post"), ("/spaces/{spaceId}", "get"), ("/spaces/{spaceId}", "patch"), ("/spaces/{spaceId}/archive", "post"),
            ("/collections", "get"), ("/collections", "post"), ("/collections/{collectionId}", "get"), ("/collections/{collectionId}", "patch"),
            ("/collections/{collectionId}/schema", "get"), ("/collections/{collectionId}/schema", "patch"),
            ("/collections/{collectionId}/describe", "get"), ("/collections/{collectionId}/archive", "post"),
            ("/collections/{collectionId}/templates", "get"), ("/collections/{collectionId}/templates", "post"),
            ("/collections/{collectionId}/templates/{templateId}", "patch"), ("/collections/{collectionId}/templates/{templateId}", "delete"),
        };

        foreach (var (relative, method) in expected)
        {
            var path = "/api/v1/w/{workspaceSlug}" + relative;
            Assert.True(paths.TryGetProperty(path, out var item), $"Missing path {path}");
            Assert.True(item.TryGetProperty(method, out var operation), $"Missing {method} {path}");
            Assert.True(operation.GetProperty("description").GetString()!.Length > 40, $"Description too short on {method} {path}");
            Assert.True(operation.GetProperty("responses").TryGetProperty("404", out _), $"No 404 on {method} {path}");
        }

        var types = document.GetProperty("components").GetProperty("schemas").GetProperty("PropertyType").GetProperty("enum");
        Assert.Contains("datetime", types.EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("multi_select", types.EnumerateArray().Select(e => e.GetString()));
    }
}
