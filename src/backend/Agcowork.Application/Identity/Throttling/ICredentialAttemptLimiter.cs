namespace Agcowork.Application.Identity.Throttling;

/// <summary>
/// Brakes password guessing (AP-043): counts failed credential checks per <see cref="AttemptKey"/> and locks a key after
/// too many of them, with growing lockouts (<see cref="CredentialThrottlingOptions"/>). A use case reserves an attempt
/// <em>before</em> it verifies a password and settles it afterwards; a denied attempt must not verify, so a lock also spares
/// the CPU the deliberately expensive hash. Implemented in Infrastructure (in memory, single instance).
/// </summary>
public interface ICredentialAttemptLimiter
{
    /// <summary>
    /// Reserves one attempt for every key. Returns null when any key is locked, or when so many attempts of a key are still
    /// in flight that they could exceed its limit; <paramref name="retryAfter"/> then says how long to wait.
    /// </summary>
    ICredentialAttempt? TryBegin(IReadOnlyList<AttemptKey> keys, out TimeSpan retryAfter);
}

/// <summary>
/// A reserved attempt. Settle it once with <see cref="Failed"/> or <see cref="Succeeded"/>; disposing an unsettled attempt
/// (exception, cancellation) releases the reservation without counting it.
/// </summary>
public interface ICredentialAttempt : IDisposable
{
    /// <summary>The credential was wrong: counts as a failure for every key and may lock it.</summary>
    void Failed();

    /// <summary>
    /// The credential was right: resets the keys that reset on success (account, password change); the client address keeps
    /// its failures, so an attacker cannot clear them by logging into an account of their own.
    /// </summary>
    void Succeeded();
}

/// <summary>Never limits. For hosts and tests that run without throttling.</summary>
public sealed class UnlimitedCredentialAttempts : ICredentialAttemptLimiter, ICredentialAttempt
{
    public static UnlimitedCredentialAttempts Instance { get; } = new();

    private UnlimitedCredentialAttempts()
    {
    }

    public ICredentialAttempt? TryBegin(IReadOnlyList<AttemptKey> keys, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        return this;
    }

    public void Failed()
    {
    }

    public void Succeeded()
    {
    }

    public void Dispose()
    {
    }
}
