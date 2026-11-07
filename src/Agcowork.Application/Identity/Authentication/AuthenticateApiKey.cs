using System.Security.Cryptography;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.Authentication;

/// <param name="Token">The bearer token as sent by the client.</param>
public sealed record AuthenticateApiKeyRequest(string Token);

/// <summary>
/// Resolves a bearer token to its agent: prefix lookup, constant-time hash comparison, then
/// revoked / expired / inactive checks. Updates <see cref="ApiKey.LastUsedAt"/> at most once per
/// <see cref="ApiKey.LastUsedResolution"/>. All failures are the same <see cref="UnauthorizedError"/>;
/// its reason is for logs only.
/// </summary>
public sealed class AuthenticateApiKey(IAgcoworkDbContext db, IApiKeyGenerator generator, TimeProvider timeProvider)
    : IUseCase<AuthenticateApiKeyRequest, AuthenticatedActor>
{
    public async Task<Result<AuthenticatedActor>> HandleAsync(AuthenticateApiKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ApiKeyToken.TryParse(request.Token, out var prefix, out var secret))
        {
            return new UnauthorizedError("Malformed API key.");
        }

        var match = await (
                from key in db.ApiKeys.AsNoTracking()
                join actor in db.Actors.AsNoTracking() on key.ActorId equals actor.Id
                where key.Prefix == prefix
                select new { Key = key, Actor = actor })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Hash even when the prefix is unknown, so response time does not reveal which prefixes exist.
        var presentedHash = generator.HashSecret(secret);
        if (match is null || !CryptographicOperations.FixedTimeEquals(presentedHash, match.Key.SecretHash))
        {
            return new UnauthorizedError("Unknown API key.");
        }

        var now = timeProvider.GetUtcNow();
        var status = match.Key.GetStatus(match.Actor, now);
        if (status != ApiKeyStatus.Active)
        {
            return new UnauthorizedError($"API key rejected: {status}.");
        }

        // Runs on every request: no tracking, and a single conditional UPDATE instead of load-modify-save.
        // The WHERE clause repeats the resolution check so parallel requests write at most once.
        if (match.Key.IsLastUsedUpdateDue(now))
        {
            var dueBefore = now - ApiKey.LastUsedResolution;
            await db.ApiKeys
                .Where(k => k.Id == match.Key.Id && (k.LastUsedAt == null || k.LastUsedAt <= dueBefore))
                .ExecuteUpdateAsync(setters => setters.SetProperty(k => k.LastUsedAt, now), cancellationToken)
                .ConfigureAwait(false);
        }

        return AuthenticatedActor.ForApiKey(match.Actor, match.Key);
    }
}
