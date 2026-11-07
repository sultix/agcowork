using System.ComponentModel;
using Agcowork.Application.Spaces;
using Agcowork.Application.Workspaces;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Mcp.Tools;

/// <summary>Orientation and spaces (<c>/w/{workspaceSlug}/mcp</c>). Thin: bind, call the use case, render.</summary>
[McpServerToolType]
internal static class WorkspaceTools
{
    [McpServerTool(Name = "workspace_overview", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Call this first. Returns the workspace this server is bound to (name, description, your role and scopes) and all its "
        + "spaces with their collections: ids, descriptions written for agents (what a record is, when to add one) and record "
        + "counts. Use the descriptions to decide where something belongs, then call collection_describe before you create "
        + "records in a collection. Archived spaces and collections are left out. Requires read.")]
    public static async Task<CallToolResult> Overview(GetWorkspaceOverview useCase, McpOutput output, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetWorkspaceOverviewRequest(), cancellationToken);
        return output.From(result, overview => McpText.Overview(overview));
    }

    [McpServerTool(Name = "spaces_list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists the spaces of the workspace (areas such as 'Produkt' or 'Engineering') by sort order with id, name and description. "
        + "workspace_overview already contains them together with their collections; use this for archived spaces "
        + "(includeArchived: true). Requires read.")]
    public static async Task<CallToolResult> SpacesList(
        ListSpaces useCase,
        McpOutput output,
        [Description("Also list archived spaces. Default false.")] bool? includeArchived = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new ListSpacesRequest(includeArchived ?? false), cancellationToken);
        return output.From(result, spaces => output.SummaryWithJson($"{spaces.Count} space(s).", spaces));
    }

    [McpServerTool(Name = "space_create", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Creates a space, a top-level area that holds collections and free pages. Check workspace_overview first: most things "
        + "belong into an existing space. 'name' max. 200 characters; 'description' (max. 2000) tells agents what the space is "
        + "for. Returns the space with its id (the 'spaceId' for collection_create and record_create). Requires write.")]
    public static async Task<CallToolResult> SpaceCreate(
        CreateSpace useCase,
        McpOutput output,
        [Description("Display name, max. 200 characters, e.g. 'Produkt'.")] string name,
        [Description("What the space is for, max. 2000 characters. Written for agents.")] string? description = null,
        [Description("Emoji or icon name, max. 64 characters.")] string? icon = null,
        [Description("Position in the navigation; default: after the last space.")] int? sortOrder = null,
        CancellationToken cancellationToken = default)
    {
        var result = await useCase.HandleAsync(new CreateSpaceRequest(name, description, icon, sortOrder), cancellationToken);
        return output.From(result, space => output.SummaryWithJson($"Created space {space.Name} (spaceId {space.Id}).", space));
    }
}
