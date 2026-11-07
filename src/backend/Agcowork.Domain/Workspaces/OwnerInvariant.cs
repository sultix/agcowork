namespace Agcowork.Domain.Workspaces;

/// <summary>
/// Every workspace keeps at least one owner. Callers count the current owners (under a lock on the
/// workspace, so two concurrent demotions cannot both pass) and ask before changing or removing a membership.
/// </summary>
public static class OwnerInvariant
{
    /// <summary>
    /// True when the workspace still has an owner after <paramref name="target"/> gets
    /// <paramref name="newRole"/> (null = membership removed). <paramref name="ownerCount"/> is the
    /// number of owners before the change, including <paramref name="target"/> if it is one.
    /// </summary>
    public static bool HoldsAfter(int ownerCount, Membership target, WorkspaceRole? newRole)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsOwner || newRole == WorkspaceRole.Owner)
        {
            return true;
        }

        return ownerCount > 1;
    }
}
