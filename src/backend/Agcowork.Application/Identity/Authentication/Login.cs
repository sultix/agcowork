using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.Authentication;

public sealed record LoginRequest(string? Email, string? Password);

/// <summary>
/// Verifies email and password of a human. Every failure (unknown email, wrong password, inactive,
/// agent) yields the same <see cref="UnauthorizedError"/> and costs one hash verification.
/// The adapter establishes the session (cookie).
/// </summary>
public sealed class Login(IAgcoworkDbContext db, IPasswordHasher passwordHasher)
    : IUseCase<LoginRequest, AuthenticatedActor>
{
    public async Task<Result<AuthenticatedActor>> HandleAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new FieldErrors();
        errors.Required("email", request.Email);
        if (string.IsNullOrEmpty(request.Password))
        {
            errors.Add("password", ErrorCodes.Required, "'password' is required.");
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        var email = Actor.NormalizeEmail(request.Email!);
        var candidate = await (
                from actor in db.Actors.AsNoTracking()
                join login in db.LocalLogins.AsNoTracking() on actor.Id equals login.ActorId
                where actor.Email == email && actor.Kind == ActorKind.Human
                select new { Actor = actor, login.PasswordHash, login.UpdatedAt })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var verification = passwordHasher.Verify(candidate?.PasswordHash, request.Password!);
        if (candidate is null || verification == PasswordVerification.Failed || !candidate.Actor.IsActive)
        {
            return new UnauthorizedError("Invalid credentials.");
        }

        // Same password, newer hash parameters: not a credential change, so UpdatedAt and other sessions stay. Conditional
        // on the hash that was verified: if the password changed meanwhile, the rehash is dropped instead of writing the old
        // password back over the new one.
        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            var verifiedHash = candidate.PasswordHash;
            var rehashed = passwordHasher.Hash(request.Password!);
            await db.LocalLogins
                .Where(l => l.ActorId == candidate.Actor.Id && l.PasswordHash == verifiedHash)
                .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.PasswordHash, rehashed), cancellationToken)
                .ConfigureAwait(false);
        }

        // The session is bound to the password state that was verified (read together with its hash), not to "now": a
        // password change committed while this login was hashing must still end this session.
        return AuthenticatedActor.ForSession(candidate.Actor, candidate.UpdatedAt);
    }
}
