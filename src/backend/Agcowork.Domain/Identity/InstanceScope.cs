namespace Agcowork.Domain.Identity;

/// <summary>
/// Rights that apply to the whole installation, outside any workspace. Workspace rights come
/// from memberships (<c>Workspaces.WorkspaceScope</c>).
/// </summary>
public static class InstanceScope
{
    /// <summary>Manage actors and API keys. Implies <see cref="WorkspacesCreate"/>.</summary>
    public const string Admin = "instance:admin";

    /// <summary>Create new workspaces; the creator becomes owner.</summary>
    public const string WorkspacesCreate = "workspaces:create";

    public static IReadOnlyList<string> All { get; } = [Admin, WorkspacesCreate];

    public static bool IsKnown(string scope) => All.Contains(scope, StringComparer.Ordinal);

    /// <summary>True when <paramref name="held"/> grants <paramref name="required"/>, directly or by implication (admin implies workspaces:create).</summary>
    public static bool Grants(IEnumerable<string> held, string required)
    {
        ArgumentNullException.ThrowIfNull(held);
        var scopes = held as IReadOnlyCollection<string> ?? [.. held];
        return scopes.Contains(required, StringComparer.Ordinal)
            || (string.Equals(required, WorkspacesCreate, StringComparison.Ordinal) && scopes.Contains(Admin, StringComparer.Ordinal));
    }
}
