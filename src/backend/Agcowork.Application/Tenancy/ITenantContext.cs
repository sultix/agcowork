using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Tenancy;

/// <summary>
/// The workspace a request addresses (<c>/api/v1/w/{slug}/…</c>) and the caller's membership in it,
/// established by <see cref="ResolveTenant"/> before any workspace use case runs (architecture §2a).
/// Workspace use cases inject this and authorize with <see cref="TenantAuthorization"/>.
/// </summary>
/// <remarks>
/// The injected instance reads <see cref="ITenantContextAccessor.Current"/>; its members throw
/// <see cref="InvalidOperationException"/> when no tenant is set (a programming error: a workspace
/// use case was called outside the workspace route group).
/// </remarks>
public interface ITenantContext
{
    Guid WorkspaceId { get; }

    string Slug { get; }

    /// <summary>Archived workspaces are read-only: writes fail with 409 <c>workspace_archived</c>.</summary>
    bool IsArchived { get; }

    /// <summary>The caller's membership (detached snapshot).</summary>
    Membership Membership { get; }

    /// <summary>Membership scopes intersected with the key's scopes (see <see cref="Tenancy.EffectiveScopes"/>).</summary>
    IReadOnlySet<string> EffectiveScopes { get; }
}

/// <summary>Immutable tenant context of one request.</summary>
public sealed class TenantContext(Guid workspaceId, string slug, bool isArchived, Membership membership, IReadOnlySet<string> effectiveScopes)
    : ITenantContext
{
    public Guid WorkspaceId { get; } = workspaceId;

    public string Slug { get; } = slug;

    public bool IsArchived { get; } = isArchived;

    public Membership Membership { get; } = membership;

    public IReadOnlySet<string> EffectiveScopes { get; } = effectiveScopes;
}
