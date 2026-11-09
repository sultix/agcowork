using System.Net;
using System.Net.Http.Json;
using Agcowork.Api.Tests.Collections;
using Agcowork.Api.Tests.Identity;
using Agcowork.Api.Tests.Mcp;
using Agcowork.Api.Tests.Tenancy;
using Agcowork.Application.Collections;
using Agcowork.Application.Records;
using Agcowork.Application.Workspaces;

namespace Agcowork.Api.Tests.Records;

/// <summary>Creating a record without the default template (AP-042) over REST and MCP.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class SkipTemplateTests(AgcoworkApiFactory factory)
{
    private static readonly string[] DbTag = ["db"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestAgent Owner, WorkspaceDto Workspace, CollectionDto Collection)> WithDefaultTemplateAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "skip");
        var (_, collection) = await RecordsApi.SeedCollectionAsync(owner.Client, workspace.Slug);
        using var response = await owner.Client.PostAsJsonAsync(
            TenancyFixture.Path(workspace.Slug, $"/collections/{collection.Id}/templates"),
            new { name = "Standard", propertiesPreset = new { status = "doing", tags = DbTag }, bodyMarkdown = "## Kontext\n", isDefault = true },
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (owner, workspace, collection);
    }

    [Fact]
    public async Task Rest_SkipTemplate_StartsEmpty_WithoutFlagDefaultTemplateApplies()
    {
        var (owner, workspace, collection) = await WithDefaultTemplateAsync();

        var skipped = await RecordsApi.CreateAsync(owner.Client, workspace.Slug, new { collectionId = collection.Id, title = "Leer", skipTemplate = true });
        var withDefault = await RecordsApi.CreateAsync(owner.Client, workspace.Slug, new { collectionId = collection.Id, title = "Mit" });

        Assert.Equal("todo", skipped.Properties["status"].GetString());
        Assert.False(skipped.Properties.ContainsKey("tags"));
        Assert.Equal(string.Empty, (await RecordsApi.GetAsync(owner.Client, workspace.Slug, skipped.Id)).Body);
        Assert.Equal("doing", withDefault.Properties["status"].GetString());
        Assert.Equal("## Kontext\n", (await RecordsApi.GetAsync(owner.Client, workspace.Slug, withDefault.Id)).Body);
    }

    [Fact]
    public async Task Rest_SkipTemplateWithTemplateId_BadRequestOnSkipTemplate()
    {
        var (owner, workspace, collection) = await WithDefaultTemplateAsync();
        var template = await SchemaApi.CreateTemplateAsync(owner.Client, workspace.Slug, collection.Id, "Zweite");

        using var response = await owner.Client.PostAsJsonAsync(
            TenancyFixture.Path(workspace.Slug, "/records"), new { collectionId = collection.Id, title = "X", templateId = template.Id, skipTemplate = true }, Ct);

        var problem = await ProblemAssert.IsProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal("conflict", ProblemAssert.Field(problem, "skipTemplate").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Rest_IdempotencyKey_OtherSkipTemplate_Conflict_SameSkipTemplate_Replays()
    {
        var (owner, workspace, collection) = await WithDefaultTemplateAsync();
        var key = Guid.NewGuid().ToString();

        async Task<HttpResponseMessage> PostAsync(bool skip)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(TenancyFixture.Path(workspace.Slug, "/records"), UriKind.Relative))
            {
                Content = JsonContent.Create(new { collectionId = collection.Id, title = "Einmal", skipTemplate = skip }),
            };
            request.Headers.Add("Idempotency-Key", key);
            return await owner.Client.SendAsync(request, Ct);
        }

        using var first = await PostAsync(true);
        using var retry = await PostAsync(true);
        using var changed = await PostAsync(false);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal((await first.Content.ReadFromJsonAsync<RecordDto>(AgcoworkApiFactory.Json, Ct))!.Id, (await retry.Content.ReadFromJsonAsync<RecordDto>(AgcoworkApiFactory.Json, Ct))!.Id);
        await ProblemAssert.IsProblemAsync(changed, HttpStatusCode.Conflict, "idempotency_key_reused");
    }

    [Fact]
    public async Task Mcp_SkipTemplate_StartsEmpty_ConflictWithTemplateId_ToolListsArgument()
    {
        var (owner, workspace, collection) = await WithDefaultTemplateAsync();
        var template = await SchemaApi.CreateTemplateAsync(owner.Client, workspace.Slug, collection.Id, "Zweite");
        await using var client = await McpClientFixture.ConnectAsync(factory, owner.Key, workspace.Slug);

        var skipped = await McpClientFixture.OkAsync<RecordDto>(client, "record_create", new { collectionId = collection.Id, title = "Leer", skipTemplate = true });
        var withDefault = await McpClientFixture.OkAsync<RecordDto>(client, "record_create", new { collectionId = collection.Id, title = "Mit" });
        var conflict = McpClientFixture.Error(
            await McpClientFixture.CallAsync(client, "record_create", new { collectionId = collection.Id, title = "X", templateId = template.Id, skipTemplate = true }), 400, "validation_failed");

        Assert.False(skipped.Properties.ContainsKey("tags"));
        Assert.Equal("doing", withDefault.Properties["status"].GetString());
        Assert.Contains(conflict.GetProperty("errors").EnumerateArray(), e => e.GetProperty("field").GetString() == "skipTemplate" && e.GetProperty("code").GetString() == "conflict");
        var tool = (await client.ListToolsAsync(cancellationToken: Ct)).Single(t => t.Name == "record_create");
        Assert.Contains("skipTemplate", tool.JsonSchema.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("no template preset values or body, even if the collection has a default template; schema defaults still apply", tool.JsonSchema.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mcp_RecordTexts_TitleWithMarkdown_AreEscaped()
    {
        var (owner, workspace, collection) = await WithDefaultTemplateAsync();
        await using var client = await McpClientFixture.ConnectAsync(factory, owner.Key, workspace.Slug);
        const string title = "**fett** [x](y)\n# H";

        var created = await McpClientFixture.CallAsync(client, "record_create", new { collectionId = collection.Id, title });
        var list = await McpClientFixture.CallAsync(client, "records_list", new { collectionId = collection.Id });

        var id = McpClientFixture.Ok(created).GetProperty("id").GetGuid();
        var quoted = "\"**fett** [x](y)\\n# H\"";
        Assert.StartsWith($"Created: {quoted} (id {id}, revision 1)", McpClientFixture.Text(created), StringComparison.Ordinal);
        Assert.Contains($"- {quoted} (id {id}, revision 1)", McpClientFixture.Text(list), StringComparison.Ordinal);
        Assert.DoesNotContain("\n# H", McpClientFixture.Text(list), StringComparison.Ordinal);
    }
}
