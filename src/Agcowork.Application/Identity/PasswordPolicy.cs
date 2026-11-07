namespace Agcowork.Application.Identity;

/// <summary>Length bounds for passwords set through the API. No composition rules (NIST SP 800-63B).</summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 256;
}
