using System.Net;
using System.Net.Http.Json;
using System.Text;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Records;
using Agcowork.Application.Workspaces;
using Agcowork.Domain.Records;
using Record = Agcowork.Domain.Records.Record;

namespace Agcowork.Api.Tests.Records;

[Collection(ApiTestGroup.Name)]
public sealed class RecordsEndpointsTests(AgcoworkApiFactory factory)
{
    private static readonly string[] Api = ["api"];
    private static readonly string[] Db = ["db"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestAgent Owner, WorkspaceDto Workspace)> OwnWorkspaceAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        return (owner, await TenancyFixture.CreateWorkspaceAsync(owner.Client, "rec"));
    }

    private async Task<TestAgent> MemberAsync(TestAgent owner, WorkspaceDto workspace, string role)
    {
        var agent = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, agent.ActorId, role);
        return agent;
    }

    private static string Path(WorkspaceDto workspace, string relative) => TenancyFixture.Path(workspace.Slug, relative);

    [Fact]
    public async Task Writer_CreatesWithAndWithoutTemplate_GetsJsonAndMarkdown()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (space, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var template = await SchemaApi.CreateTemplateAsync(owner.Client, workspace.Slug, collection.Id, "Bug", new { status = "doing", tags = Db });
        var writer = await MemberAsync(owner, workspace, "editor");

        using var create = await writer.Client.PostAsJsonAsync(Path(workspace, "/records"), new { collectionId = collection.Id, title = "Ohne Vorlage", properties = new { tags = Api }, body = "Text" }, Ct);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var plain = (await create.Content.ReadFromJsonAsync<RecordDto>(AgcoworkApiFactory.Json, Ct))!;
        Assert.Equal(Path(workspace, $"/records/{plain.Id}"), create.Headers.Location?.OriginalString);
        Assert.Equal((space.Id, 1, writer.ActorId, "todo"), (plain.SpaceId, plain.Revision, plain.CreatedBy, plain.Properties["status"].GetString()));
        var fromTemplate = await RecordsApi.CreateAsync(writer.Client, workspace.Slug, new { collectionId = collection.Id, title = "Mit Vorlage", templateId = template.Id });
        Assert.Equal(("doing", "[\"db\"]"), (fromTemplate.Properties["status"].GetString(), fromTemplate.Properties["tags"].GetRawText()));

        var json = await RecordsApi.GetAsync(writer.Client, workspace.Slug, fromTemplate.Id, "?includeBody=true&includeHistory=3");
        Assert.Equal("## Kontext\n", json.Body);
        Assert.Equal(RevisionKind.Created, Assert.Single(json.History!).Kind);
        Assert.Null((await RecordsApi.GetAsync(writer.Client, workspace.Slug, plain.Id, string.Empty)).Body);

        using var markdown = await writer.Client.GetAsync(new Uri(Path(workspace, $"/records/{plain.Id}?format=markdown"), UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, markdown.StatusCode);
        Assert.Equal("text/markdown", markdown.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", markdown.Content.Headers.ContentType?.CharSet);
        var text = await markdown.Content.ReadAsStringAsync(Ct);
        Assert.StartsWith($"---\nid: {plain.Id}\ncollection: Aufgaben\n", text, StringComparison.Ordinal);
        Assert.Contains("\ntitle: Ohne Vorlage\nstatus: todo\ntags:\n  - api\nrevision: 1\ncreated_at: ", text, StringComparison.Ordinal);
        Assert.Contains($"\ncreated_by: {writer.ActorId}\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n---\nText", text, StringComparison.Ordinal);

        using var badFormat = await writer.Client.GetAsync(new Uri(Path(workspace, $"/records/{plain.Id}?format=yaml"), UriKind.Relative), Ct);
        var problem = await ProblemAssert.IsProblemAsync(badFormat, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(2, ProblemAssert.Field(problem, "format").GetProperty("allowed").GetArrayLength());
    }

    [Fact]
    public async Task Create_InvalidProperties_400WithAllowed()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);

        using var response = await owner.Client.PostAsJsonAsync(Path(workspace, "/records"), new { collectionId = collection.Id, title = "T", properties = new { status = "maybe", prio = 1 } }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        var status = ProblemAssert.Field(problem, "properties.status");
        Assert.Equal("not_in_options", status.GetProperty("code").GetString());
        Assert.Equal(3, status.GetProperty("allowed").GetArrayLength());
        Assert.Equal("unknown_property", ProblemAssert.Field(problem, "properties.prio").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Patch_ExpectedRevision_MatchesOrMismatches_WithoutLastWriterWins()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id);
        var path = Path(workspace, $"/records/{record.Id}");

        using var ok = await owner.Client.PatchAsJsonAsync(path, new { properties = new { status = "doing" }, expectedRevision = 1, reason = "Begonnen" }, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(2, (await ok.Content.ReadFromJsonAsync<RecordDto>(AgcoworkApiFactory.Json, Ct))!.Revision);

        using var stale = await owner.Client.PatchAsJsonAsync(path, new { properties = new { status = "done" }, expectedRevision = 1 }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(stale, HttpStatusCode.Conflict, "revision_mismatch");
        Assert.Equal(2, problem.GetProperty("currentRevision").GetInt32());
        Assert.Equal(["status"], problem.GetProperty("changedKeys").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal("expectedRevision", ProblemAssert.Field(problem, "expectedRevision").GetProperty("field").GetString());

        using var blind = await owner.Client.PatchAsJsonAsync(path, new { properties = new { status = "done", tags = (string[]?)null } }, Ct);
        Assert.Equal(HttpStatusCode.OK, blind.StatusCode);
        Assert.Equal("done", (await RecordsApi.GetAsync(owner.Client, workspace.Slug, record.Id)).Properties["status"].GetString());
    }

    [Fact]
    public async Task Append_SectionAndEnd_BodyLimit_HistoryAndRevision()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, body: "## Log\n\n## Fazit\n");
        var path = Path(workspace, $"/records/{record.Id}");

        using (var append = await owner.Client.PostAsJsonAsync($"{path}/append", new { markdown = "- erster Eintrag", section = "Log", reason = "Lauf 1" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, append.StatusCode);
        }

        using (var append = await owner.Client.PostAsJsonAsync($"{path}/append", new { markdown = "Ende." }, Ct))
        {
            Assert.Equal(3, (await append.Content.ReadFromJsonAsync<RecordDto>(AgcoworkApiFactory.Json, Ct))!.Revision);
        }

        Assert.Equal("## Log\n\n- erster Eintrag\n\n## Fazit\n\nEnde.\n", (await RecordsApi.GetAsync(owner.Client, workspace.Slug, record.Id)).Body);

        var history = await owner.Client.GetFromJsonAsync<Page<RevisionSummaryDto>>($"{path}/history", AgcoworkApiFactory.Json, Ct);
        Assert.Equal([(3, RevisionKind.Appended, (string?)null), (2, RevisionKind.Appended, "Lauf 1"), (1, RevisionKind.Created, null)], history!.Items.Select(r => (r.No, r.Kind, r.Reason)));
        Assert.All(history.Items, r => Assert.Equal(owner.ActorId, r.ActorId));
        var revision = await owner.Client.GetFromJsonAsync<RevisionDto>($"{path}/revisions/2", AgcoworkApiFactory.Json, Ct);
        Assert.Equal("## Log\n\n- erster Eintrag\n\n## Fazit\n", revision!.Body);
        await TenancyFixture.AssertNotFoundAsync(owner.Client, HttpMethod.Get, $"{path}/revisions/9");

        var big = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, body: new string('a', Record.BodyMaxBytes - 5));
        using var tooLong = await owner.Client.PostAsJsonAsync(Path(workspace, $"/records/{big.Id}/append"), new { markdown = "more than five" }, Ct);
        var problem = await ProblemAssert.IsProblemAsync(tooLong, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("too_long", ProblemAssert.Field(problem, "markdown").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ArchiveRestore_ListHidesArchived_ConflictsOnRepeat()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id);
        var path = Path(workspace, $"/records/{record.Id}");
        var list = Path(workspace, $"/records?collectionId={collection.Id}");

        using (var archive = await owner.Client.PostAsync(new Uri($"{path}/archive", UriKind.Relative), null, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        }

        Assert.Empty((await owner.Client.GetFromJsonAsync<Page<RecordDto>>(list, AgcoworkApiFactory.Json, Ct))!.Items);
        Assert.Single((await owner.Client.GetFromJsonAsync<Page<RecordDto>>($"{list}&includeArchived=true", AgcoworkApiFactory.Json, Ct))!.Items);
        using (var patch = await owner.Client.PatchAsJsonAsync(path, new { title = "X" }, Ct))
        {
            await ProblemAssert.IsProblemAsync(patch, HttpStatusCode.Conflict, "record_archived");
        }

        using (var restore = await owner.Client.PostAsJsonAsync($"{path}/restore", new { reason = "Doch noch nötig" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        }

        using var again = await owner.Client.PostAsync(new Uri($"{path}/restore", UriKind.Relative), null, Ct);
        await ProblemAssert.IsProblemAsync(again, HttpStatusCode.Conflict, "record_not_archived");
        Assert.Single((await owner.Client.GetFromJsonAsync<Page<RecordDto>>(list, AgcoworkApiFactory.Json, Ct))!.Items);
    }

    [Fact]
    public async Task List_CursorPages_FreePagesBySpace()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (space, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        for (var i = 0; i < 3; i++)
        {
            await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, $"R{i}");
        }

        var page = await RecordsApi.CreateAsync(owner.Client, workspace.Slug, new { spaceId = space.Id, title = "Notiz" });

        var first = await owner.Client.GetFromJsonAsync<Page<RecordDto>>(Path(workspace, $"/records?collectionId={collection.Id}&limit=2"), AgcoworkApiFactory.Json, Ct);
        var second = await owner.Client.GetFromJsonAsync<Page<RecordDto>>(Path(workspace, $"/records?collectionId={collection.Id}&limit=2&cursor={first!.NextCursor}"), AgcoworkApiFactory.Json, Ct);
        var free = await owner.Client.GetFromJsonAsync<Page<RecordDto>>(Path(workspace, $"/records?spaceId={space.Id}"), AgcoworkApiFactory.Json, Ct);

        Assert.Equal(2, first.Items.Count);
        Assert.Single(second!.Items);
        Assert.Null(second.NextCursor);
        Assert.Empty(first.Items.Select(r => r.Id).Intersect(second.Items.Select(r => r.Id)));
        Assert.Equal([page.Id], free!.Items.Select(r => r.Id));
        using var neither = await owner.Client.GetAsync(new Uri(Path(workspace, "/records"), UriKind.Relative), Ct);
        await ProblemAssert.IsProblemAsync(neither, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task IdempotencyKey_SecondPost_SameRecord()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var key = Guid.NewGuid().ToString();

        async Task<RecordDto> PostAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Path(workspace, "/records"), UriKind.Relative))
            {
                Content = JsonContent.Create(new { collectionId = collection.Id, title = "Einmal" }),
            };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await owner.Client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<RecordDto>(AgcoworkApiFactory.Json, Ct))!;
        }

        var first = await PostAsync();
        var second = await PostAsync();

        Assert.Equal(first.Id, second.Id);
        var all = await owner.Client.GetFromJsonAsync<Page<RecordDto>>(Path(workspace, $"/records?collectionId={collection.Id}"), AgcoworkApiFactory.Json, Ct);
        Assert.Single(all!.Items);
    }

    [Fact]
    public async Task IdempotencyKey_SameContentOtherJsonSpelling_SameRecord()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var key = Guid.NewGuid().ToString();

        async Task<RecordDto> PostAsync(string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Path(workspace, "/records"), UriKind.Relative))
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await owner.Client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<RecordDto>(AgcoworkApiFactory.Json, Ct))!;
        }

        // Escaped and unescaped characters, as different clients and the MCP adapter send them.
        var first = await PostAsync($$"""{ "collectionId": "{{collection.Id}}", "title": "Café", "properties": { "status": "doing", "tags": ["db"] } }""");
        var retry = await PostAsync($$"""{"properties":{"tags":["db"],"status":"doing"},"title":"Café","collectionId":"{{collection.Id}}"}""");

        Assert.Equal(first.Id, retry.Id);
    }

    [Fact]
    public async Task IdempotencyKey_SameKeyDifferentContent_409_KeyReused()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var key = Guid.NewGuid().ToString();

        async Task<HttpResponseMessage> PostAsync(object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Path(workspace, "/records"), UriKind.Relative)) { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key);
            return await owner.Client.SendAsync(request, Ct);
        }

        using var first = await PostAsync(new { collectionId = collection.Id, title = "Einmal", body = "Text" });
        using var retry = await PostAsync(new { body = "Text", title = "Einmal", collectionId = collection.Id });
        using var changed = await PostAsync(new { collectionId = collection.Id, title = "Anders", body = "Text" });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        var problem = await changed.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(Ct);
        Assert.Equal("idempotency_key_reused", problem!["code"]!.GetValue<string>());
        Assert.Contains("Same key, different request", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        var all = await owner.Client.GetFromJsonAsync<Page<RecordDto>>(Path(workspace, $"/records?collectionId={collection.Id}"), AgcoworkApiFactory.Json, Ct);
        Assert.Single(all!.Items);
    }

    [Fact]
    public async Task Viewer_CannotWrite_403()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id);
        var viewer = await MemberAsync(owner, workspace, "viewer");
        var path = $"/records/{record.Id}";

        foreach (var (method, relative, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Post, "/records", new { collectionId = collection.Id, title = "T" }),
                     (HttpMethod.Patch, path, new { title = "T" }),
                     (HttpMethod.Post, $"{path}/append", new { markdown = "x" }),
                     (HttpMethod.Post, $"{path}/archive", null),
                     (HttpMethod.Post, $"{path}/restore", null),
                 })
        {
            using var response = await TenancyFixture.SendAsync(viewer.Client, method, Path(workspace, relative), body);
            var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
            Assert.Equal("requires write", problem.GetProperty("detail").GetString());
        }

        Assert.Equal(record.Id, (await RecordsApi.GetAsync(viewer.Client, workspace.Slug, record.Id)).Id);
    }

    [Fact]
    public async Task ArchivedWorkspace_Writes409_ReadsWork()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id);
        using (var archive = await owner.Client.PostAsync(new Uri(Path(workspace, "/archive"), UriKind.Relative), null, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        }

        var path = $"/records/{record.Id}";
        foreach (var (method, relative, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Post, "/records", new { collectionId = collection.Id, title = "T" }),
                     (HttpMethod.Patch, path, new { title = "T" }),
                     (HttpMethod.Post, $"{path}/append", new { markdown = "x" }),
                     (HttpMethod.Post, $"{path}/archive", null),
                     (HttpMethod.Post, $"{path}/restore", null),
                 })
        {
            using var response = await TenancyFixture.SendAsync(owner.Client, method, Path(workspace, relative), body);
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Conflict, "workspace_archived");
        }

        Assert.Equal(record.Id, (await RecordsApi.GetAsync(owner.Client, workspace.Slug, record.Id)).Id);
        using var markdown = await owner.Client.GetAsync(new Uri(Path(workspace, $"{path}?format=markdown"), UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, markdown.StatusCode);
    }

    [Fact]
    public async Task Markdown_IsUtf8()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, "Größe ändern", "Übersicht ✓");

        using var response = await owner.Client.GetAsync(new Uri(Path(workspace, $"/records/{record.Id}?format=markdown"), UriKind.Relative), Ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);

        Assert.Contains("title: Größe ändern\n", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.EndsWith("Übersicht ✓", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patch_BodyOver2MiB_400TooLong()
    {
        var (owner, workspace) = await OwnWorkspaceAsync();
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        var record = await RecordsApi.CreateInAsync(owner.Client, workspace.Slug, collection.Id, body: "klein");

        using var response = await owner.Client.PatchAsJsonAsync(Path(workspace, $"/records/{record.Id}"), new { body = new string('a', Record.BodyMaxBytes + 1) }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("too_long", ProblemAssert.Field(problem, "body").GetProperty("code").GetString());
        Assert.Equal(("klein", 1), ((await RecordsApi.GetAsync(owner.Client, workspace.Slug, record.Id)).Body, record.Revision));
    }
}
