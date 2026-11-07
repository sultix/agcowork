using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.Passwords;

/// <param name="ActorId">Human whose password is set.</param>
/// <param name="NewPassword">The new password, 12 to 256 characters.</param>
/// <param name="MustChange">Whether the human should replace it after the next login (default true).</param>
public sealed record ResetPasswordRequest(Guid ActorId, string? NewPassword, bool MustChange = true);

/// <summary>
/// An instance admin sets the password of a human, e.g. a forgotten one or the first one. Creates the local login when the
/// human has none. Every existing session of the human becomes invalid. Requires <c>instance:admin</c>.
/// </summary>
public sealed class ResetPassword(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    IPasswordHasher passwordHasher,
    TimeProvider timeProvider) : IUseCase<ResetPasswordRequest>
{
    public async Task<Result> HandleAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentActor.RequireInstanceScope(InstanceScope.Admin) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
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

        var actor = await db.Actors.AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.ActorId, cancellationToken).ConfigureAwait(false);
        if (actor is null)
        {
            return new NotFoundError("actor");
        }

        if (actor.Kind != ActorKind.Human)
        {
            return new FieldError("actorId", ErrorCodes.NotHuman, "Only humans have passwords; agents authenticate with API keys (POST /api/v1/actors/{id}/keys).")
                .ToValidationError();
        }

        var hash = passwordHasher.Hash(request.NewPassword!);
        var login = await db.LocalLogins.SingleOrDefaultAsync(l => l.ActorId == actor.Id, cancellationToken).ConfigureAwait(false);
        if (login is null)
        {
            db.LocalLogins.Add(LocalLogin.Create(actor, hash, timeProvider, request.MustChange));
        }
        else
        {
            login.ChangePassword(hash, request.MustChange, timeProvider);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
