using Agcowork.Application.Common.Results;
using Agcowork.Application.Identity.Authentication;
using Agcowork.Domain.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Identity;

public sealed class AuthenticateSessionTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Now);

    [Fact]
    public async Task HandleAsync_ActiveHuman_ReturnsCurrentScopes()
    {
        var human = await SeedAsync(Actor.CreateHuman("Ada", "ada@example.com", null, [InstanceScope.Admin], _clock), withLogin: true);

        var result = await Sut().HandleAsync(new AuthenticateSessionRequest(human.Id, Now), Ct);

        Assert.Equal(human.Id, result.Value.ActorId);
        Assert.Equal([InstanceScope.Admin], result.Value.InstanceScopes);
    }

    [Fact]
    public async Task HandleAsync_Agent_Unauthorized()
    {
        var agent = await SeedAsync(Actor.CreateAgent("Claude", null, null, [], _clock), withLogin: false);

        var result = await Sut().HandleAsync(new AuthenticateSessionRequest(agent.Id, Now), Ct);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_InactiveHuman_Unauthorized()
    {
        var human = Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock);
        human.Deactivate();
        await SeedAsync(human, withLogin: true);

        var result = await Sut().HandleAsync(new AuthenticateSessionRequest(human.Id, Now), Ct);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_UnknownActor_Unauthorized()
    {
        var result = await Sut().HandleAsync(new AuthenticateSessionRequest(Guid.CreateVersion7(), Now), Ct);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_HumanWithoutLocalLogin_Unauthorized()
    {
        var human = await SeedAsync(Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock), withLogin: false);

        var result = await Sut().HandleAsync(new AuthenticateSessionRequest(human.Id, Now), Ct);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_StampOfEarlierPassword_Unauthorized()
    {
        var human = await SeedAsync(Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock), withLogin: true);

        var result = await Sut().HandleAsync(new AuthenticateSessionRequest(human.Id, Now.AddMilliseconds(-1)), Ct);

        Assert.Equal("Session belongs to an earlier password.", Assert.IsType<UnauthorizedError>(result.Error).Reason);
    }

    [Fact]
    public async Task HandleAsync_StampAfterDatabaseRoundTrip_Succeeds()
    {
        // A microsecond value, as LocalLogin keeps it: PostgreSQL stores it exactly, so equality holds after reading back.
        var changedAt = Now.AddTicks(1_234_560);
        var human = await SeedAsync(Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock), withLogin: true, loginAt: changedAt);

        var result = await Sut().HandleAsync(new AuthenticateSessionRequest(human.Id, changedAt), Ct);

        Assert.Equal(changedAt, result.Value.PasswordStamp);
    }

    [Fact]
    public async Task HandleAsync_NoStampNoIssueTime_Unauthorized()
    {
        var human = await SeedAsync(Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock), withLogin: true);

        var result = await Sut().HandleAsync(new AuthenticateSessionRequest(human.Id, null), Ct);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Theory]
    [InlineData(-10_000, false)] // 1 ms before: an earlier password.
    [InlineData(-10, false)] // 1 µs before: no tolerance any more.
    [InlineData(0, true)] // The same state.
    [InlineData(10, false)] // 1 µs after: a stamp the database does not have.
    public void IsCurrent_WithStamp_RequiresEquality(long ticksFromChange, bool expected)
    {
        var changedAt = Now.AddTicks(1_234_560);

        Assert.Equal(expected, AuthenticateSession.IsCurrent(changedAt.AddTicks(ticksFromChange), issuedAt: Now.AddYears(1), changedAt));
    }

    [Theory]
    [InlineData(-1, false)] // Legacy cookie issued in the second before the change.
    [InlineData(0, false)] // Same second, rounded down: reads as older (fail safe).
    [InlineData(1, true)] // Next second.
    public void IsCurrent_LegacyCookieWithoutStamp_IssueTimeAtOrAfterChange(int secondsFromChange, bool expected)
    {
        var changedAt = Now.AddMilliseconds(400);
        var issuedUtc = Now.AddSeconds(secondsFromChange); // IssuedUtc keeps whole seconds only.

        Assert.Equal(expected, AuthenticateSession.IsCurrent(null, issuedUtc, changedAt));
    }

    [Fact]
    public void IsCurrent_NothingKnown_False() =>
        Assert.False(AuthenticateSession.IsCurrent(null, null, Now));

    private AuthenticateSession Sut()
    {
        Db.ChangeTracker.Clear();
        return new AuthenticateSession(Db);
    }

    private async Task<Actor> SeedAsync(Actor actor, bool withLogin, DateTimeOffset? loginAt = null)
    {
        Db.Actors.Add(actor);
        if (withLogin)
        {
            Db.LocalLogins.Add(LocalLogin.Create(actor, "hash", new FakeTimeProvider(loginAt ?? Now)));
        }

        await Db.SaveChangesAsync(Ct);
        return actor;
    }
}
