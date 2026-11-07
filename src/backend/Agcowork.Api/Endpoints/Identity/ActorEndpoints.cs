using Agcowork.Api.Auth;
using Agcowork.Api.Http;
using Agcowork.Application.Identity.Actors;
using Agcowork.Application.Identity.Passwords;
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

        actors.MapPut("/{id:guid}/password", ResetPasswordAsync)
            .WithName("ResetActorPassword")
            .WithSummary("Set a human's password")
            .WithDescription(
                "Sets 'newPassword' (12 to 256 characters) for a human, e.g. when they forgot it; creates the local login if the "
                + "human has none yet. All existing sessions of that human are signed out (your own too, if you reset yourself; "
                + "use PATCH /api/v1/me/password for that). 'mustChange' (default true) makes "
                + "GET /api/v1/me report 'mustChangePassword' so the human picks their own password after logging in. Agents have "
                + "no password: 400 with code 'not_human' on 'actorId'. Requires instance:admin.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

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

    private static async Task<Results<NoContent, ProblemHttpResult>> ResetPasswordAsync(
        Guid id,
        ResetPasswordBody body,
        ResetPassword useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ResetPasswordRequest(id, body.NewPassword, body.MustChange), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }
}

/// <summary>Body of <c>PATCH /actors/{id}</c>; the id comes from the route. Null or omitted leaves a field unchanged.</summary>
/// <param name="Name">New display name.</param>
/// <param name="Role">New free-text role; "" clears it.</param>
/// <param name="InstanceScopes">Replaces the whole set when given.</param>
/// <param name="IsActive">False deactivates: no login, keys rejected.</param>
internal sealed record UpdateActorBody(string? Name = null, string? Role = null, IReadOnlyList<string>? InstanceScopes = null, bool? IsActive = null);

/// <summary>Body of <c>PUT /actors/{id}/password</c>; the id comes from the route.</summary>
/// <param name="NewPassword">The new password, 12 to 256 characters.</param>
/// <param name="MustChange">Whether the human should replace it after logging in. Default true.</param>
internal sealed record ResetPasswordBody(string? NewPassword, bool MustChange = true);
