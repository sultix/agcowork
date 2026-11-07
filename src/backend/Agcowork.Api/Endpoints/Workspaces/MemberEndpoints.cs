using Agcowork.Api.Http;
using Agcowork.Application.Workspaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Agcowork.Api.Endpoints.Workspaces;

internal static class MemberEndpoints
{
    public static RouteGroupBuilder MapMemberEndpoints(this RouteGroupBuilder workspace)
    {
        var members = workspace.MapGroup("/members").WithTags("Members");

        members.MapGet("/", ListAsync)
            .WithName("ListMembers")
            .WithSummary("List members")
            .WithDescription("Members of the workspace with role and scopes, owners first. Requires read.");

        members.MapPost("/", AddAsync)
            .WithName("AddMember")
            .WithSummary("Add a member")
            .WithDescription(
                "Adds an actor (human or agent, by 'actorId') with a 'role': owner, admin, editor or viewer. Roles grant scopes: "
                + "viewer = read; editor = read, write; admin and owner = read, write, schema, admin. Optional 'scopes' narrows the role "
                + "to a non-empty subset. Requires admin; only owners can add owners. An existing member yields 409 'already_member'.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        members.MapPatch("/{actorId:guid}", UpdateAsync)
            .WithName("UpdateMember")
            .WithSummary("Change a member's role or scopes")
            .WithDescription(
                "Partial update. A new 'role' without 'scopes' resets the scopes to the role's defaults; 'scopes' alone narrows the "
                + "current role. Requires admin; changing an owner or promoting to owner requires the owner role. The last owner cannot "
                + "be demoted (409 'last_owner'): make another member owner first.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        members.MapDelete("/{actorId:guid}", RemoveAsync)
            .WithName("RemoveMember")
            .WithSummary("Remove a member")
            .WithDescription(
                "Removes the actor from the workspace. Requires admin; removing an owner requires the owner role. The last owner "
                + "cannot be removed (409 'last_owner').")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return workspace;
    }

    private static async Task<Results<Ok<IReadOnlyList<MemberDto>>, ProblemHttpResult>> ListAsync(ListMembers useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListMembersRequest(), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Created<MemberDto>, ProblemHttpResult>> AddAsync(
        AddMemberRequest request,
        AddMember useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(request, cancellationToken);
        // There is no single-member resource (yet); the member shows up in GET /members.
        return result.IsSuccess ? TypedResults.Created((string?)null, result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<MemberDto>, ProblemHttpResult>> UpdateAsync(
        Guid actorId,
        UpdateMemberBody body,
        UpdateMember useCase,
        CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new UpdateMemberRequest(actorId, body.Role, body.Scopes), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveAsync(Guid actorId, RemoveMember useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new RemoveMemberRequest(actorId), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }
}

/// <summary>Body of <c>PATCH /members/{actorId}</c>; the actor comes from the route.</summary>
internal sealed record UpdateMemberBody(string? Role, IReadOnlyList<string>? Scopes);
