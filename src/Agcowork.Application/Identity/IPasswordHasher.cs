namespace Agcowork.Application.Identity;

/// <summary>Hashes and verifies passwords of local logins. Implemented in Infrastructure.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>
    /// Verifies <paramref name="password"/> against <paramref name="passwordHash"/>. Passing null runs
    /// a verification of equal cost against a dummy hash and fails, so unknown accounts cannot be
    /// told apart by timing.
    /// </summary>
    PasswordVerification Verify(string? passwordHash, string password);
}

public enum PasswordVerification
{
    Failed = 0,
    Success = 1,

    /// <summary>Correct, but the hash uses outdated parameters and should be replaced.</summary>
    SuccessRehashNeeded = 2,
}
