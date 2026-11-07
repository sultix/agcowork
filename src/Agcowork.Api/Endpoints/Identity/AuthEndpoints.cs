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

        // Rate limiting against password guessing (per IP and per email) follows in a later package.
        auth.MapPost("/login", LoginAsync)
            .WithName("Login")
            .WithSummary("Log in a human with email and password")
            .WithDescription(
                "Humans only. On success the response sets an HttpOnly session cookie (7 days, sliding) and returns 204. "
                + "Wrong email, wrong password, inactive account and agent accounts all yield the same 401, so the response "
                + "never reveals whether an email exists. Agents do not log in; they send 'Authorization: Bearer <api key>'.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

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
            return result.Error.ToProblem();
        }

        await httpContext.SignInAsync(
            AuthSchemes.Cookie,
            ActorClaims.ToPrincipal(result.Value, AuthSchemes.Cookie),
            new AuthenticationProperties { IsPersistent = true });
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> LogoutAsync(HttpContext httpContext)
    {
        await httpContext.SignOutAsync(AuthSchemes.Cookie);
        return TypedResults.NoContent();
    }
}
