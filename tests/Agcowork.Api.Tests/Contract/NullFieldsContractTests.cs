using System.Text.Json;
using Agcowork.Api.Tests.Comments;
using Agcowork.Api.Tests.Mcp;
using Agcowork.Api.Tests.Tenancy;

namespace Agcowork.Api.Tests.Contract;

/// <summary>
/// The wire matches the OpenAPI contract (AP-033): members the schema lists as <c>required</c> are present, also when null
/// (<c>"readAt": null</c>), so clients can test <c>=== null</c>. Optional members (e.g. <c>replies</c> of a reply) stay
/// omitted when null. Checked for a sample of endpoints with nullable members, plus the same rule for MCP structured content.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class NullFieldsContractTests(AgcoworkApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A word no other test uses (the search index is shared).</summary>
    private static string Word() => $"vertrag{Guid.NewGuid():N}"[..19];

    private sealed record Sample(string Template, string Path, JsonElement Body);

    /// <summary>Workspace with an owner and a mentioned editor, an open thread with one reply, and a human for <c>/me</c>.</summary>
    private async Task<(string Slug, TestAgent Owner, TestAgent Editor, Guid RecordId, string Word)> ArrangeAsync()
    {
        var owner = await factory.CreateAgentAsync(["workspaces:create"], cancellationToken: Ct);
        var workspace = await TenancyFixture.CreateWorkspaceAsync(owner.Client, "null");
        var editor = await factory.CreateAgentAsync(cancellationToken: Ct);
        await TenancyFixture.AddMemberAsync(owner.Client, workspace.Slug, editor.ActorId, "editor");
        var word = Word();
        var record = await CommentsApi.SeedRecordAsync(owner.Client, workspace.Slug, $"Vertrag {word}");
        var root = await CommentsApi.AddAsync(owner.Client, workspace.Slug, record.Id, $"Bitte prüfen, {CommentsApi.Mention(editor.ActorId)}");
        await CommentsApi.AddAsync(editor.Client, workspace.Slug, record.Id, "Mache ich.", root.Id);
        return (workspace.Slug, owner, editor, record.Id, word);
    }

    private static async Task<Sample> GetAsync(HttpClient client, string template, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {text}");
        return new Sample(template, path, JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<IReadOnlyList<Sample>> SamplesAsync()
    {
        var (slug, owner, editor, recordId, word) = await ArrangeAsync();
        using var human = await TenancyFixture.CreateHumanAsync(factory);
        const string W = "/api/v1/w/{workspaceSlug}";
        return
        [
            await GetAsync(editor.Client, W + "/mentions", TenancyFixture.Path(slug, "/mentions")),
            await GetAsync(owner.Client, W + "/records/{recordId}/comments", TenancyFixture.Path(slug, $"/records/{recordId}/comments")),
            await GetAsync(owner.Client, W + "/activity", TenancyFixture.Path(slug, "/activity")),
            await GetAsync(owner.Client, W + "/search", TenancyFixture.Path(slug, "/search?q=" + Uri.EscapeDataString(word))),
            await GetAsync(human.Client, "/api/v1/me", "/api/v1/me"),
        ];
    }

    private static Sample Find(IReadOnlyList<Sample> samples, string templateSuffix) =>
        samples.Single(s => s.Template.EndsWith(templateSuffix, StringComparison.Ordinal));

    [Fact]
    public async Task NullableRequiredMembers_SampleEndpoints_PresentAsNull()
    {
        var samples = await SamplesAsync();

        // GET /mentions: an unread mention has "readAt": null; the only page has "nextCursor": null.
        var mentions = Find(samples, "/mentions").Body;
        var mention = Assert.Single(mentions.GetProperty("items").EnumerateArray());
        AssertNull(mention, "readAt");
        AssertNull(mention, "parentCommentId");
        AssertNull(mentions, "nextCursor");

        // GET /records/{id}/comments: the root has "parentCommentId": null and, while open, "resolvedAt": null.
        var thread = Assert.Single(Find(samples, "/comments").Body.GetProperty("threads").EnumerateArray());
        AssertNull(thread, "parentCommentId");
        AssertNull(thread, "resolvedAt");
        AssertNull(thread, "resolvedBy");
        AssertNull(thread, "editedAt");
        AssertNull(thread, "deletedAt");
        // Optional members stay omitted when null: a reply carries no replies.
        var reply = Assert.Single(thread.GetProperty("replies").EnumerateArray());
        Assert.Equal(thread.GetProperty("id").GetGuid(), reply.GetProperty("parentCommentId").GetGuid());
        Assert.False(reply.TryGetProperty("replies", out _), reply.GetRawText());
        Assert.False(reply.TryGetProperty("omittedReplies", out _), reply.GetRawText());

        // GET /activity: member events have no title ("targetTitle": null) and no space or collection.
        var memberAdded = Find(samples, "/activity").Body.GetProperty("items").EnumerateArray()
            .First(e => e.GetProperty("type").GetString() == "member.added");
        AssertNull(memberAdded, "targetTitle");
        AssertNull(memberAdded, "spaceId");
        AssertNull(memberAdded, "collectionId");

        // GET /search: the last page has "nextCursor": null.
        var search = Find(samples, "/search").Body;
        Assert.Single(search.GetProperty("items").EnumerateArray());
        AssertNull(search, "nextCursor");

        // GET /me with a session cookie: "apiKey": null.
        AssertNull(Find(samples, "/me").Body, "apiKey");
    }

    [Fact]
    public async Task RequiredMembers_SampleResponses_PresentEverywhere()
    {
        var samples = await SamplesAsync();
        using var client = factory.CreateClient();
        await using var stream = await client.GetStreamAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: Ct);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        foreach (var sample in samples)
        {
            var schema = document.RootElement.GetProperty("paths").GetProperty(sample.Template).GetProperty("get")
                .GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
            var checkedMembers = new List<string>();

            AssertRequiredPresent(schemas, schema, sample.Body, sample.Path, checkedMembers);

            // The walk reached the nullable members this sample is about (and did not stop at the top level).
            Assert.True(checkedMembers.Count > 5, $"{sample.Path}: only {checkedMembers.Count} required members checked.");
        }
    }

    [Fact]
    public async Task NullableRequiredMembers_McpStructuredContent_PresentAsNull()
    {
        var (slug, owner, editor, recordId, _) = await ArrangeAsync();
        await using var answerer = await McpClientFixture.ConnectAsync(factory, editor.Key, slug);
        await using var asker = await McpClientFixture.ConnectAsync(factory, owner.Key, slug);

        var inbox = McpClientFixture.Ok(await McpClientFixture.CallAsync(answerer, "mentions_list", new { unreadOnly = true }));
        AssertNull(Assert.Single(inbox.GetProperty("items").EnumerateArray()), "readAt");
        AssertNull(inbox, "nextCursor");

        var comments = McpClientFixture.Ok(await McpClientFixture.CallAsync(asker, "comments_list", new { recordId }));
        var thread = Assert.Single(comments.GetProperty("threads").EnumerateArray());
        AssertNull(thread, "parentCommentId");
        AssertNull(thread, "resolvedAt");
        Assert.False(Assert.Single(thread.GetProperty("replies").EnumerateArray()).TryGetProperty("replies", out _));
    }

    private static void AssertNull(JsonElement owner, string member)
    {
        Assert.True(owner.TryGetProperty(member, out var value), $"'{member}' is missing: {owner.GetRawText()}");
        Assert.Equal(JsonValueKind.Null, value.ValueKind);
    }

    /// <summary>Walks <paramref name="value"/> along its schema and asserts that every required member is present.</summary>
    private static void AssertRequiredPresent(JsonElement schemas, JsonElement schema, JsonElement value, string path, List<string> checkedMembers)
    {
        schema = Resolve(schemas, schema);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        foreach (var keyword in (string[])["oneOf", "anyOf", "allOf"])
        {
            if (schema.TryGetProperty(keyword, out var variants))
            {
                foreach (var variant in variants.EnumerateArray().Select(v => Resolve(schemas, v)).Where(v => !IsNullType(v)))
                {
                    AssertRequiredPresent(schemas, variant, value, path, checkedMembers);
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                {
                    Assert.True(value.TryGetProperty(name, out _), $"{path}: required member '{name}' is missing in {value.GetRawText()}");
                    checkedMembers.Add($"{path}.{name}");
                }
            }

            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (value.TryGetProperty(property.Name, out var member))
                    {
                        AssertRequiredPresent(schemas, property.Value, member, $"{path}.{property.Name}", checkedMembers);
                    }
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                AssertRequiredPresent(schemas, items, item, $"{path}[{index++}]", checkedMembers);
            }
        }
    }

    private static JsonElement Resolve(JsonElement schemas, JsonElement schema)
    {
        while (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$ref", out var reference))
        {
            const string Prefix = "#/components/schemas/";
            var target = reference.GetString()!;
            Assert.StartsWith(Prefix, target, StringComparison.Ordinal);
            schema = schemas.GetProperty(target[Prefix.Length..]);
        }

        return schema;
    }

    private static bool IsNullType(JsonElement schema) =>
        schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "null";
}
