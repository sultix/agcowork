using System.Net;
using Agcowork.Api.Tests.Records;
using Agcowork.Api.Tests.Tenancy;

namespace Agcowork.Api.Tests.Mcp;

/// <summary>
/// Every workspace tool against the isolation fixture (architecture §2a): outside Alpha the endpoint answers HTTP 404 before
/// any tool runs; cookies get 401. Foreign ids inside the own workspace are tool errors <c>not_found</c> without detail.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class McpIsolationTests(AgcoworkApiFactory factory)
{
    private static readonly string[] ReadScope = ["read"];
    private static readonly string[] TagsKey = ["tags"];

    public static TheoryData<string> Tools => [.. ToolsListTests.WorkspaceTools];

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task WorkspaceTool_OutsideMembership_TransportNotFound(string tool)
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (space, collection) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        var record = await RecordsApi.CreateInAsync(t.AlphaOwner.Client, t.Alpha.Slug, collection.Id);
        await using var alphaOwner = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        // Read-only or invalid arguments for the positive control, so the shared fixture is not changed.
        object arguments = tool switch
        {
            "workspace_overview" or "spaces_list" or "activity_recent" => new { },
            "space_create" => new { name = "" },
            "collection_describe" or "records_query" => new { collectionId = collection.Id },
            "collection_create" => new { spaceId = space.Id, name = "", description = "" },
            "collection_update_schema" => new { collectionId = collection.Id },
            "records_list" => new { collectionId = collection.Id },
            "record_create" => new { collectionId = collection.Id },
            "record_get" or "history_get" => new { recordId = record.Id },
            "record_update" => new { recordId = record.Id, title = "" },
            "record_append" => new { recordId = record.Id, markdown = "" },
            "record_archive" => new { recordId = Guid.NewGuid() },
            "view_create" => new { collectionId = collection.Id, type = "board", name = "" },
            "search" => new { query = "aufgabe" },
            _ => throw new InvalidOperationException($"No arguments for {tool}."),
        };

        await McpClientFixture.AssertToolIsolatedAsync(t, alphaOwner, tool, arguments);
    }

    [Fact]
    public async Task ToolsList_OutsideMembership_TransportNotFound_CookieUnauthorized()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await McpClientFixture.AssertTransportNotFoundAsync(t.AlphaOwner.Client, McpClientFixture.WorkspacePath(t.Beta.Slug), new { }, "tools/list");
        await McpClientFixture.AssertTransportNotFoundAsync(t.Outsider.Client, McpClientFixture.WorkspacePath(t.Alpha.Slug), new { }, "tools/list");
        await McpClientFixture.AssertTransportNotFoundAsync(t.RestrictedToAlpha.Client, McpClientFixture.WorkspacePath(t.Beta.Slug), new { }, "initialize");

        using var cookie = await McpClientFixture.PostRawAsync(t.AlphaHuman.Client, McpClientFixture.WorkspacePath(t.Alpha.Slug), "tools/list");
        Assert.Equal(HttpStatusCode.Unauthorized, cookie.StatusCode);
    }

    [Fact]
    public async Task RestrictedKey_InOwnWorkspace_Works()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.RestrictedToAlpha.Key, t.Alpha.Slug);

        McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "workspace_overview"));
    }

    [Fact]
    public async Task ScopeRestrictedKey_Write_Forbidden()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (space, _) = await RecordsApi.SeedCollectionAsync(t.AlphaOwner.Client, t.Alpha.Slug);
        using var readOnly = await TenancyFixture.CreateKeyClientAsync(factory, t.AlphaOwner.ActorId, new { scopes = ReadScope });
        var key = readOnly.DefaultRequestHeaders.Authorization!.Parameter!;
        await using var client = await McpClientFixture.ConnectAsync(factory, key, t.Alpha.Slug);

        McpClientFixture.Ok(await McpClientFixture.CallAsync(client, "workspace_overview"));
        var problem = McpClientFixture.Error(await McpClientFixture.CallAsync(client, "space_create", new { name = "Nein" }), 403, "forbidden");
        Assert.Contains("write", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.NotEqual(Guid.Empty, space.Id);
    }

    [Fact]
    public async Task ForeignIds_InOwnWorkspace_NotFoundToolErrors()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (betaSpace, betaCollection) = await RecordsApi.SeedCollectionAsync(t.BetaOwner.Client, t.Beta.Slug);
        var betaRecord = await RecordsApi.CreateInAsync(t.BetaOwner.Client, t.Beta.Slug, betaCollection.Id);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        (string Tool, object Args)[] calls =
        [
            ("collection_describe", new { collectionId = betaCollection.Id }),
            ("records_query", new { collectionId = betaCollection.Id }),
            ("record_get", new { recordId = betaRecord.Id }),
            ("record_get", new { recordId = betaRecord.Id, format = "json" }),
            ("record_update", new { recordId = betaRecord.Id, title = "Fremd" }),
            ("record_append", new { recordId = betaRecord.Id, markdown = "Fremd" }),
            ("record_archive", new { recordId = betaRecord.Id }),
            ("history_get", new { recordId = betaRecord.Id }),
            ("collection_update_schema", new { collectionId = betaCollection.Id, remove = TagsKey, confirmRemoval = true }),
        ];
        foreach (var (tool, args) in calls)
        {
            var problem = McpClientFixture.Error(await McpClientFixture.CallAsync(client, tool, args), 404, "not_found");
            Assert.False(problem.TryGetProperty("detail", out _), $"{tool} must not hint at existence.");
        }

        // Foreign ids as references are field errors, as in REST.
        McpClientFixture.Error(await McpClientFixture.CallAsync(client, "record_create", new { spaceId = betaSpace.Id, title = "Fremd" }), 400, "validation_failed");

        // Nothing happened in Beta.
        var unchanged = await RecordsApi.GetAsync(t.BetaOwner.Client, t.Beta.Slug, betaRecord.Id);
        Assert.Equal(1, unchanged.Revision);
        Assert.Null(unchanged.ArchivedAt);
    }

    [Fact]
    public async Task ActivityRecent_AfterSeq_Isolated_ForeignSeqUnknown()
    {
        var t = await TenancyFixture.GetAsync(factory);
        var (_, betaCollection) = await RecordsApi.SeedCollectionAsync(t.BetaOwner.Client, t.Beta.Slug);
        var betaRecord = await RecordsApi.CreateInAsync(t.BetaOwner.Client, t.Beta.Slug, betaCollection.Id);
        await using var betaClient = await McpClientFixture.ConnectAsync(factory, t.BetaOwner.Key, t.Beta.Slug);
        var betaSeq = McpClientFixture.Ok(await McpClientFixture.CallAsync(betaClient, "activity_recent", new { limit = 1 })).GetProperty("items")[0].GetProperty("seq").GetInt64();
        await using var alphaOwner = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        await McpClientFixture.AssertToolIsolatedAsync(t, alphaOwner, "activity_recent", new { afterSeq = 0, targetId = betaRecord.Id });

        var history = McpClientFixture.Ok(await McpClientFixture.CallAsync(alphaOwner, "activity_recent", new { afterSeq = 0, targetId = betaRecord.Id }));
        Assert.Empty(history.GetProperty("items").EnumerateArray());
        var foreign = McpClientFixture.Error(await McpClientFixture.CallAsync(alphaOwner, "activity_recent", new { afterSeq = betaSeq }), 400, "validation_failed");
        Assert.Equal("invalid_cursor", foreign.GetProperty("errors")[0].GetProperty("code").GetString());
    }
}
