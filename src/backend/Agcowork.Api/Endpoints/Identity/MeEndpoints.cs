using Agcowork.Api.Auth;
using Agcowork.Api.Http;
using Agcowork.Application.Identity.Actors;
using Agcowork.Application.Identity.Passwords;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Agcowork.Api.Endpoints.Identity;

internal static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", GetMeAsync)
            .WithTags("Me")
            .WithName("GetMe")
            .WithSummary("Who am I")
            .WithDescription(
                "Returns the calling actor (human via session cookie, agent via API key) with its kind and instance scopes. "
                + "For API-key calls, 'apiKey' holds the key's metadata: prefix, scope and workspace restrictions, expiry. "
                + "'mustChangePassword' is true when a human still uses the bootstrap password or one an admin set; they should "
                + "choose their own with PATCH /api/v1/me/password (always false for agents). "
                + "Agents should call this first to check which identity and rights a key carries.");

        api.MapPatch("/me/password", ChangePasswordAsync)
            .WithTags("Me")
            .WithName("ChangeMyPassword")
            .WithSummary("Change my password")
            .WithDescription(
                "Humans with a session cookie only: sets 'newPassword' (12 to 256 characters) after checking 'currentPassword'. "
                + "Returns 204 with a fresh session cookie; every other session of the account is signed out. Clears "
                + "'mustChangePassword'. A wrong 'currentPassword' yields 400 with code 'invalid_credentials' on that field, the same "
                + "password again 400 'unchanged'. Wrong current passwords are limited per account; beyond the limit the answer "
                + "is 429 'rate_limited' with 'retryAfterSeconds' and a Retry-After header, also for the right password, until "
                + "the wait is over. Agents and API-key calls get 403: they have no password.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return api;
    }

    private static async Task<Results<Ok<MeDto>, ProblemHttpResult>> GetMeAsync(GetMe useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetMeRequest(), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ChangePasswordAsync(
        ChangeOwnPasswordRequest request,
        ChangeOwnPassword useCase,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.ToProblem(httpContext.Response);
        }

        // The change invalidated every session issued before it, including this one: replace it.
        await SessionCookie.SignInAsync(httpContext, result.Value);
        return TypedResults.NoContent();
    }
}
