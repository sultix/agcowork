using Agcowork.Application.Common.Results;
using Agcowork.Application.Workspaces;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Tenancy;

/// <summary>
/// Authorization checks for workspace use cases, against <see cref="ITenantContext.EffectiveScopes"/>.
/// Same shape as <c>CurrentActorAuthorization</c>: null when allowed, otherwise the error to return.
/// </summary>
/// <example>
/// <code>
/// if (tenant.RequireWritable() is { } archived) { return archived; }
/// if (tenant.RequireScope(WorkspaceScope.Write) is { } denied) { return denied; }
/// </code>
/// </example>
public static class TenantAuthorization
{
    /// <summary>403 <c>forbidden</c> ("requires write") unless the caller's effective scopes contain <paramref name="scope"/>.</summary>
    public static Error? RequireScope(this ITenantContext tenant, string scope)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return tenant.EffectiveScopes.Contains(scope) ? null : ForbiddenError.MissingScope(scope);
    }

    /// <summary>
    /// Owner-only operations (archiving, creating or changing owners). Needs the owner role and the
    /// <c>admin</c> scope, so an owner's read-only key cannot do them.
    /// </summary>
    public static Error? RequireOwner(this ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (tenant.RequireScope(WorkspaceScope.Admin) is { } denied)
        {
            return denied;
        }

        return tenant.Membership.IsOwner ? null : ForbiddenError.MissingScope("role owner");
    }

    /// <summary>409 <c>workspace_archived</c> for any write to an archived workspace. Check it first in every writing use case.</summary>
    public static Error? RequireWritable(this ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return tenant.IsArchived ? WorkspaceConflicts.Archived : null;
    }
}
