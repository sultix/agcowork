using System.Net;
using Agcowork.Application.Identity.Throttling;
using Agcowork.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Identity;

/// <summary>Limiter logic of AP-043 against a fake clock: window, growing lockouts, reset, in-flight attempts, table bound.</summary>
public sealed class CredentialAttemptLimiterTests
{
    private static readonly AttemptKey Account = AttemptKey.LoginAccount("ada@example.com");
    private static readonly AttemptKey Address = AttemptKey.LoginAddress(IPAddress.Parse("203.0.113.7"));

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TryBegin_FiveFailures_SixthDeniedForFirstLockout()
    {
        var limiter = Limiters.Create(_clock);

        for (var i = 0; i < 5; i++)
        {
            Fail(limiter, Account);
        }

        Assert.Null(limiter.TryBegin([Account], out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(1), retryAfter);
    }

    [Fact]
    public void TryBegin_FourFailures_FifthAllowed()
    {
        var limiter = Limiters.Create(_clock);
        for (var i = 0; i < 4; i++)
        {
            Fail(limiter, Account);
        }

        using var attempt = limiter.TryBegin([Account], out var retryAfter);

        Assert.NotNull(attempt);
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    [Fact]
    public void TryBegin_LockExpired_AllowedAgain()
    {
        var limiter = Limiters.Create(_clock);
        LockOnce(limiter, Account);

        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(limiter.TryBegin([Account], out var remaining));
        Assert.Equal(TimeSpan.FromSeconds(1), remaining);

        _clock.Advance(TimeSpan.FromSeconds(1));
        using var attempt = limiter.TryBegin([Account], out _);
        Assert.NotNull(attempt);
    }

    [Fact]
    public void TryBegin_LocksInARow_LockoutsGrowAndLastRepeats()
    {
        var limiter = Limiters.Create(_clock);
        TimeSpan[] expected = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)];

        foreach (var lockout in expected)
        {
            LockOnce(limiter, Account);
            Assert.Null(limiter.TryBegin([Account], out var retryAfter));
            Assert.Equal(lockout, retryAfter);
            _clock.Advance(lockout);
        }
    }

    [Fact]
    public void TryBegin_QuietForAWindowAfterLock_RowStartsOver()
    {
        var limiter = Limiters.Create(_clock);
        LockOnce(limiter, Account);
        _clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromMinutes(15));

        LockOnce(limiter, Account);

        Assert.Null(limiter.TryBegin([Account], out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(1), retryAfter);
    }

    [Fact]
    public void TryBegin_FailuresOlderThanWindow_DoNotCount()
    {
        var limiter = Limiters.Create(_clock);
        for (var i = 0; i < 4; i++)
        {
            Fail(limiter, Account);
        }

        _clock.Advance(TimeSpan.FromMinutes(15));
        for (var i = 0; i < 4; i++)
        {
            Fail(limiter, Account);
        }

        using var attempt = limiter.TryBegin([Account], out _);
        Assert.NotNull(attempt);
    }

    [Fact]
    public void TryBegin_SlidingWindow_OldestFailureDropsOutSeparately()
    {
        var limiter = Limiters.Create(_clock);
        Fail(limiter, Account);
        _clock.Advance(TimeSpan.FromMinutes(10));
        for (var i = 0; i < 3; i++)
        {
            Fail(limiter, Account);
        }

        // 15 minutes after the first failure only the three later ones are in the window: two more are allowed.
        _clock.Advance(TimeSpan.FromMinutes(5));
        Fail(limiter, Account);
        using (var attempt = limiter.TryBegin([Account], out _))
        {
            Assert.NotNull(attempt);
            attempt.Failed();
        }

        Assert.Null(limiter.TryBegin([Account], out _));
    }

