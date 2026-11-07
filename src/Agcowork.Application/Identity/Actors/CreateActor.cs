using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Identity.Actors;

/// <param name="Kind"><c>human</c> or <c>agent</c>.</param>
/// <param name="Name">Display name, max. 200 characters.</param>
/// <param name="Role">Free-text role such as "Tech-Lead"; not a permission.</param>
/// <param name="Email">Required for humans, optional for agents. Unique across the installation.</param>
/// <param name="Password">Initial password for a human's local login (optional; without it the human cannot log in yet). Not allowed for agents.</param>
/// <param name="InstanceScopes">Subset of <c>instance:admin</c>, <c>workspaces:create</c>.</param>
public sealed record CreateActorRequest(
    string? Kind,
    string? Name,
    string? Role = null,
    string? Email = null,
    string? Password = null,
    IReadOnlyList<string>? InstanceScopes = null);

/// <summary>Creates a human or agent actor. Requires <c>instance:admin</c>.</summary>
public sealed class CreateActor(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    IPasswordHasher passwordHasher,
    TimeProvider timeProvider) : IUseCase<CreateActorRequest, ActorDto>
{
    public async Task<Result<ActorDto>> HandleAsync(CreateActorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Authorize. 2. Validate input. 3. Check state. 4. Change and save. 5. Map.
        if (currentActor.RequireInstanceScope(InstanceScope.Admin) is { } denied)
        {
            return denied;
        }

        // Normalize once; validation, uniqueness check and factory all see the same value.
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        var errors = Validate(request, email, out var kind);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var normalizedEmail = email is null ? null : Actor.NormalizeEmail(email);
        if (normalizedEmail is not null && await db.Actors.AnyAsync(a => a.Email == normalizedEmail, cancellationToken).ConfigureAwait(false))
        {
            return IdentityConflicts.ActorEmail.Error;
        }

        var scopes = request.InstanceScopes ?? [];
        var actor = kind == ActorKind.Human
            ? Actor.CreateHuman(request.Name!, email!, request.Role, scopes, timeProvider)
            : Actor.CreateAgent(request.Name!, request.Role, email, scopes, timeProvider);
        db.Actors.Add(actor);

        if (request.Password is not null)
        {
            db.LocalLogins.Add(LocalLogin.Create(actor, passwordHasher.Hash(request.Password), timeProvider));
        }

        // The pre-check above is the fast path; this catches the race between check and insert.
        if (await db.SaveChangesOrConflictAsync(cancellationToken, IdentityConflicts.ActorEmail).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        return ActorDto.From(actor);
    }

    private static FieldErrors Validate(CreateActorRequest request, string? email, out ActorKind? kind)
    {
        var errors = new FieldErrors();

        kind = null;
        if (errors.Required("kind", request.Kind))
        {
            kind = ActorKinds.Parse(request.Kind);
            if (kind is null)
            {
                errors.OneOf("kind", request.Kind, ActorKinds.All);
            }
        }

        ActorInputValidation.Name(errors, request.Name);
        ActorInputValidation.Role(errors, request.Role);

        if (kind == ActorKind.Human)
        {
            errors.Required("email", email);
        }

        ActorInputValidation.Email(errors, email);

        if (request.Password is not null)
        {
            if (kind == ActorKind.Agent)
            {
                errors.Add("password", ErrorCodes.NotAllowed, "Agents authenticate with API keys and cannot have a password.");
            }
            else if (request.Password.Length < PasswordPolicy.MinLength)
            {
                errors.Add("password", ErrorCodes.TooShort, $"'password' must have at least {PasswordPolicy.MinLength} characters.");
            }
            else if (request.Password.Length > PasswordPolicy.MaxLength)
            {
                errors.Add("password", ErrorCodes.TooLong, $"'password' must not exceed {PasswordPolicy.MaxLength} characters.");
            }
        }

        ActorInputValidation.InstanceScopes(errors, request.InstanceScopes);
        return errors;
    }
}
