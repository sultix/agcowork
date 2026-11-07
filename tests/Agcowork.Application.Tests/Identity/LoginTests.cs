using Agcowork.Application.Common.Results;
using Agcowork.Application.Identity.Authentication;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Identity;
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

    private Login Sut() => new(Db, _hasher, _clock);

    private async Task<Actor> SeedHumanAsync()
    {
        var human = Actor.CreateHuman("Ada", "ada@example.com", null, [InstanceScope.Admin], _clock);
        Db.Actors.Add(human);
        Db.LocalLogins.Add(LocalLogin.Create(human, _hasher.Hash(Password), _clock));
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return human;
    }
}
