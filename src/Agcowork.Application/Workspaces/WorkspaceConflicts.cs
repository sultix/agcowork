using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;

namespace Agcowork.Application.Workspaces;

/// <summary>Conflicts of the workspace module. Codes are part of the API contract.</summary>
public static class WorkspaceConflicts
{
    public static UniqueConflict Slug { get; } = new(
        "ix_workspaces_slug",
        new ConflictError("slug_taken", "Another workspace already uses this slug. Choose a different one.", "slug"));

    public static UniqueConflict Member { get; } = new(
        "ix_memberships_workspace_id_actor_id",
        new ConflictError("already_member", "The actor is already a member of this workspace. Change the membership with PATCH instead.", "actorId"));

    /// <summary>Any write to an archived workspace.</summary>
    public static ConflictError Archived { get; } =
        new("workspace_archived", "The workspace is archived and read-only.");

    /// <summary>Demoting or removing the last owner.</summary>
    public static ConflictError LastOwner(string? field) =>
        new("last_owner", "A workspace needs at least one owner. Make another member owner first.", field);
}
