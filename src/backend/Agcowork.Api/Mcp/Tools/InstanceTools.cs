using Agcowork.Application.Common;
using System.ComponentModel;
using Agcowork.Application.Identity.Actors;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Workspaces;
using Agcowork.Application.Workspaces.Templates;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Mcp.Tools;

/// <summary>
/// Instance level (<c>/mcp</c>, no workspace): who am I, which workspaces, create workspaces, add members.
/// </summary>
/// <remarks>
/// <c>workspace_create</c> and <c>workspace_add_member</c> run in a DI scope of their own (<see cref="TenantScopes"/>):
/// <see cref="CreateWorkspace"/> sets the tenant of its scope to the new workspace (and a scope never switches tenants), and
/// adding a member needs the tenant of the addressed workspace. The request scope stays tenant-free, so several such calls
/// work in one MCP session.
/// </remarks>
[McpServerToolType]
internal static class InstanceTools
{
    [McpServerTool(Name = "me", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Who you are: your actor (id, kind, name), your instance scopes (e.g. workspaces:create, instance:admin) and, since you "
        + "call with an API key, the key's metadata including its restrictions (workspaceId: the only workspace it works in; "
        + "scopes: the workspace scopes it is limited to). A restricted key carries no instance scopes.")]
    public static async Task<CallToolResult> Me(GetMe useCase, McpOutput output, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetMeRequest(), cancellationToken);
        return output.From(result, me => output.SummaryWithJson(
            $"Actor {me.Name} (id {me.Id}); instance scopes: {(me.InstanceScopes.Count == 0 ? "none" : string.Join(", ", me.InstanceScopes))}.", me));
    }

    [McpServerTool(Name = "workspaces_list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "The workspaces you are a member of, with your role and effective scopes, by name. Each workspace has its own MCP endpoint "
        + "/w/{slug}/mcp with the content tools (workspace_overview, records_query, record_create, …). A key restricted to one "
        + "workspace sees only that one.")]
    public static async Task<CallToolResult> WorkspacesList(ListMyWorkspaces useCase, McpOutput output, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListMyWorkspacesRequest(), cancellationToken);
        return output.From(result, workspaces => output.SummaryWithJson(
            workspaces.Count == 0
                ? "You are not a member of any workspace."
                : string.Join("\n", workspaces.Select(w => $"- {QuotedString.Of(w.Name)} (slug {w.Slug}, id {w.Id}, role {w.Role.ToString().ToLowerInvariant()}){(w.ArchivedAt is null ? string.Empty : ", archived")}")),
            workspaces));
    }

    [McpServerTool(Name = "templates_list", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The workspace templates you can pass as 'templateId' to workspace_create, with what they create. The default one is applied when templateId is omitted.")]
    public static async Task<CallToolResult> TemplatesList(ListWorkspaceTemplates useCase, McpOutput output, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListWorkspaceTemplatesRequest(), cancellationToken);
        return output.From(result, templates => output.SummaryWithJson(
            string.Join("\n", templates.Select(t => $"- {t.Id}{(t.IsDefault ? " (default)" : string.Empty)}: {t.Name} – {t.Description}")), templates));
    }

    [McpServerTool(Name = "workspace_create", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Creates a workspace; you become its owner. Requires the instance scope workspaces:create (see me). 'templateId' "
        + "(templates_list) defaults to '{{defaultTemplate}}'. Templates: {{templates}} Afterwards connect to /w/{slug}/mcp and "
        + "call workspace_overview. A taken slug: 'slug_taken'.")]
    public static async Task<CallToolResult> WorkspaceCreate(
        TenantScopes tenantScopes,
        McpOutput output,
        [Description("URL segment, unique and immutable: lowercase letters, digits, single hyphens, max. 63 characters, e.g. 'projekt-alpha'.")] string slug,
        [Description("Display name, max. 200 characters.")] string name,
        [Description("What the workspace is for, max. 2000 characters. Agents read it.")] string? description = null,
        [Description("Template id from templates_list; allowed: {{templateIds}}. Default '{{defaultTemplate}}'.")] string? templateId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await tenantScopes.RunIsolatedAsync(
            (services, ct) => services.GetRequiredService<CreateWorkspace>().HandleAsync(new CreateWorkspaceRequest(slug, name, description, templateId), ct),
            cancellationToken);
        return output.From(result, workspace => output.SummaryWithJson(
            $"Created workspace {QuotedString.Of(workspace.Name)} (slug {workspace.Slug}, id {workspace.Id}); you are its owner. MCP endpoint: /w/{workspace.Slug}/mcp.", workspace));
    }

    [McpServerTool(Name = "workspace_add_member", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Adds an actor (human or agent) to a workspace you administer (admin or owner; only owners can add owners). 'role' "
        + "allowed: {{roles}}. 'scopes' optionally narrows the role (a non-empty subset of its scopes: {{workspaceScopes}}). "
        + "Already a member: 'already_member'. An unknown workspace or one you are not a member of is 'not_found'.")]
    public static async Task<CallToolResult> WorkspaceAddMember(
        TenantScopes tenantScopes,
        McpOutput output,
        [Description("Workspace id (from workspaces_list).")] Guid workspaceId,
        [Description("Actor to add.")] Guid actorId,
        [Description("Allowed: {{roles}}.")] string role,
        [Description("Optional narrowing: a non-empty subset of the role's scopes (read, write, schema, admin).")] IReadOnlyList<string>? scopes = null,
        CancellationToken cancellationToken = default)
    {
        var result = await tenantScopes.RunInWorkspaceAsync(
            workspaceId,
            (services, ct) => services.GetRequiredService<AddMember>().HandleAsync(new AddMemberRequest(actorId, role, scopes), ct),
            cancellationToken);
        return output.From(result, member => output.SummaryWithJson(
            $"Added {member.Name} (actor {member.ActorId}) to workspace {workspaceId} as {member.Role.ToString().ToLowerInvariant()}.", member));
    }
}
