namespace Agcowork.Domain.Workspaces;

/// <summary>
/// Rights inside a workspace (architecture §7). A membership carries the scopes of its role,
/// optionally narrowed; an API key can narrow them further (<c>KeyScope</c>, same values).
/// </summary>
public static class WorkspaceScope
{
    /// <summary>Read workspace content.</summary>
    public const string Read = "read";

    /// <summary>Create and change content.</summary>
    public const string Write = "write";

    /// <summary>Change collections, properties and views.</summary>
    public const string Schema = "schema";

    /// <summary>Manage the workspace and its members.</summary>
    public const string Admin = "admin";

    public static IReadOnlyList<string> All { get; } = [Read, Write, Schema, Admin];

    private static readonly string[] ViewerScopes = [Read];
    private static readonly string[] EditorScopes = [Read, Write];
    private static readonly string[] AdminScopes = [Read, Write, Schema, Admin];

    public static bool IsKnown(string scope) => All.Contains(scope, StringComparer.Ordinal);

    /// <summary>Default scopes of a role: viewer = read; editor = read, write; admin and owner = all.</summary>
    public static IReadOnlyList<string> ForRole(WorkspaceRole role) => role switch
    {
        WorkspaceRole.Viewer => ViewerScopes,
        WorkspaceRole.Editor => EditorScopes,
        WorkspaceRole.Admin or WorkspaceRole.Owner => AdminScopes,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown workspace role."),
    };
}
