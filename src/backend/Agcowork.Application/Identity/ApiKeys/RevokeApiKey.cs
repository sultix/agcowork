using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.ApiKeys;

public sealed record RevokeApiKeyRequest(Guid ApiKeyId);

/// <summary>Revokes a key (soft: the row stays for audit). Idempotent. Requires <c>instance:admin</c>.</summary>
public sealed class RevokeApiKey(IAgcoworkDbContext db, ICurrentActor currentActor, TimeProvider timeProvider) : IUseCase<RevokeApiKeyRequest>
{
    public async Task<Result> HandleAsync(RevokeApiKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentActor.RequireInstanceScope(InstanceScope.Admin) is { } denied)
        {
            return denied;
        }

        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == request.ApiKeyId, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return new NotFoundError("api key");
        }

        key.Revoke(timeProvider);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
