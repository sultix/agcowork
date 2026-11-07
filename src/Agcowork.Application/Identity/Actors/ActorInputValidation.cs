using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Identity;

namespace Agcowork.Application.Identity.Actors;

/// <summary>Field rules shared by <see cref="CreateActor"/> and <see cref="UpdateActor"/>. Mirrors the guards in <see cref="Actor"/>.</summary>
internal static class ActorInputValidation
{
    public static void Name(FieldErrors errors, string? name)
    {
        if (errors.Required("name", name))
        {
            errors.MaxLength("name", name, Actor.NameMaxLength);
        }
    }

    public static void Role(FieldErrors errors, string? role) => errors.MaxLength("role", role, Actor.RoleMaxLength);

    public static void Email(FieldErrors errors, string? email)
    {
        if (email is null)
        {
            return;
        }

        if (email.Trim().Length > Actor.EmailMaxLength)
        {
            errors.Add("email", ErrorCodes.TooLong, $"'email' must not exceed {Actor.EmailMaxLength} characters.");
        }
        else if (!Actor.IsValidEmail(email))
        {
            errors.Add("email", ErrorCodes.InvalidFormat, "'email' must be a plain address like 'name@example.com'.");
        }
    }

    public static void InstanceScopes(FieldErrors errors, IReadOnlyList<string>? scopes) =>
        errors.AllOneOf("instanceScopes", scopes, InstanceScope.All);
}
