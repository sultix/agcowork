using System.Text.Json;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Collections;
using Agcowork.Application.Records;
using Agcowork.Application.Spaces;
using Agcowork.Application.Views;
using Agcowork.Application.Workspaces;
using ModelContextProtocol.Client;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>Every workspace tool on <c>/w/{slug}/mcp</c> with a happy path, plus the structured error cases agents rely on.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class WorkspaceToolsTests(AgcoworkApiFactory factory)
{
    private static readonly string[] StandardCollections = ["Analysen", "Aufgaben", "Entscheidungen", "Komponenten"];
    private static readonly string[] StatusField = ["status"];
    private static readonly string[] UrgentPriorities = ["high", "critical"];
    private static readonly string[] RecordCreated = ["record.created"];

    /// <summary>A fresh workspace from the standard template, owned by Alpha's owner, and a client connected to it.</summary>
    private async Task<(WorkspaceDto Workspace, McpClient Client)> ConnectAsync()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(t.AlphaOwner.Client, "mcp", "software-projekt");
        return (workspace, await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, workspace.Slug));
    }

    private static async Task<CollectionSummaryDto> CollectionAsync(McpClient client, string name)
    {
        var overview = await McpClientFixture.OkAsync<WorkspaceOverviewDto>(client, "workspace_overview");
        return overview.Spaces.SelectMany(s => s.Collections).Single(c => c.Name == name);
    }

    private static Task<RecordDto> CreateTaskAsync(McpClient client, Guid collectionId, string title = "Aufgabe") =>
        McpClientFixture.OkAsync<RecordDto>(client, "record_create", new { collectionId, title, properties = new { status = "open", priority = "high" } });

    [Fact]
    public async Task WorkspaceOverview_StandardTemplate_SpacesAndCollectionsWithDescriptionsAndCounts()
    {
        var (workspace, client) = await ConnectAsync();
        await using var connection = client;

        var result = await McpClientFixture.CallAsync(client, "workspace_overview");
        var overview = McpClientFixture.Ok(result).Deserialize<WorkspaceOverviewDto>(AgcoworkApiFactory.Json)!;

        Assert.Equal(workspace.Id, overview.Workspace.Id);
        Assert.Equal(["Produkt", "Technik", "Notizen"], overview.Spaces.Select(s => s.Name));
        var collections = overview.Spaces.SelectMany(s => s.Collections).ToList();
        Assert.Equal(StandardCollections, collections.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.All(collections, c => Assert.True(c.Description.Length > 40));
        Assert.All(collections, c => Assert.Equal(0, c.RecordCount));
        Assert.All(overview.Spaces, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));

        var text = McpClientFixture.Text(result);
        Assert.Contains("Entscheidungen", text, StringComparison.Ordinal);
        Assert.Contains(collections[0].Id.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpaceCreate_ThenSpacesList_ContainsIt()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;

        var space = await McpClientFixture.OkAsync<SpaceDto>(client, "space_create", new { name = "Betrieb", description = "Runbooks und Incidents." });
        var spaces = await McpClientFixture.OkAsync<List<SpaceDto>>(client, "spaces_list");

        Assert.Contains(spaces, s => s.Id == space.Id && s.Description == "Runbooks und Incidents.");
    }

    [Fact]
    public async Task SpaceCreate_EmptyName_StructuredValidationError()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;

        var problem = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "space_create", new { name = "" }), 400, "validation_failed");
        Assert.Equal("name", problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task CollectionDescribe_Decisions_SchemaJsonSchemaTemplatesViewsAndExample()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var decisions = await CollectionAsync(client, "Entscheidungen");

        var result = await McpClientFixture.CallAsync(client, "collection_describe", new { collectionId = decisions.Id });
        var described = McpClientFixture.Ok(result);

        Assert.Equal("Entscheidungen", described.GetProperty("collection").GetProperty("name").GetString());
        var statusEnum = described.GetProperty("jsonSchema").GetProperty("properties").GetProperty("status").GetProperty("enum");
        Assert.Contains("accepted", statusEnum.EnumerateArray().Select(e => e.GetString()));
        Assert.True(described.GetProperty("templates").GetArrayLength() >= 1);
        Assert.Contains(described.GetProperty("views").EnumerateArray(), v => v.GetProperty("type").GetString() == "table");
        Assert.Equal(JsonValueKind.Object, described.GetProperty("example").ValueKind);

        var text = McpClientFixture.Text(result);
        Assert.Contains("`status`", text, StringComparison.Ordinal);
        Assert.Contains($"agcowork://collection/{decisions.Id}/schema", text, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\":false", text, StringComparison.Ordinal);
        Assert.Contains("accepted", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectionCreate_ThenUpdateSchema_AddsProperty()
    {
        var (workspace, client) = await ConnectAsync();
        await using var connection = client;
        var overview = await McpClientFixture.OkAsync<WorkspaceOverviewDto>(client, "workspace_overview");
        var notes = overview.Spaces.Single(s => s.Name == "Notizen");

        var collection = await McpClientFixture.OkAsync<CollectionDto>(client, "collection_create", new
        {
            spaceId = notes.Id,
            name = "Meetings",
            description = "Ein Protokoll pro Meeting mit Datum und Teilnehmern.",
            schema = new object[] { new { key = "date", name = "Datum", type = "date" } },
        });
        Assert.Equal(["title", "date"], collection.Schema.Select(p => p.Key));

        var schema = await McpClientFixture.OkAsync<CollectionSchemaDto>(client, "collection_update_schema", new
        {
            collectionId = collection.Id,
            add = new object[] { new { key = "kind", name = "Art", type = "select", options = new[] { new { value = "daily" }, new { value = "review" } } } },
            expectedSchemaVersion = collection.SchemaVersion,
        });
        Assert.Equal(collection.SchemaVersion + 1, schema.SchemaVersion);
        Assert.Contains(schema.Properties, p => p.Key == "kind");
        Assert.NotEqual(Guid.Empty, workspace.Id);
    }

    [Fact]
    public async Task CollectionCreate_UnknownPropertyType_ErrorsWithAllowed()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var overview = await McpClientFixture.OkAsync<WorkspaceOverviewDto>(client, "workspace_overview");

        var problem = McpClientFixture.Error(
            await McpClientFixture.CallAsync(client, "collection_create", new
            {
                spaceId = overview.Spaces[0].Id,
                name = "Kaputt",
                description = "Hat einen unbekannten Typ.",
                schema = new object[] { new { key = "x", name = "X", type = "string" } },
            }),
            400,
            "validation_failed");

        var field = problem.GetProperty("errors").EnumerateArray().Single(e => e.GetProperty("field").GetString() == "schema[0].type");
        Assert.Contains("text", field.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task RecordCreate_UnknownAndInvalidProperties_ErrorsWithAllowed()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");

        var result = await McpClientFixture.CallAsync(client, "record_create", new
        {
            collectionId = tasks.Id,
            title = "Falsch",
            properties = new { colour = "red", status = "later" },
        });

        var problem = McpClientFixture.Error(result, 400, "validation_failed");
        var errors = problem.GetProperty("errors").EnumerateArray().ToList();
        Assert.Contains(errors, e => e.GetProperty("field").GetString() == "properties.colour" && e.GetProperty("code").GetString() == "unknown_property");
        var status = errors.Single(e => e.GetProperty("field").GetString() == "properties.status");
        Assert.Equal("not_in_options", status.GetProperty("code").GetString());
        Assert.Contains("in_progress", status.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("allowed:", McpClientFixture.Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordCreate_SameIdempotencyKey_ReturnsFirstRecord()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");
        var key = Guid.NewGuid().ToString();

        var first = await McpClientFixture.OkAsync<RecordDto>(client, "record_create", new { collectionId = tasks.Id, title = "Einmal", idempotencyKey = key });
        var second = await McpClientFixture.OkAsync<RecordDto>(client, "record_create", new { collectionId = tasks.Id, title = "Zweimal", idempotencyKey = key });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Einmal", second.Title);
    }

    [Fact]
    public async Task RecordCreate_MissingRequiredArgumentOfAppend_StructuredRequiredError()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");
        var record = await CreateTaskAsync(client, tasks.Id);

        var problem = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "record_append", new { recordId = record.Id }), 400, "validation_failed");

        var field = problem.GetProperty("errors")[0];
        Assert.Equal("markdown", field.GetProperty("field").GetString());
        Assert.Equal("required", field.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RecordGet_DefaultMarkdownWithFrontmatter_JsonOnRequest()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");
        var record = await CreateTaskAsync(client, tasks.Id, "Lesen");

        var markdown = await McpClientFixture.CallAsync(client, "record_get", new { recordId = record.Id });
        var text = McpClientFixture.Text(markdown);
        Assert.StartsWith("---\n", text, StringComparison.Ordinal);
        Assert.Contains($"id: {record.Id}", text, StringComparison.Ordinal);
        Assert.Contains("title: Lesen", text, StringComparison.Ordinal);
        Assert.Contains("revision: 1", text, StringComparison.Ordinal);
        Assert.Contains("## Ziel", text, StringComparison.Ordinal);
        Assert.Equal(text, McpClientFixture.Ok(markdown).GetProperty("markdown").GetString());

        var json = await McpClientFixture.OkAsync<RecordDto>(client, "record_get", new { recordId = record.Id, format = "json", includeHistory = 5 });
        Assert.Equal(record.Id, json.Id);
        Assert.Contains("## Ziel", json.Body, StringComparison.Ordinal);
        Assert.Single(json.History!);
    }

    [Fact]
    public async Task RecordGet_InvalidFormat_NotInOptionsWithAllowed()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;

        var problem = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "record_get", new { recordId = Guid.NewGuid(), format = "yaml" }), 400, "validation_failed");
        var field = problem.GetProperty("errors")[0];
        Assert.Equal("format", field.GetProperty("field").GetString());
        Assert.Equal(["markdown", "json"], field.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task RecordGet_UnknownId_NotFoundWithoutDetail()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;

        var problem = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "record_get", new { recordId = Guid.NewGuid() }), 404, "not_found");
        Assert.False(problem.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task RecordUpdate_StaleExpectedRevision_RevisionMismatchWithCurrentRevision()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");
        var record = await CreateTaskAsync(client, tasks.Id);

        var updated = await McpClientFixture.OkAsync<RecordDto>(client, "record_update", new
        {
            recordId = record.Id,
            properties = new { status = "in_progress" },
            expectedRevision = record.Revision,
            reason = "Begonnen",
        });
        Assert.Equal(record.Revision + 1, updated.Revision);

        var result = await McpClientFixture.CallAsync(client, "record_update", new { recordId = record.Id, title = "Neu", expectedRevision = record.Revision });
        var problem = McpClientFixture.Error(result, 409, "revision_mismatch");
        Assert.Equal(updated.Revision, problem.GetProperty("currentRevision").GetInt32());
        Assert.Contains("status", problem.GetProperty("changedKeys").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task RecordAppend_Archive_History_InOrder()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");
        var record = await CreateTaskAsync(client, tasks.Id);

        var appended = await McpClientFixture.OkAsync<RecordDto>(client, "record_append", new { recordId = record.Id, markdown = "- erster Eintrag", section = "Log" });
        Assert.Equal(2, appended.Revision);
        var archived = await McpClientFixture.OkAsync<RecordDto>(client, "record_archive", new { recordId = record.Id, reason = "Erledigt" });
        Assert.NotNull(archived.ArchivedAt);
        McpClientFixture.Error(await McpClientFixture.CallAsync(client, "record_archive", new { recordId = record.Id }), 409, "record_archived");

        var history = McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "history_get", new { recordId = record.Id }));
        Assert.Equal(["archived", "appended", "created"], history.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("kind").GetString()));

        var body = (await McpClientFixture.OkAsync<RecordDto>(client, "record_get", new { recordId = record.Id, format = "json" })).Body!;
        Assert.Contains("## Log\n", body, StringComparison.Ordinal);
        Assert.Contains("- erster Eintrag", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordsList_And_RecordsQuery_FilterSortAndCount()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");
        await CreateTaskAsync(client, tasks.Id, "B offen");
        await CreateTaskAsync(client, tasks.Id, "A offen");
        await McpClientFixture.OkAsync<RecordDto>(client, "record_create", new { collectionId = tasks.Id, title = "C fertig", properties = new { status = "done" } });

        var list = McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "records_list", new { collectionId = tasks.Id, limit = 2 }));
        Assert.Equal(2, list.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.String, list.GetProperty("nextCursor").ValueKind);

        var result = await McpClientFixture.CallAsync(client, "records_query", new
        {
            collectionId = tasks.Id,
            filter = new { prop = "status", op = "eq", value = "open" },
            sorts = new[] { new { prop = "title", dir = "asc" } },
            fields = StatusField,
            includeCount = true,
        });
        var page = McpClientFixture.Ok(result);
        Assert.Equal(["A offen", "B offen"], page.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("title").GetString()));
        Assert.Equal(2, page.GetProperty("count").GetInt32());
        Assert.Contains("status=open", McpClientFixture.Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordsQuery_InvalidOperator_ErrorsOnPathWithAllowed()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");

        var problem = McpClientFixture.Error(
            await McpClientFixture.CallAsync(client, "records_query", new { collectionId = tasks.Id, filter = new { and = new[] { new { prop = "status", op = "gt", value = "open" } } } }),
            400,
            "validation_failed");

        var field = problem.GetProperty("errors")[0];
        Assert.Equal("filter.and[0].op", field.GetProperty("field").GetString());
        Assert.Contains("eq", field.GetProperty("allowed").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task RecordsQuery_FilterNotAnObject_InvalidFormat()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");

        var problem = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "records_query", new { collectionId = tasks.Id, filter = "status = open" }), 400, "validation_failed");
        Assert.Equal("filter", problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task ViewCreate_Board_GroupedByStatus()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");

        var view = await McpClientFixture.OkAsync<ViewDto>(client, "view_create", new
        {
            collectionId = tasks.Id,
            type = "board",
            name = "Board nach Status (MCP)",
            groupBy = "status",
            filter = new { prop = "priority", op = "in", value = UrgentPriorities },
        });

        Assert.Equal("board", view.Type);
        Assert.Equal("status", view.GroupBy);
        Assert.NotNull(view.Filter);

        var invalid = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "view_create", new { collectionId = tasks.Id, type = "timeline", name = "X" }), 400, "validation_failed");
        Assert.Equal("type", invalid.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task ActivityRecent_AfterCreate_NewestFirst()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");
        var record = await CreateTaskAsync(client, tasks.Id, "Aktivität");

        var result = await McpClientFixture.CallAsync(client, "activity_recent", new { collectionId = tasks.Id, types = RecordCreated });
        var first = McpClientFixture.Ok(result).GetProperty("items")[0];

        Assert.Equal("record.created", first.GetProperty("type").GetString());
        Assert.Equal(record.Id, first.GetProperty("targetId").GetGuid());
        Assert.Equal("Aktivität", first.GetProperty("targetTitle").GetString());
        Assert.Contains("record.created", McpClientFixture.Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivityRecent_AfterRename_TargetTitleIsCurrentTitle()
    {
        var (_, client) = await ConnectAsync();
        await using var connection = client;
        var tasks = await CollectionAsync(client, "Aufgaben");
        var record = await CreateTaskAsync(client, tasks.Id, "Alter Name");
        await McpClientFixture.OkAsync<RecordDto>(client, "record_update", new { recordId = record.Id, title = "Neuer \"Name\" für Ä" });

        var result = await McpClientFixture.CallAsync(client, "activity_recent", new { collectionId = tasks.Id });
        var items = McpClientFixture.Ok(result).GetProperty("items").EnumerateArray().Where(e => e.GetProperty("targetId").GetGuid() == record.Id).ToList();

        Assert.Equal(2, items.Count);
        Assert.All(items, e => Assert.Equal("Neuer \"Name\" für Ä", e.GetProperty("targetTitle").GetString()));
        // JSON-quoted in the text: inner quotes escaped, umlauts kept.
        Assert.Contains("record \"Neuer \\\"Name\\\" für Ä\" ", McpClientFixture.Text(result), StringComparison.Ordinal);
    }
}
