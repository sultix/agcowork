using Agcowork.Api.Http;
using Agcowork.Application.Identity.Actors;
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
                + "Agents should call this first to check which identity and rights a key carries.");

        return api;
    }

    private static async Task<Results<Ok<MeDto>, ProblemHttpResult>> GetMeAsync(GetMe useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetMeRequest(), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}
