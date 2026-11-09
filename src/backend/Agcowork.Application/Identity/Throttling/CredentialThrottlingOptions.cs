namespace Agcowork.Application.Identity.Throttling;

/// <summary>
/// Limits for failed credential checks (AP-043), bound from <c>Agcowork:CredentialThrottling</c>. The defaults here equal
/// those in <c>appsettings.json</c>.
/// </summary>
public sealed class CredentialThrottlingOptions
{
    public const string SectionName = "Agcowork:CredentialThrottling";

    /// <summary>Upper bound of <see cref="AttemptLimit.Window"/> and of every lockout: longer values are configuration mistakes.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(1);

    private static readonly TimeSpan[] GrowingLockouts = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    /// <summary>Switch for tests only: the host refuses to start with <c>false</c> outside Development.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Upper bound of tracked keys (all kinds together). When reached, expired keys are dropped first, then the ones that
    /// expire soonest; random emails cannot grow the memory beyond this.
    /// </summary>
    public int MaxEntries { get; set; } = 50_000;

    /// <summary>Failed logins per email address (known or not): 5 in 15 minutes, then 1, 5, 15 minutes.</summary>
    public AttemptLimit Account { get; set; } = new(5, TimeSpan.FromMinutes(15), GrowingLockouts);

    /// <summary>Failed logins per client address: 20 in 15 minutes, then 15 minutes.</summary>
    public AttemptLimit Address { get; set; } = new(20, TimeSpan.FromMinutes(15), [TimeSpan.FromMinutes(15)]);

    /// <summary>Wrong <c>currentPassword</c> per actor: like <see cref="Account"/>.</summary>
    public AttemptLimit PasswordChange { get; set; } = new(5, TimeSpan.FromMinutes(15), GrowingLockouts);

    public AttemptLimit For(AttemptKind kind) => kind switch
    {
        AttemptKind.LoginAccount => Account,
        AttemptKind.LoginAddress => Address,
        AttemptKind.PasswordChange => PasswordChange,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown attempt kind."),
    };

    /// <summary>Configuration errors, empty when valid. The host checks them on start.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaxEntries < 100)
        {
            errors.Add($"{SectionName}:MaxEntries must be at least 100.");
        }

        Check(nameof(Account), Account);
        Check(nameof(Address), Address);
        Check(nameof(PasswordChange), PasswordChange);
        return errors;

        void Check(string name, AttemptLimit? limit)
        {
            if (limit is null)
            {
                errors.Add($"{SectionName}:{name} is missing.");
                return;
            }

            if (limit.MaxFailures < 1)
            {
                errors.Add($"{SectionName}:{name}:MaxFailures must be at least 1.");
            }

            if (limit.Window <= TimeSpan.Zero || limit.Window > MaxDuration)
            {
                errors.Add($"{SectionName}:{name}:Window must be positive and at most {MaxDuration}.");
            }

            if (limit.EffectiveLockouts.Any(lockout => lockout <= TimeSpan.Zero || lockout > MaxDuration))
            {
                errors.Add($"{SectionName}:{name}:Lockouts must all be positive and at most {MaxDuration}.");
            }
        }
    }
}

/// <summary>
/// <see cref="MaxFailures"/> failures within <see cref="Window"/> lock the key. The n-th lock in a row lasts
/// <c>Lockouts[n]</c> (the last entry repeats); the row starts over after a quiet <see cref="Window"/> since the last
/// failure or lock, or after a success.
/// </summary>
public sealed class AttemptLimit
{
    private readonly TimeSpan[] _defaultLockouts;

    /// <summary>For the configuration binder; 5 failures in 15 minutes, lock 15 minutes.</summary>
    public AttemptLimit()
        : this(5, TimeSpan.FromMinutes(15), [TimeSpan.FromMinutes(15)])
    {
    }

    public AttemptLimit(int maxFailures, TimeSpan window, IReadOnlyList<TimeSpan> defaultLockouts)
    {
        ArgumentNullException.ThrowIfNull(defaultLockouts);
        MaxFailures = maxFailures;
        Window = window;
        _defaultLockouts = [.. defaultLockouts];
    }

    public int MaxFailures { get; set; }

    public TimeSpan Window { get; set; }

    /// <summary>
    /// Lock durations in order (from configuration, e.g. <c>["00:01:00", "00:05:00"]</c>); null or empty means the
    /// defaults of this limit. Null by default because the configuration binder appends to an existing array.
    /// </summary>
    public IReadOnlyList<TimeSpan>? Lockouts { get; set; }

    public IReadOnlyList<TimeSpan> EffectiveLockouts => Lockouts is { Count: > 0 } configured ? configured : _defaultLockouts;

    /// <summary>Duration of the lock that follows <paramref name="previousLocks"/> locks in a row.</summary>
    public TimeSpan LockoutAfter(int previousLocks)
    {
        var lockouts = EffectiveLockouts;
        return lockouts[Math.Clamp(previousLocks, 0, lockouts.Count - 1)];
    }
}
