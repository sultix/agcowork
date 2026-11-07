using Agcowork.Api.Auth;
using Agcowork.Api.Http;
using Agcowork.Application.Identity.Actors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Agcowork.Api.Endpoints.Identity;

/// <summary>
/// Actor administration. Endpoints are thin: bind input, call the use case, map the result.
/// Authorization is enforced twice on purpose: by policy here and inside the use case (which MCP reuses).
/// </summary>
internal static class ActorEndpoints
{
    public static RouteGroupBuilder MapActorEndpoints(this RouteGroupBuilder api)
    {
        var actors = api.MapGroup("/actors")
            .WithTags("Actors")
            .RequireAuthorization(AuthPolicies.InstanceAdmin)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        actors.MapGet("/", ListAsync)
            .WithName("ListActors")
            .WithSummary("List actors")
            .WithDescription("All humans and agents of the installation, ordered by name, including inactive ones. Requires instance:admin.");

        actors.MapPost("/", CreateAsync)
            .WithName("CreateActor")
            .WithSummary("Create an actor")
            .WithDescription(
                "Creates a human or agent. 'kind' is 'human' or 'agent'. Humans need 'email' and may get an initial 'password' "
                + "(min. 12 characters) for local login; agents get no password and authenticate with API keys "
                + "(POST /api/v1/actors/{id}/keys). 'instanceScopes' is a subset of 'instance:admin', 'workspaces:create'. "
                + "Requires instance:admin. A taken email yields 409 with code 'email_taken'.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        actors.MapGet("/{id:guid}", GetAsync)
            .WithName("GetActor")
            .WithSummary("Get an actor")
            .WithDescription("One actor by id. Requires instance:admin.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        actors.MapPatch("/{id:guid}", UpdateAsync)
            .WithName("UpdateActor")
            .WithSummary("Update an actor")
            .WithDescription(
                "Partial update: omitted or null fields stay unchanged; role \"\" clears the role; 'instanceScopes' replaces the set. "
                + "'isActive': false deactivates the actor: no login, all its API keys are rejected. "
                + "You cannot deactivate yourself or remove instance:admin from yourself (409 'self_lockout'). Requires instance:admin.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static async Task<Results<Ok<IReadOnlyList<ActorDto>>, ProblemHttpResult>> ListAsync(ListActors useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListActorsRequest(), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<ActorDto>, ProblemHttpResult>> CreateAsync(
        CreateActorRequest request,
        CreateActor useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(request, cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"{ApiV1.Prefix}/actors/{result.Value.Id}", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ActorDto>, ProblemHttpResult>> GetAsync(Guid id, GetActor useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new GetActorRequest(id), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ActorDto>, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateActorBody body,
        UpdateActor useCase,
        CancellationToken cancellationToken)
    {
        var request = new UpdateActorRequest(id, body.Name, body.Role, body.InstanceScopes, body.IsActive);
        var result = await useCase.HandleAsync(request, cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }
}

/// <summary>Body of <c>PATCH /actors/{id}</c>; the id comes from the route.</summary>
internal sealed record UpdateActorBody(string? Name, string? Role, IReadOnlyList<string>? InstanceScopes, bool? IsActive);