    [Fact]
    public void Succeeded_ResetsAccountButNotAddress()
    {
        var limiter = Limiters.Create(_clock, o => o.Address.MaxFailures = 5);
        for (var i = 0; i < 4; i++)
        {
            Fail(limiter, Account, Address);
        }

        using (var success = limiter.TryBegin([Account, Address], out _))
        {
            Assert.NotNull(success);
            success.Succeeded();
        }

        // The address still holds its four failures (the fifth locks it), the account starts over at one.
        Fail(limiter, Account, Address);
        Assert.Null(limiter.TryBegin([Address], out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(15), retryAfter);
        using var accountOnly = limiter.TryBegin([Account], out _);
        Assert.NotNull(accountOnly);
    }

    [Fact]
    public void Succeeded_AfterParallelFailures_ResetsThem()
    {
        var limiter = Limiters.Create(_clock);
        using var early = limiter.TryBegin([Account], out _);
        for (var i = 0; i < 4; i++)
        {
            Fail(limiter, Account);
        }

        // Four failures plus the attempt in flight fill the limit.
        Assert.Null(limiter.TryBegin([Account], out _));
        Assert.NotNull(early);
        early.Succeeded();

        for (var i = 0; i < 4; i++)
        {
            Fail(limiter, Account);
        }

        using var next = limiter.TryBegin([Account], out _);
        Assert.NotNull(next);
    }

    [Fact]
    public void TryBegin_AnyKeyLocked_DeniedWithLongestWait()
    {
        var limiter = Limiters.Create(_clock);
        LockOnce(limiter, Account);
        var other = AttemptKey.LoginAccount("bob@example.com");

        Assert.Null(limiter.TryBegin([other, Account], out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(1), retryAfter);
        using var unrelated = limiter.TryBegin([other], out _);
        Assert.NotNull(unrelated);
    }

    [Fact]
    public void TryBegin_AttemptsInFlightFillLimit_FurtherDeniedBriefly()
    {
        var limiter = Limiters.Create(_clock);
        var inFlight = Enumerable.Range(0, 5).Select(i => limiter.TryBegin([Account], out _)).ToList();

        Assert.All(inFlight, Assert.NotNull);
        Assert.Null(limiter.TryBegin([Account], out var retryAfter));
        Assert.Equal(InMemoryCredentialAttemptLimiter.InFlightRetryAfter, retryAfter);

        // An attempt that ends without outcome (exception, cancellation) frees its place without counting.
        inFlight[0]!.Dispose();
        using var next = limiter.TryBegin([Account], out _);
        Assert.NotNull(next);
        foreach (var attempt in inFlight)
        {
            attempt!.Dispose();
        }
    }

    [Fact]
    public void Settle_Twice_CountsOnce()
    {
        var limiter = Limiters.Create(_clock, o => o.Account.MaxFailures = 2);
        var attempt = limiter.TryBegin([Account], out _)!;

        attempt.Failed();
        attempt.Failed();
        attempt.Dispose();

        using var second = limiter.TryBegin([Account], out _);
        Assert.NotNull(second);
    }

    [Fact]
    public void TryBegin_ManyRandomKeys_TableStaysBounded()
    {
        var limiter = Limiters.Create(_clock, o => o.MaxEntries = 100);

        for (var i = 0; i < 1_000; i++)
        {
            Fail(limiter, AttemptKey.LoginAccount($"random-{i}@example.com"));
        }

        Assert.InRange(limiter.Count, 1, 100);
    }

    [Fact]
    public void TryBegin_TableFull_LockedKeyOutlivesUnlockedOnes()
    {
        var logger = new ListLogger<InMemoryCredentialAttemptLimiter>();
        var limiter = Limiters.Create(_clock, o => o.MaxEntries = 100, logger);
        LockOnce(limiter, Account);

        for (var i = 0; i < 500; i++)
        {
            Fail(limiter, AttemptKey.LoginAccount($"random-{i}@example.com"));
        }

        Assert.Null(limiter.TryBegin([Account], out _));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("table full", StringComparison.Ordinal));
    }

    [Fact]
    public void TryBegin_TableFull_LockedKeyIsNeverDroppedEvenIfItExpiresFirst()
    {
        // A short window lets the locked account key expire before the address keys: by expiry alone it would go first.
        var limiter = Limiters.Create(_clock, o =>
        {
            o.MaxEntries = 100;
            o.Account.Window = TimeSpan.FromMinutes(1);
            o.Account.Lockouts = [TimeSpan.FromMinutes(5)];
        });
        LockOnce(limiter, Account);

        for (var i = 0; i < 500; i++)
        {
            Fail(limiter, AttemptKey.LoginAddress(new IPAddress(0x0A000000 + i)));
        }

        Assert.Null(limiter.TryBegin([Account], out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(5), retryAfter);
        Assert.InRange(limiter.Count, 1, 100);
    }

    [Fact]
    public void TryBegin_TableFullOfLockedKeys_RefusesNewKeysUntilFirstLockEnds()
    {
        var logger = new ListLogger<InMemoryCredentialAttemptLimiter>();
        var limiter = Limiters.Create(_clock, o => o.MaxEntries = 100, logger);
        var locked = Enumerable.Range(0, 100).Select(i => AttemptKey.LoginAccount($"locked-{i}@example.com")).ToList();
        foreach (var key in locked)
        {
            LockOnce(limiter, key);
        }

        Assert.Null(limiter.TryBegin([AttemptKey.LoginAccount("new@example.com")], out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(1), retryAfter);
        Assert.Equal(100, limiter.Count);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("all locked", StringComparison.Ordinal));

        // Known keys answer as before; once the locks end, new keys get room again and the bound holds.
        Assert.Null(limiter.TryBegin([locked[0]], out var known));
        Assert.Equal(TimeSpan.FromMinutes(1), known);
        _clock.Advance(TimeSpan.FromMinutes(1));
        using var fresh = limiter.TryBegin([AttemptKey.LoginAccount("new@example.com")], out _);
        Assert.NotNull(fresh);
        Assert.InRange(limiter.Count, 1, 100);
    }

    [Fact]
    public void TryBegin_TableFullOfAttemptsInFlight_RefusesNewKeysBriefly()
    {
        var limiter = Limiters.Create(_clock, o => o.MaxEntries = 100);
        var inFlight = Enumerable.Range(0, 100)
            .Select(i => limiter.TryBegin([AttemptKey.LoginAccount($"busy-{i}@example.com")], out _)!)
            .ToList();

        Assert.Null(limiter.TryBegin([Account], out var retryAfter));
        Assert.Equal(InMemoryCredentialAttemptLimiter.InFlightRetryAfter, retryAfter);

        inFlight[0].Dispose();
        using (var next = limiter.TryBegin([Account], out _))
        {
            Assert.NotNull(next);
        }

        Assert.Equal(100, limiter.Count);
        foreach (var attempt in inFlight)
        {
            attempt.Dispose();
        }
    }

    [Fact]
    public void TryBegin_ExpiredKeys_AreSweptAfterTheirWindow()
    {
        var limiter = Limiters.Create(_clock);
        for (var i = 0; i < 10; i++)
        {
            Fail(limiter, AttemptKey.LoginAccount($"user-{i}@example.com"));
        }

        _clock.Advance(TimeSpan.FromMinutes(16));
        using (limiter.TryBegin([Account], out _))
        {
            Assert.Equal(1, limiter.Count);
        }
    }

    [Fact]
    public void TryBegin_Disabled_NeverDenies()
    {
        var limiter = Limiters.Create(_clock, o => o.Enabled = false);

        for (var i = 0; i < 50; i++)
        {
            Fail(limiter, Account);
        }

        using var attempt = limiter.TryBegin([Account], out _);
        Assert.NotNull(attempt);
        Assert.Equal(0, limiter.Count);
    }

    [Fact]
    public void Failed_Locking_LogsPseudonymNotEmail()
    {
        var logger = new ListLogger<InMemoryCredentialAttemptLimiter>();
        var limiter = Limiters.Create(_clock, logger: logger);

        LockOnce(limiter, Account);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(AttemptKey.Pseudonym("ada@example.com"), entry.Message, StringComparison.Ordinal);
        Assert.Contains("LoginAccount", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ada", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void LoginAddress_Bucket(string? address, string expected)
    {
        var key = AttemptKey.LoginAddress(address is null ? null : IPAddress.Parse(address));

        Assert.Equal(new AttemptKey(AttemptKind.LoginAddress, expected), key);
    }

    [Fact]
    public void LoginAccount_PseudonymOfFixedSizeWithoutEmail()
    {
        var key = AttemptKey.LoginAccount(new string('a', 500) + "@example.com");

        Assert.Equal(22, key.Subject.Length);
        Assert.DoesNotContain("example", key.Subject, StringComparison.Ordinal);
        Assert.Equal(key, AttemptKey.LoginAccount(new string('a', 500) + "@example.com"));
        Assert.NotEqual(key, AttemptKey.LoginAccount("b@example.com"));
    }

    [Fact]
    public void Options_BoundFromConfiguration_ReplaceDefaultLockouts()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Account:MaxFailures"] = "3",
                ["Account:Lockouts:0"] = "00:02:00",
                ["Account:Lockouts:1"] = "00:10:00",
            })
            .Build();
        var options = new CredentialThrottlingOptions();

        configuration.Bind(options);

        Assert.Equal(3, options.Account.MaxFailures);
        Assert.Equal([TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)], options.Account.EffectiveLockouts);
        Assert.Equal([TimeSpan.FromMinutes(15)], options.Address.EffectiveLockouts);
        Assert.Equal(20, options.Address.MaxFailures);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Options_Invalid_NamesEveryProblem()
    {
        var options = new CredentialThrottlingOptions { MaxEntries = 1 };
        options.Account.MaxFailures = 0;
        options.Address.Window = TimeSpan.Zero;
        options.PasswordChange.Lockouts = [TimeSpan.FromMinutes(1), TimeSpan.Zero];

        var errors = options.Validate();

        Assert.Equal(4, errors.Count);
    }

    [Fact]
    public void Options_DurationsOverOneDay_Invalid()
    {
        var options = new CredentialThrottlingOptions();
        options.Account.Window = TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1);
        options.Address.Lockouts = [TimeSpan.FromMinutes(15), TimeSpan.FromDays(2)];
        options.PasswordChange.Window = TimeSpan.FromDays(1);
        options.PasswordChange.Lockouts = [TimeSpan.FromDays(1)];

        var errors = options.Validate();

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("Account:Window", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Address:Lockouts", StringComparison.Ordinal));
    }

    private static void Fail(InMemoryCredentialAttemptLimiter limiter, params AttemptKey[] keys)
    {
        using var attempt = limiter.TryBegin(keys, out _);
        Assert.NotNull(attempt);
        attempt.Failed();
    }

    private static void LockOnce(InMemoryCredentialAttemptLimiter limiter, AttemptKey key)
    {
        for (var i = 0; i < 5; i++)
        {
            Fail(limiter, key);
        }
    }
}
