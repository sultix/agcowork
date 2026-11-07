using System.Security.Cryptography;
using System.Text;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Identity.ApiKeys;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Identity;

public sealed class CreateApiKeyTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);

    [Fact]
    public async Task HandleAsync_Admin_ReturnsTokenOnceAndStoresOnlyHash()
    {
        var agent = await SeedAgentAsync();

        var result = await Sut(TestActor.Admin()).HandleAsync(
            new CreateApiKeyRequest(agent.Id, [KeyScope.Write, KeyScope.Read], null, Now.AddDays(30)),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(ApiKeyToken.TryParse(result.Value.Key, out var prefix, out var secret));
        Assert.Equal(prefix, result.Value.ApiKey.Prefix);
        Assert.Equal([KeyScope.Read, KeyScope.Write], result.Value.ApiKey.Scopes);
        Assert.Equal(Now.AddDays(30), result.Value.ApiKey.ExpiresAt);

        var stored = await Db.ApiKeys.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), stored.SecretHash);
        Assert.Equal(agent.Id, stored.ActorId);
    }

    [Fact]
    public async Task HandleAsync_TwoKeys_HaveDistinctPrefixesAndSecrets()
    {
        var agent = await SeedAgentAsync();
        var sut = Sut(TestActor.Admin());

        var first = await sut.HandleAsync(new CreateApiKeyRequest(agent.Id), TestContext.Current.CancellationToken);
        var second = await sut.HandleAsync(new CreateApiKeyRequest(agent.Id), TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Value.Key, second.Value.Key);
        Assert.NotEqual(first.Value.ApiKey.Prefix, second.Value.ApiKey.Prefix);
    }

    [Fact]
    public async Task HandleAsync_WithoutInstanceAdmin_Forbidden()
    {
        var agent = await SeedAgentAsync();

        var result = await Sut(TestActor.Agent(scopes: InstanceScope.WorkspacesCreate))
            .HandleAsync(new CreateApiKeyRequest(agent.Id), TestContext.Current.CancellationToken);

        var error = Assert.IsType<ForbiddenError>(result.Error);
        Assert.Equal("requires instance:admin", error.Message);
        Assert.Empty(Db.ApiKeys);
    }

    [Fact]
    public async Task HandleAsync_Anonymous_Unauthorized()
    {
        var agent = await SeedAgentAsync();

        var result = await Sut(TestActor.Anonymous).HandleAsync(new CreateApiKeyRequest(agent.Id), TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_UnknownActor_NotFound()
    {
        var result = await Sut(TestActor.Admin()).HandleAsync(new CreateApiKeyRequest(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_HumanActor_ValidationErrorOnActorId()
    {
        var human = Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock);
        Db.Actors.Add(human);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Sut(TestActor.Admin()).HandleAsync(new CreateApiKeyRequest(human.Id), TestContext.Current.CancellationToken);

        var field = Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors);
        Assert.Equal(("actorId", ErrorCodes.NotAllowed), (field.Field, field.Code));
    }

    [Fact]
    public async Task HandleAsync_UnknownScopeAndPastExpiry_ReportsBothFields()
    {
        var agent = await SeedAgentAsync();

        var result = await Sut(TestActor.Admin()).HandleAsync(
            new CreateApiKeyRequest(agent.Id, [KeyScope.Read, "delete"], null, Now.AddSeconds(-1)),
            TestContext.Current.CancellationToken);

        var errors = Assert.IsType<ValidationError>(result.Error).Errors;
        Assert.Collection(
            errors,
            e =>
            {
                Assert.Equal(("scopes[1]", ErrorCodes.NotInOptions), (e.Field, e.Code));
                Assert.Equal(KeyScope.All, e.Allowed);
            },
            e => Assert.Equal(("expiresAt", ErrorCodes.OutOfRange), (e.Field, e.Code)));
    }

    private CreateApiKey Sut(ICurrentActor actor) => new(Db, actor, new ApiKeyGenerator(), _clock);

    private async Task<Actor> SeedAgentAsync()
    {
        var agent = Actor.CreateAgent("Claude", null, null, [], _clock);
        Db.Actors.Add(agent);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return agent;
    }
}
