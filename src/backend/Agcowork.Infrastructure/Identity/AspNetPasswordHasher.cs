using Agcowork.Application.Identity;
using AspNetIdentity = Microsoft.AspNetCore.Identity;

namespace Agcowork.Infrastructure.Identity;

/// <summary>
/// <see cref="IPasswordHasher"/> on top of ASP.NET Core Identity's <c>PasswordHasher</c>
/// (PBKDF2-HMAC-SHA512, salted, versioned format). Only the hasher is used, not ASP.NET Identity.
/// </summary>
internal sealed class AspNetPasswordHasher : IPasswordHasher
{
    // The ASP.NET hasher takes a user object but ignores it; one shared marker instance suffices.
    private static readonly PasswordSubject Subject = new();
    private static readonly AspNetIdentity.PasswordHasher<PasswordSubject> Hasher = new();

    // Computed once per process; verifying against it makes unknown accounts cost the same as known ones.
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(Subject, Guid.NewGuid().ToString("N")));

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return Hasher.HashPassword(Subject, password);
    }

    public PasswordVerification Verify(string? passwordHash, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var result = Hasher.VerifyHashedPassword(Subject, passwordHash ?? DummyHash.Value, password);
        if (passwordHash is null)
        {
            return PasswordVerification.Failed;
        }

        return result switch
        {
            AspNetIdentity.PasswordVerificationResult.Success => PasswordVerification.Success,
            AspNetIdentity.PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
            _ => PasswordVerification.Failed,
        };
    }

    private sealed class PasswordSubject;
}
