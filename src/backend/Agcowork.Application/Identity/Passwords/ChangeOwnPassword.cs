using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Identity.Authentication;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.Passwords;

/// <param name="CurrentPassword">The password the caller logged in with.</param>
/// <param name="NewPassword">The new password, 12 to 256 characters, different from the current one.</param>
public sealed record ChangeOwnPasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>
/// A human changes their own password after proving the current one. Clears <see cref="LocalLogin.MustChangePassword"/>
/// and moves <see cref="LocalLogin.UpdatedAt"/>, which invalidates every existing session of the actor; the adapter
/// issues a fresh session to the caller (returned principal). Agents and API-key calls are refused: they have no password.
/// </summary>
public sealed class ChangeOwnPassword(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    IPasswordHasher passwordHasher,
    TimeProvider timeProvider) : IUseCase<ChangeOwnPasswordRequest, AuthenticatedActor>
{
    public async Task<Result<AuthenticatedActor>> HandleAsync(ChangeOwnPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentActor.RequireAuthenticated() is { } unauthenticated)
        {
            return unauthenticated;
        }

        if (currentActor.Kind != ActorKind.Human || currentActor.ApiKeyId is not null)
        {
            return new ForbiddenError(
                "Only a human signed in with email and password (session cookie) can change their password. "
                + "Agents have no password; they authenticate with API keys, which an instance admin issues and revokes.");
        }

        var errors = new FieldErrors();
        if (string.IsNullOrEmpty(request.CurrentPassword))
        {
            errors.Add("currentPassword", ErrorCodes.Required, "'currentPassword' is required.");
        }

        if (string.IsNullOrEmpty(request.NewPassword))
        {
            errors.Add("newPassword", ErrorCodes.Required, "'newPassword' is required.");
        }
        else
        {
            PasswordPolicy.CheckLength(errors, "newPassword", request.NewPassword);
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        var match = await (
                from actor in db.Actors
                join login in db.LocalLogins on actor.Id equals login.ActorId
                where actor.Id == currentActor.Id
                select new { Actor = actor, Login = login })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Without a login, Verify runs against a dummy hash. This keeps the code path uniform; it hides little, since the
        // caller is authenticated and knows whether they log in with a password.
        var verification = passwordHasher.Verify(match?.Login.PasswordHash, request.CurrentPassword!);
        if (match is null || verification == PasswordVerification.Failed)
        {
            return new FieldError("currentPassword", ErrorCodes.InvalidCredentials, "'currentPassword' is not your current password.")
                .ToValidationError();
        }

        if (string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
        {
            return new FieldError("newPassword", ErrorCodes.Unchanged, "'newPassword' must differ from the current password.")
                .ToValidationError();
        }

        match.Login.ChangePassword(passwordHasher.Hash(request.NewPassword!), mustChangePassword: false, timeProvider);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The new session is bound to exactly the state just written; a later change or reset ends it like any other.
        return AuthenticatedActor.ForSession(match.Actor, match.Login.UpdatedAt);
    }
}
