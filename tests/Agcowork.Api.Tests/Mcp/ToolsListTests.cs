using Agcowork.Api.Tests.Tenancy;

namespace Agcowork.Api.Tests.Mcp;

/// <summary><c>initialize</c> and <c>tools/list</c>: exactly the documented tools (architecture §4.2), each with a description and schema.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ToolsListTests(AgcoworkApiFactory factory)
{
    /// <summary>Architecture §4.2 phase 1 plus records_list, spaces_list, space_create (AP-011); no search, no comment_add.</summary>
    public static readonly string[] WorkspaceTools =
    [
        "workspace_overview", "spaces_list", "space_create",
        "collection_describe", "collection_create", "collection_update_schema",
        "records_list", "records_query", "record_get", "record_create", "record_update", "record_append", "record_archive", "history_get",
        "view_create", "activity_recent",
    ];

    public static readonly string[] InstanceTools = ["me", "workspaces_list", "templates_list", "workspace_create", "workspace_add_member"];

    [Fact]
    public async Task ListTools_WorkspaceEndpoint_ExactlyTheDocumentedToolsWithDescriptions()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, t.Alpha.Slug);

        Assert.Equal("agcowork", client.ServerInfo?.Name);
        Assert.Contains("workspace_overview", client.ServerInstructions, StringComparison.Ordinal);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(WorkspaceTools.Order(StringComparer.Ordinal), tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        foreach (var tool in tools)
        {
            Assert.True(tool.Description.Length > 80, $"{tool.Name} needs a description written for agents.");
            Assert.Equal("object", tool.JsonSchema.GetProperty("type").GetString());
            Assert.DoesNotContain("{{", tool.Description, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", tool.JsonSchema.GetRawText(), StringComparison.Ordinal);
        }

        // Closed sets come from the registries.
        var create = tools.Single(tool => tool.Name == "collection_create").Description;
        Assert.All(Domain.Collections.Schema.PropertyTypes.AllWireNames, type => Assert.Contains(type, create, StringComparison.Ordinal));
        Assert.All(Domain.Collections.Schema.PropertyOption.Colors, color => Assert.Contains(color, create, StringComparison.Ordinal));
        var activity = tools.Single(tool => tool.Name == "activity_recent").Description;
        Assert.All(Domain.Activity.ActivityTypes.All, type => Assert.Contains(type, activity, StringComparison.Ordinal));
        var viewType = tools.Single(tool => tool.Name == "view_create").JsonSchema.GetProperty("properties").GetProperty("type").GetProperty("description").GetString();
        Assert.All(Domain.Views.ViewTypes.AllWireNames, type => Assert.Contains(type, viewType, StringComparison.Ordinal));

        var query = tools.Single(tool => tool.Name == "records_query");
        Assert.Contains("is_not_empty", query.Description, StringComparison.Ordinal);
        Assert.Contains("collectionId", query.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("record_get", tools.Single(tool => tool.Name == "record_update").Description, StringComparison.Ordinal);
        Assert.Contains("preferred way", tools.Single(tool => tool.Name == "record_append").Description, StringComparison.Ordinal);
        Assert.Contains("not with since", tools.Single(tool => tool.Name == "activity_recent").Description, StringComparison.Ordinal);
        Assert.Contains("Call this first", tools.Single(tool => tool.Name == "workspace_overview").Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListTools_InstanceEndpoint_OnlyInstanceTools()
    {
        var t = await TenancyFixture.GetAsync(factory);
        await using var client = await McpClientFixture.ConnectAsync(factory, t.AlphaOwner.Key, slug: null);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(InstanceTools.Order(StringComparer.Ordinal), tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
        Assert.All(tools, tool => Assert.DoesNotContain("{{", tool.Description + tool.JsonSchema.GetRawText(), StringComparison.Ordinal));
        Assert.Contains("software-projekt", tools.Single(tool => tool.Name == "workspace_create").Description, StringComparison.Ordinal);
    }
}
