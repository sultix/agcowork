using Agcowork.Domain.Identity;
using Microsoft.AspNetCore.Authorization;

namespace Agcowork.Api.Auth;

/// <summary>Requires <see cref="Scope"/> among the <see cref="ActorClaims.InstanceScope"/> claims, directly or implied (<see cref="InstanceScope.Grants"/>).</summary>
/// <remarks>
/// Requirement and handler in one class only works because the check needs nothing but the claims:
/// ASP.NET's pass-through handler invokes it, and the instance is created by hand, outside DI.
/// Checks that need services (e.g. a membership lookup in AP-005) get a plain requirement record plus
/// a separate <c>AuthorizationHandler&lt;TRequirement&gt;</c> registered in DI.
/// </remarks>
internal sealed class InstanceScopeRequirement(string scope)
    : AuthorizationHandler<InstanceScopeRequirement>, IAuthorizationRequirement
{
    public string Scope { get; } = scope;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, InstanceScopeRequirement requirement)
    {
        var held = context.User.FindAll(ActorClaims.InstanceScope).Select(claim => claim.Value);
        if (InstanceScope.Grants(held, requirement.Scope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
