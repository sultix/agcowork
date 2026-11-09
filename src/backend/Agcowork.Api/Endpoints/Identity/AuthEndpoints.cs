using Agcowork.Api.Auth;
using Agcowork.Api.Http;
using Agcowork.Application.Identity.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Agcowork.Api.Endpoints.Identity;

internal static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth").AllowAnonymous();

        auth.MapPost("/login", LoginAsync)
            .WithName("Login")
            .WithSummary("Log in a human with email and password")
            .WithDescription(
                "Humans only. On success the response sets an HttpOnly session cookie (7 days, sliding) and returns 204. "
                + "Wrong email, wrong password, inactive account and agent accounts all yield the same 401, so the response "
                + "never reveals whether an email exists. Failed attempts are limited per email address and per client "
                + "address; beyond the limit the answer is 429 'rate_limited' with 'retryAfterSeconds' and a Retry-After "
                + "header, also for the right password, until the wait is over. Agents do not log in; they send "
                + "'Authorization: Bearer <api key>'.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Cast: a handler taking only HttpContext would otherwise bind as a RequestDelegate and drop the typed result (ASP0016).
        auth.MapPost("/logout", (Delegate)LogoutAsync)
            .WithName("Logout")
            .WithSummary("End the session")
            .WithDescription("Deletes the session cookie. Always 204, also without a session.");

        return api;
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        Login useCase,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.ToProblem(httpContext.Response);
        }

        await SessionCookie.SignInAsync(httpContext, result.Value);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> LogoutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(AuthSchemes.Cookie);
        return TypedResults.NoContent();
    }
}
