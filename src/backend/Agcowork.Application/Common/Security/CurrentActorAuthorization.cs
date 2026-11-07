using Agcowork.Application.Common.Results;
using Agcowork.Domain.Identity;

namespace Agcowork.Application.Common.Security;

/// <summary>
/// Authorization checks for use cases. Each returns null when allowed, otherwise the error to
/// return as-is: <c>if (actor.RequireInstanceScope(InstanceScope.Admin) is { } denied) { return denied; }</c>
/// </summary>
/// <remarks>Workspace rights are checked against the tenant instead: see <c>Tenancy.TenantAuthorization</c>.</remarks>
public static class CurrentActorAuthorization
{
    public static Error? RequireAuthenticated(this ICurrentActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.IsAuthenticated ? null : new UnauthorizedError();
    }

    /// <summary>Requires an instance scope; <c>instance:admin</c> implies <c>workspaces:create</c> (<see cref="InstanceScope.Grants"/>).</summary>
    public static Error? RequireInstanceScope(this ICurrentActor actor, string scope)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAuthenticated)
        {
            return new UnauthorizedError();
        }

        return InstanceScope.Grants(actor.InstanceScopes, scope) ? null : ForbiddenError.MissingScope(scope);
    }
}
