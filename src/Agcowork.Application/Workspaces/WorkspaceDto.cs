using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Workspaces;

/// <summary>A workspace as seen by the caller.</summary>
/// <param name="Id">Workspace id.</param>
/// <param name="Slug">Route segment: <c>/api/v1/w/{slug}/…</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">What the workspace is for.</param>
/// <param name="CreatedBy">Actor that created the workspace.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="ArchivedAt">Set when archived; archived workspaces are read-only.</param>
/// <param name="Role">The caller's role in the workspace.</param>
/// <param name="Scopes">What the caller may do here: membership scopes narrowed by the API key, if any.</param>
public sealed record WorkspaceDto(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    WorkspaceRole Role,
    IReadOnlyList<string> Scopes)
{
    public static WorkspaceDto From(Workspace workspace, WorkspaceRole role, IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(scopes);
        return new WorkspaceDto(
            workspace.Id,
            workspace.Slug,
            workspace.Name,
            workspace.Description,
            workspace.CreatedBy,
            workspace.CreatedAt,
            workspace.ArchivedAt,
            role,
            [.. scopes.Order(StringComparer.Ordinal)]);
    }
}
