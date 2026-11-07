using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.Authentication;

/// <param name="ActorId">Actor id from the session cookie.</param>
/// <param name="PasswordStamp">
/// <see cref="LocalLogin.UpdatedAt"/> the session was issued for (<see cref="AuthenticatedActor.PasswordStamp"/>); null for
/// cookies issued before sessions carried it.
/// </param>
/// <param name="IssuedAt">Fallback for cookies without stamp: when the cookie was issued (whole seconds, rounded down).</param>
public sealed record AuthenticateSessionRequest(Guid ActorId, DateTimeOffset? PasswordStamp, DateTimeOffset? IssuedAt = null);

/// <summary>
/// Re-validates a cookie session on every request: the actor must still exist, be human, be active and have a
/// local login whose password is still the one the session was issued for (<see cref="LocalLogin.UpdatedAt"/> equals the
/// session's stamp). Instance scopes are re-read, so changes take effect immediately instead of at cookie expiry.
/// </summary>
/// <remarks>
/// A password change or reset therefore signs out every session of the actor without a session table; the adapter
/// issues a fresh cookie to the caller who changed their own password. The stamp is the value read together with the
/// verified hash, not the issue time: a login that read the old hash while a change committed must not outlive it.
/// </remarks>
public sealed class AuthenticateSession(IAgcoworkDbContext db) : IUseCase<AuthenticateSessionRequest, AuthenticatedActor>
{
    public async Task<Result<AuthenticatedActor>> HandleAsync(AuthenticateSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // One round trip: the actor and the time its password last changed.
        var match = await (
                from actor in db.Actors.AsNoTracking()
                join login in db.LocalLogins.AsNoTracking() on actor.Id equals login.ActorId into logins
                from login in logins.DefaultIfEmpty()
                where actor.Id == request.ActorId
                select new { Actor = actor, PasswordChangedAt = login == null ? (DateTimeOffset?)null : login.UpdatedAt })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (match is null || match.Actor.Kind != ActorKind.Human || !match.Actor.IsActive)
        {
            return new UnauthorizedError("Session actor missing or inactive.");
        }

        // Fail closed: today every session comes from a password login, so a session without a local login cannot be
        // legitimate (the login was removed). Sessions from other sources (OIDC) need their own rule here.
        if (match.PasswordChangedAt is not { } changedAt)
        {
            return new UnauthorizedError("Session actor has no local login.");
        }

        if (!IsCurrent(request.PasswordStamp, request.IssuedAt, changedAt))
        {
            return new UnauthorizedError("Session belongs to an earlier password.");
        }

        return AuthenticatedActor.ForSession(match.Actor, changedAt);
    }

    /// <summary>
    /// Whether a session still belongs to the password that changed last at <paramref name="passwordChangedAt"/>.
    /// With a stamp: exact equality (both values are microseconds, which PostgreSQL stores losslessly). Without one
    /// (cookies from before stamps): issued at or after the change; <paramref name="issuedAt"/> is rounded down to
    /// seconds, so such a cookie can only read as older than it is (fail safe). Neither known: not current.
    /// </summary>
    public static bool IsCurrent(DateTimeOffset? passwordStamp, DateTimeOffset? issuedAt, DateTimeOffset passwordChangedAt) =>
        passwordStamp is { } stamp
            ? stamp.UtcTicks == passwordChangedAt.UtcTicks
            : issuedAt is { } issued && issued >= passwordChangedAt;
}
