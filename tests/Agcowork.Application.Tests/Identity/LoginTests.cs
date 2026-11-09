using System.Net;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Identity;
using Agcowork.Application.Identity.Authentication;
using Agcowork.Application.Identity.Throttling;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Identity;

public sealed class LoginTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private const string Password = "correct horse battery";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly AspNetPasswordHasher _hasher = new();

    [Fact]
    public async Task HandleAsync_CorrectCredentials_ReturnsHumanWithScopes()
    {
        var human = await SeedHumanAsync();

        var result = await Sut().HandleAsync(new LoginRequest(" ADA@example.com ", Password), TestContext.Current.CancellationToken);

        Assert.Equal(human.Id, result.Value.ActorId);
        Assert.Equal([InstanceScope.Admin], result.Value.InstanceScopes);
        Assert.Null(result.Value.ApiKeyId);
    }

    [Fact]
    public async Task HandleAsync_WrongPassword_Unauthorized()
    {
        await SeedHumanAsync();

        var result = await Sut().HandleAsync(new LoginRequest("ada@example.com", "wrong password!"), TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_UnknownEmail_SameUnauthorized()
    {
        await SeedHumanAsync();

        var result = await Sut().HandleAsync(new LoginRequest("nobody@example.com", Password), TestContext.Current.CancellationToken);

        Assert.Equal(new UnauthorizedError("Invalid credentials."), result.Error);
    }

    [Fact]
    public async Task HandleAsync_InactiveHuman_Unauthorized()
    {
        var human = await SeedHumanAsync();
        human.Deactivate();
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Sut().HandleAsync(new LoginRequest("ada@example.com", Password), TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_MissingFields_ValidationError()
    {
        var result = await Sut().HandleAsync(new LoginRequest(null, ""), TestContext.Current.CancellationToken);

        var errors = Assert.IsType<ValidationError>(result.Error).Errors;
        Assert.Equal(["email", "password"], errors.Select(e => e.Field));
    }

    [Fact]
    public async Task HandleAsync_AgentWithEmail_Unauthorized()
    {
        Db.Actors.Add(Actor.CreateAgent("Claude", null, "claude@example.com", [InstanceScope.Admin], _clock));
        await Db.SaveChangesAsync(Ct);

        var result = await Sut().HandleAsync(new LoginRequest("claude@example.com", Password), Ct);

        Assert.Equal(new UnauthorizedError("Invalid credentials."), result.Error);
    }

    [Fact]
    public async Task HandleAsync_Success_StampIsUpdatedAtOfVerifiedPassword()
    {
        var human = await SeedHumanAsync();
        var login = await Db.LocalLogins.AsNoTracking().SingleAsync(l => l.ActorId == human.Id, Ct);

        var result = await Sut().HandleAsync(new LoginRequest("ada@example.com", Password), Ct);

        Assert.Equal(login.UpdatedAt, result.Value.PasswordStamp);
    }

    [Fact]
    public async Task HandleAsync_PasswordChangedWhileHashing_SessionRejectedAfterwards()
    {
        // Race: the login reads the old hash, the change commits while PBKDF2 runs, the cookie is issued after the change.
        var human = await SeedHumanAsync();
        var changedAt = new DateTimeOffset(2026, 11, 7, 13, 0, 0, TimeSpan.Zero);
        var hasher = new InterceptingHasher(_hasher, () => ChangePasswordBehindTheLogin(human.Id, "the-new-password!", changedAt));

        var login = await new Login(Db, hasher, UnlimitedCredentialAttempts.Instance, NoAddress.Instance).HandleAsync(new LoginRequest("ada@example.com", Password), Ct);
        Db.ChangeTracker.Clear();
        var session = await new AuthenticateSession(Db).HandleAsync(
            new AuthenticateSessionRequest(human.Id, login.Value.PasswordStamp, IssuedAt: changedAt.AddSeconds(1)), Ct);

        Assert.True(login.IsSuccess);
        Assert.IsType<UnauthorizedError>(session.Error);
    }

    [Fact]
    public async Task HandleAsync_RehashNeeded_ReplacesHashButKeepsUpdatedAt()
    {
        var human = await SeedHumanAsync();
        var before = await Db.LocalLogins.AsNoTracking().SingleAsync(l => l.ActorId == human.Id, Ct);
        _clock.Advance(TimeSpan.FromHours(1));

        var result = await new Login(Db, new InterceptingHasher(_hasher, rehash: true), UnlimitedCredentialAttempts.Instance, NoAddress.Instance).HandleAsync(new LoginRequest("ada@example.com", Password), Ct);

        Assert.True(result.IsSuccess);
        var after = await Db.LocalLogins.AsNoTracking().SingleAsync(l => l.ActorId == human.Id, Ct);
        Assert.NotEqual(before.PasswordHash, after.PasswordHash);
        Assert.Equal(PasswordVerification.Success, _hasher.Verify(after.PasswordHash, Password));
        // Same password, new parameters: no credential change, so other sessions must stay valid.
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task HandleAsync_RehashWhilePasswordChanges_NewPasswordKept()
    {
        var human = await SeedHumanAsync();
        var changedAt = new DateTimeOffset(2026, 11, 7, 13, 0, 0, TimeSpan.Zero);
        var hasher = new InterceptingHasher(_hasher, () => ChangePasswordBehindTheLogin(human.Id, "the-new-password!", changedAt), rehash: true);

        var result = await new Login(Db, hasher, UnlimitedCredentialAttempts.Instance, NoAddress.Instance).HandleAsync(new LoginRequest("ada@example.com", Password), Ct);

        Assert.True(result.IsSuccess);
        var after = await Db.LocalLogins.AsNoTracking().SingleAsync(l => l.ActorId == human.Id, Ct);
        Assert.Equal(PasswordVerification.Success, _hasher.Verify(after.PasswordHash, "the-new-password!"));
        Assert.Equal(changedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task HandleAsync_SixthFailureForAccount_RateLimitedWithoutHash()
    {
        await SeedHumanAsync();
        var hasher = new CountingHasher(_hasher);
        var sut = Throttled(hasher, Limiters.Create(_clock));
        for (var i = 0; i < 5; i++)
        {
            Assert.IsType<UnauthorizedError>((await sut.HandleAsync(new LoginRequest("ada@example.com", "wrong password!"), Ct)).Error);
        }

        var locked = await sut.HandleAsync(new LoginRequest("ADA@example.com ", Password), Ct);

        Assert.Equal(TimeSpan.FromMinutes(1), Assert.IsType<RateLimitedError>(locked.Error).RetryAfter);
        Assert.Equal(5, hasher.Verifications);
    }

    [Fact]
    public async Task HandleAsync_LockExpired_RightPasswordSucceeds()
    {
        await SeedHumanAsync();
        var sut = Throttled(_hasher, Limiters.Create(_clock));
        for (var i = 0; i < 5; i++)
        {
            await sut.HandleAsync(new LoginRequest("ada@example.com", "wrong password!"), Ct);
        }

        _clock.Advance(TimeSpan.FromMinutes(1));
        var result = await sut.HandleAsync(new LoginRequest("ada@example.com", Password), Ct);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task HandleAsync_UnknownAndKnownEmail_CountedAndAnsweredAlike()
    {
        await SeedHumanAsync();
        var hasher = new CountingHasher(_hasher);
        var sut = Throttled(hasher, Limiters.Create(_clock));
        var results = new Dictionary<string, List<Error>>(StringComparer.Ordinal) { ["ada@example.com"] = [], ["nobody@example.com"] = [] };

        for (var i = 0; i < 7; i++)
        {
            foreach (var (email, errors) in results)
            {
                errors.Add((await sut.HandleAsync(new LoginRequest(email, "wrong password!"), Ct)).Error!);
            }
        }

        Assert.Equal(results["ada@example.com"], results["nobody@example.com"]);
        Assert.Equal(5, results["ada@example.com"].Count(e => e is UnauthorizedError));
        Assert.Equal(10, hasher.Verifications);
    }

    [Fact]
    public async Task HandleAsync_InactiveWithRightPassword_CountsAsFailure()
    {
        var human = await SeedHumanAsync();
        human.Deactivate();
        await Db.SaveChangesAsync(Ct);
        var sut = Throttled(_hasher, Limiters.Create(_clock));

        for (var i = 0; i < 5; i++)
        {
            Assert.IsType<UnauthorizedError>((await sut.HandleAsync(new LoginRequest("ada@example.com", Password), Ct)).Error);
        }

        Assert.IsType<RateLimitedError>((await sut.HandleAsync(new LoginRequest("ada@example.com", Password), Ct)).Error);
    }

    [Fact]
    public async Task HandleAsync_Success_ResetsAccountCounter()
    {
        await SeedHumanAsync();
        var sut = Throttled(_hasher, Limiters.Create(_clock));
        for (var i = 0; i < 4; i++)
        {
            await sut.HandleAsync(new LoginRequest("ada@example.com", "wrong password!"), Ct);
        }

        Assert.True((await sut.HandleAsync(new LoginRequest("ada@example.com", Password), Ct)).IsSuccess);
        for (var i = 0; i < 4; i++)
        {
            await sut.HandleAsync(new LoginRequest("ada@example.com", "wrong password!"), Ct);
        }

        Assert.True((await sut.HandleAsync(new LoginRequest("ada@example.com", Password), Ct)).IsSuccess);
    }

    [Fact]
    public async Task HandleAsync_ManyEmailsFromOneAddress_AddressLimitStopsThem()
    {
        var limiter = Limiters.Create(_clock);
        var attacker = Throttled(_hasher, limiter, IPAddress.Parse("198.51.100.1"));
        var other = Throttled(_hasher, limiter, IPAddress.Parse("198.51.100.2"));

        for (var i = 0; i < 20; i++)
        {
            Assert.IsType<UnauthorizedError>((await attacker.HandleAsync(new LoginRequest($"guess-{i}@example.com", "x"), Ct)).Error);
        }

        var blocked = await attacker.HandleAsync(new LoginRequest("fresh@example.com", "x"), Ct);
        var unaffected = await other.HandleAsync(new LoginRequest("fresh@example.com", "x"), Ct);

        Assert.Equal(TimeSpan.FromMinutes(15), Assert.IsType<RateLimitedError>(blocked.Error).RetryAfter);
        Assert.IsType<UnauthorizedError>(unaffected.Error);
    }

    [Fact]
    public async Task HandleAsync_InvalidInput_NotCounted()
    {
        var limiter = Limiters.Create(_clock);
        var sut = Throttled(_hasher, limiter);

        for (var i = 0; i < 10; i++)
        {
            Assert.IsType<ValidationError>((await sut.HandleAsync(new LoginRequest("ada@example.com", ""), Ct)).Error);
        }

        Assert.Equal(0, limiter.Count);
    }

    private Login Throttled(IPasswordHasher hasher, ICredentialAttemptLimiter limiter, IPAddress? address = null) =>
        new(Db, hasher, limiter, new FixedAddress(address));

    /// <summary>A password change committed by someone else (bypasses the change tracker, like another request would).</summary>
    private void ChangePasswordBehindTheLogin(Guid actorId, string newPassword, DateTimeOffset changedAt)
    {
        var hash = _hasher.Hash(newPassword);
        Db.LocalLogins.Where(l => l.ActorId == actorId)
            .ExecuteUpdate(setters => setters.SetProperty(l => l.PasswordHash, hash).SetProperty(l => l.UpdatedAt, changedAt));
    }

    private Login Sut() => new(Db, _hasher, UnlimitedCredentialAttempts.Instance, NoAddress.Instance);

    /// <summary>
    /// The real hasher; <paramref name="duringVerify"/> runs after the login read the hash and before it continues (the
    /// time PBKDF2 takes); <paramref name="rehash"/> reports every success as outdated.
    /// </summary>
    private sealed class InterceptingHasher(IPasswordHasher inner, Action? duringVerify = null, bool rehash = false) : IPasswordHasher
    {
        public string Hash(string password) => inner.Hash(password);

        public PasswordVerification Verify(string? passwordHash, string password)
        {
            var result = inner.Verify(passwordHash, password);
            duringVerify?.Invoke();
            return rehash && result != PasswordVerification.Failed ? PasswordVerification.SuccessRehashNeeded : result;
        }
    }

    private async Task<Actor> SeedHumanAsync()
    {
        var human = Actor.CreateHuman("Ada", "ada@example.com", null, [InstanceScope.Admin], _clock);
        Db.Actors.Add(human);
        Db.LocalLogins.Add(LocalLogin.Create(human, _hasher.Hash(Password), _clock));
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return human;
    }
}
