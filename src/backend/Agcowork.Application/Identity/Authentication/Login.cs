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
public sealed class Login(IAgcoworkDbContext db, IPasswordHasher passwordHasher, TimeProvider timeProvider)
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
                from actor in db.Actors
                join login in db.LocalLogins on actor.Id equals login.ActorId
                where actor.Email == email && actor.Kind == ActorKind.Human
                select new { Actor = actor, Login = login })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var verification = passwordHasher.Verify(candidate?.Login.PasswordHash, request.Password!);
        if (candidate is null || verification == PasswordVerification.Failed || !candidate.Actor.IsActive)
        {
            return new UnauthorizedError("Invalid credentials.");
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            candidate.Login.ChangePasswordHash(passwordHasher.Hash(request.Password!), timeProvider);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return AuthenticatedActor.ForSession(candidate.Actor);
    }
}
