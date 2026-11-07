using Agcowork.Api.Http;
using Agcowork.Application.Common.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace Agcowork.Api.Auth;

/// <summary>
/// Writes policy failures (403) in the same ProblemDetails shape as use-case errors, naming the
/// missing scope. Challenges (401) go to the scheme and become ProblemDetails via status code pages.
/// </summary>
internal sealed class ProblemDetailsAuthorizationResultHandler(IProblemDetailsService problemDetailsService)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (!authorizeResult.Forbidden)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var scope = authorizeResult.AuthorizationFailure?.FailedRequirements.OfType<InstanceScopeRequirement>().FirstOrDefault()?.Scope;
        var error = scope is null ? new ForbiddenError("Access denied.") : ForbiddenError.MissingScope(scope);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = ProblemDetailsMapping.ToProblemDetails(error),
        });
    }
}
