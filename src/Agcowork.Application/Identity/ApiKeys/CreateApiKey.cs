using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.ApiKeys;

/// <param name="ActorId">Agent that will own the key.</param>
/// <param name="Scopes">Optional restriction (<c>read</c>, <c>write</c>, <c>schema</c>, <c>admin</c>); empty = membership rights apply in full.</param>
/// <param name="WorkspaceId">Optional restriction to one workspace.</param>
/// <param name="ExpiresAt">Optional expiry; must lie in the future.</param>
public sealed record CreateApiKeyRequest(
    Guid ActorId,
    IReadOnlyList<string>? Scopes = null,
    Guid? WorkspaceId = null,
    DateTimeOffset? ExpiresAt = null);

/// <summary>Issues a new API key for an agent and returns the plain-text key once. Requires <c>instance:admin</c>.</summary>
public sealed class CreateApiKey(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    IApiKeyGenerator generator,
    TimeProvider timeProvider) : IUseCase<CreateApiKeyRequest, CreatedApiKeyDto>
{
    public async Task<Result<CreatedApiKeyDto>> HandleAsync(CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentActor.RequireInstanceScope(InstanceScope.Admin) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        errors.AllOneOf("scopes", request.Scopes, KeyScope.All);
        if (request.ExpiresAt is { } expiresAt && expiresAt <= timeProvider.GetUtcNow())
        {
            errors.Add("expiresAt", ErrorCodes.OutOfRange, "'expiresAt' must lie in the future.");
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        var actor = await db.Actors.SingleOrDefaultAsync(a => a.Id == request.ActorId, cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return new NotFoundError("actor");
        }

        if (actor.Kind != ActorKind.Agent)
        {
            return new FieldError("actorId", ErrorCodes.NotAllowed, "API keys can only be issued to agent actors; humans log in.")
                .ToValidationError();
        }

        var generated = generator.Generate();
        var key = ApiKey.Issue(actor, generated.Prefix, generated.SecretHash, request.Scopes ?? [], request.WorkspaceId, request.ExpiresAt, timeProvider);
        db.ApiKeys.Add(key);
        if (await db.SaveChangesOrConflictAsync(cancellationToken, IdentityConflicts.ApiKeyPrefix).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        return new CreatedApiKeyDto(generated.Token, ApiKeyDto.From(key));
    }
}
