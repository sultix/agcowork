using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;

namespace Agcowork.Application.Identity;

/// <summary>Length bounds for passwords set through the API. No composition rules (NIST SP 800-63B).</summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 256;

    /// <summary>Adds <c>too_short</c> / <c>too_long</c> for <paramref name="field"/>. Returns true when within bounds.</summary>
    public static bool CheckLength(FieldErrors errors, string field, string password)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < MinLength)
        {
            errors.Add(field, ErrorCodes.TooShort, $"'{field}' must have at least {MinLength} characters.");
            return false;
        }

        if (password.Length > MaxLength)
        {
            errors.Add(field, ErrorCodes.TooLong, $"'{field}' must not exceed {MaxLength} characters.");
            return false;
        }

        return true;
    }
}
