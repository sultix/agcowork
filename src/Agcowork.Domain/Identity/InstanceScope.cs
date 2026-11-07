namespace Agcowork.Domain.Identity;

/// <summary>
/// Rights that apply to the whole installation, outside any workspace. Workspace rights come
/// from memberships (AP-005).
/// </summary>
public static class InstanceScope
{
    /// <summary>Manage actors and API keys.</summary>
    public const string Admin = "instance:admin";

    /// <summary>Create new workspaces; the creator becomes owner.</summary>
    public const string WorkspacesCreate = "workspaces:create";

    public static IReadOnlyList<string> All { get; } = [Admin, WorkspacesCreate];

    public static bool IsKnown(string scope) => All.Contains(scope, StringComparer.Ordinal);
}
