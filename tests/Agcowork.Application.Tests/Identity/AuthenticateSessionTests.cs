using Agcowork.Application.Common.Results;
using Agcowork.Application.Identity.Authentication;
using Agcowork.Domain.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Identity;

public sealed class AuthenticateSessionTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task HandleAsync_ActiveHuman_ReturnsCurrentScopes()
    {
        var human = await SeedAsync(Actor.CreateHuman("Ada", "ada@example.com", null, [InstanceScope.Admin], _clock));

        var result = await new AuthenticateSession(Db).HandleAsync(new AuthenticateSessionRequest(human.Id), Ct);

        Assert.Equal(human.Id, result.Value.ActorId);
        Assert.Equal([InstanceScope.Admin], result.Value.InstanceScopes);
    }

    [Fact]
    public async Task HandleAsync_Agent_Unauthorized()
    {
        var agent = await SeedAsync(Actor.CreateAgent("Claude", null, null, [], _clock));

        var result = await new AuthenticateSession(Db).HandleAsync(new AuthenticateSessionRequest(agent.Id), Ct);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_InactiveHuman_Unauthorized()
    {
        var human = Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock);
        human.Deactivate();
        await SeedAsync(human);

        var result = await new AuthenticateSession(Db).HandleAsync(new AuthenticateSessionRequest(human.Id), Ct);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_UnknownActor_Unauthorized()
    {
        var result = await new AuthenticateSession(Db).HandleAsync(new AuthenticateSessionRequest(Guid.CreateVersion7()), Ct);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    private async Task<Actor> SeedAsync(Actor actor)
    {
        Db.Actors.Add(actor);
        await Db.SaveChangesAsync(Ct);
        return actor;
    }
}
