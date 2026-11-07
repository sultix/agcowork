using Agcowork.Application.Common.Results;

namespace Agcowork.Application.Common.Security;

/// <summary>
/// Authorization checks for use cases. Each returns null when allowed, otherwise the error to
/// return as-is: <c>if (actor.RequireInstanceScope(InstanceScope.Admin) is { } denied) { return denied; }</c>
/// </summary>
public static class CurrentActorAuthorization
{
    public static Error? RequireAuthenticated(this ICurrentActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.IsAuthenticated ? null : new UnauthorizedError();
    }

    public static Error? RequireInstanceScope(this ICurrentActor actor, string scope)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAuthenticated)
        {
            return new UnauthorizedError();
        }

        return actor.InstanceScopes.Contains(scope) ? null : ForbiddenError.MissingScope(scope);
    }
}
