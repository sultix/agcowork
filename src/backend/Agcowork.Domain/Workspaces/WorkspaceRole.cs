namespace Agcowork.Domain.Workspaces;

/// <summary>Role of a member in a workspace. Determines the default scopes (see <see cref="WorkspaceScope.ForRole"/>).</summary>
public enum WorkspaceRole
{
    /// <summary>Read only.</summary>
    Viewer = 1,

    /// <summary>Read and write content.</summary>
    Editor = 2,

    /// <summary>Content, schema and member management; cannot create or change owners.</summary>
    Admin = 3,

    /// <summary>Everything an admin can do, plus managing owners and archiving the workspace.</summary>
    Owner = 4,
}
