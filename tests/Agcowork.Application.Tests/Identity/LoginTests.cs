using Agcowork.Application.Common.Results;
using Agcowork.Application.Identity;
using Agcowork.Application.Identity.Authentication;
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

        var login = await new Login(Db, hasher).HandleAsync(new LoginRequest("ada@example.com", Password), Ct);
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

        var result = await new Login(Db, new InterceptingHasher(_hasher, rehash: true)).HandleAsync(new LoginRequest("ada@example.com", Password), Ct);

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

        var result = await new Login(Db, hasher).HandleAsync(new LoginRequest("ada@example.com", Password), Ct);

        Assert.True(result.IsSuccess);
        var after = await Db.LocalLogins.AsNoTracking().SingleAsync(l => l.ActorId == human.Id, Ct);
        Assert.Equal(PasswordVerification.Success, _hasher.Verify(after.PasswordHash, "the-new-password!"));
        Assert.Equal(changedAt, after.UpdatedAt);
    }

    /// <summary>A password change committed by someone else (bypasses the change tracker, like another request would).</summary>
    private void ChangePasswordBehindTheLogin(Guid actorId, string newPassword, DateTimeOffset changedAt)
    {
        var hash = _hasher.Hash(newPassword);
        Db.LocalLogins.Where(l => l.ActorId == actorId)
            .ExecuteUpdate(setters => setters.SetProperty(l => l.PasswordHash, hash).SetProperty(l => l.UpdatedAt, changedAt));
    }

    private Login Sut() => new(Db, _hasher);

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
