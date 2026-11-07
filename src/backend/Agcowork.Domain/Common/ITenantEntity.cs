namespace Agcowork.Domain.Common;

/// <summary>
/// An entity that belongs to exactly one workspace (tenant). Infrastructure applies a global query
/// filter on <see cref="WorkspaceId"/> and refuses to write entities of another workspace
/// (architecture §2a). <c>Workspace</c> itself is the tenant root and does not implement this.
/// </summary>
public interface ITenantEntity
{
    /// <summary>Owning workspace. Set automatically on insert inside a tenant context when left empty.</summary>
    Guid WorkspaceId { get; }
}
