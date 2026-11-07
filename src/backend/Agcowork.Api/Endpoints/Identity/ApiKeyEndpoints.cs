using Agcowork.Api.Auth;
using Agcowork.Api.Http;
using Agcowork.Application.Identity.ApiKeys;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Agcowork.Api.Endpoints.Identity;

internal static class ApiKeyEndpoints
{
    public static RouteGroupBuilder MapApiKeyEndpoints(this RouteGroupBuilder api)
    {
        var keys = api.MapGroup(string.Empty)
            .WithTags("API keys")
            .RequireAuthorization(AuthPolicies.InstanceAdmin)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        keys.MapPost("/actors/{id:guid}/keys", CreateAsync)
            .WithName("CreateApiKey")
            .WithSummary("Issue an API key for an agent")
            .WithDescription(
                "Creates a key 'agck_<prefix>_<secret>' for an agent actor. The plain-text 'key' appears only in this 201 response; "
                + "store it immediately, it cannot be retrieved again (only a hash is kept). Optional body: 'scopes' (subset of "
                + "read, write, schema, admin; empty = the agent's membership rights apply in full), 'workspaceId' (restrict the "
                + "key to one workspace), 'expiresAt' (future timestamp). Humans cannot get keys. Requires instance:admin.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        keys.MapGet("/actors/{id:guid}/keys", ListAsync)
            .WithName("ListApiKeys")
            .WithSummary("List an actor's API keys")
            .WithDescription("Metadata of all keys of the actor, newest first, including revoked and expired ones. Never contains secrets. Requires instance:admin.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        keys.MapDelete("/keys/{id:guid}", RevokeAsync)
            .WithName("RevokeApiKey")
            .WithSummary("Revoke an API key")
            .WithDescription("Revokes the key immediately; further requests with it get 401. The key stays listed with 'revokedAt' for audit. Idempotent. Requires instance:admin.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static async Task<Results<Created<CreatedApiKeyDto>, ProblemHttpResult>> CreateAsync(
        Guid id,
        [FromBody] CreateApiKeyBody? body,
        CreateApiKey useCase,
        CancellationToken cancellationToken)
    {
        var request = new CreateApiKeyRequest(id, body?.Scopes, body?.WorkspaceId, body?.ExpiresAt);
        var result = await useCase.HandleAsync(request, cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"{ApiV1.Prefix}/actors/{id}/keys", result.Value)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<IReadOnlyList<ApiKeyDto>>, ProblemHttpResult>> ListAsync(Guid id, ListApiKeys useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new ListApiKeysRequest(id), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RevokeAsync(Guid id, RevokeApiKey useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.HandleAsync(new RevokeApiKeyRequest(id), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }
}

/// <summary>Optional body of <c>POST /actors/{id}/keys</c>; every field is optional.</summary>
/// <param name="Scopes">Restriction to a subset of read, write, schema, admin; omitted or empty = the agent's membership rights apply in full.</param>
/// <param name="WorkspaceId">Restricts the key to one workspace.</param>
/// <param name="ExpiresAt">Expiry, in the future.</param>
internal sealed record CreateApiKeyBody(IReadOnlyList<string>? Scopes = null, Guid? WorkspaceId = null, DateTimeOffset? ExpiresAt = null);
