using Agcowork.Application.Common.Results;
using Agcowork.Application.Identity.Authentication;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Agcowork.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Identity;

public sealed class AuthenticateApiKeyTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly ApiKeyGenerator _generator = new();

    [Fact]
    public async Task HandleAsync_ValidKey_ReturnsAgentWithKeyClaims()
    {
        var (agent, key, token) = await SeedAsync(scopes: [KeyScope.Read], restrictToWorkspace: true);

        var result = await Sut().HandleAsync(new AuthenticateApiKeyRequest(token), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(agent.Id, result.Value.ActorId);
        Assert.Equal(ActorKind.Agent, result.Value.Kind);
        // Restricted key: the actor's instance scopes do not travel with it.
        Assert.Empty(result.Value.InstanceScopes);
        Assert.Equal(key.Id, result.Value.ApiKeyId);
        Assert.NotNull(key.WorkspaceId);
        Assert.Equal(key.WorkspaceId, result.Value.KeyWorkspaceId);
        Assert.Equal([KeyScope.Read], result.Value.KeyScopes);
    }

    [Fact]
    public async Task HandleAsync_UnrestrictedKey_CarriesInstanceScopes()
    {
        var (_, _, token) = await SeedAsync();

        var result = await Sut().HandleAsync(new AuthenticateApiKeyRequest(token), TestContext.Current.CancellationToken);

        Assert.Equal([InstanceScope.WorkspacesCreate], result.Value.InstanceScopes);
    }

    [Fact]
    public async Task HandleAsync_ScopeRestrictedKey_NoInstanceScopes()
    {
        var (_, _, token) = await SeedAsync(scopes: [KeyScope.Write]);

        var result = await Sut().HandleAsync(new AuthenticateApiKeyRequest(token), TestContext.Current.CancellationToken);

        Assert.Empty(result.Value.InstanceScopes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("agck_tooshort_x")]
    public async Task HandleAsync_MalformedToken_Unauthorized(string token)
    {
        await SeedAsync();

        var result = await Sut().HandleAsync(new AuthenticateApiKeyRequest(token), TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_UnknownPrefix_Unauthorized()
    {
        await SeedAsync();
        var foreign = _generator.Generate().Token;

        var result = await Sut().HandleAsync(new AuthenticateApiKeyRequest(foreign), TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_RightPrefixWrongSecret_Unauthorized()
    {
        var (_, key, _) = await SeedAsync();
        var forged = ApiKeyToken.Format(key.Prefix, new string('x', ApiKeyToken.SecretLength));

        var result = await Sut().HandleAsync(new AuthenticateApiKeyRequest(forged), TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_RevokedKey_Unauthorized()
    {
        var (_, key, token) = await SeedAsync();
        key.Revoke(_clock);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Sut().HandleAsync(new AuthenticateApiKeyRequest(token), TestContext.Current.CancellationToken);

        Assert.Contains("Revoked", Assert.IsType<UnauthorizedError>(result.Error).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_ExpiredKey_Unauthorized()
    {
        var (_, _, token) = await SeedAsync(expiresAt: Now.AddHours(1));
        _clock.Advance(TimeSpan.FromHours(1));

        var result = await Sut().HandleAsync(new AuthenticateApiKeyRequest(token), TestContext.Current.CancellationToken);

        Assert.Contains("Expired", Assert.IsType<UnauthorizedError>(result.Error).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_InactiveActor_Unauthorized()
    {
        var (agent, _, token) = await SeedAsync();
        agent.Deactivate();
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Sut().HandleAsync(new AuthenticateApiKeyRequest(token), TestContext.Current.CancellationToken);

        Assert.Contains("ActorInactive", Assert.IsType<UnauthorizedError>(result.Error).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_RepeatedUse_UpdatesLastUsedAtAtMostOncePerMinute()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, key, token) = await SeedAsync();

        await Sut().HandleAsync(new AuthenticateApiKeyRequest(token), ct);
        Assert.Equal(Now, await LastUsedAtAsync(key.Id));

        _clock.Advance(TimeSpan.FromSeconds(30));
        await Sut().HandleAsync(new AuthenticateApiKeyRequest(token), ct);
        Assert.Equal(Now, await LastUsedAtAsync(key.Id));

        _clock.Advance(TimeSpan.FromSeconds(31));
        await Sut().HandleAsync(new AuthenticateApiKeyRequest(token), ct);
        Assert.Equal(Now.AddSeconds(61), await LastUsedAtAsync(key.Id));
    }

    private AuthenticateApiKey Sut() => new(Db, _generator, _clock);

    private async Task<DateTimeOffset?> LastUsedAtAsync(Guid keyId) =>
        await Db.ApiKeys.AsNoTracking().Where(k => k.Id == keyId).Select(k => k.LastUsedAt).SingleAsync(TestContext.Current.CancellationToken);

    private async Task<(Actor Agent, ApiKey Key, string Token)> SeedAsync(
        IReadOnlyList<string>? scopes = null,
        bool restrictToWorkspace = false,
        DateTimeOffset? expiresAt = null)
    {
        var agent = Actor.CreateAgent("Claude", null, null, [InstanceScope.WorkspacesCreate], _clock);
        Db.Actors.Add(agent);

        // The workspace restriction is a foreign key, so the workspace has to exist.
        Guid? workspaceId = null;
        if (restrictToWorkspace)
        {
            var workspace = Workspace.Create("restricted", "Restricted", null, agent.Id, _clock);
            Db.Workspaces.Add(workspace);
            workspaceId = workspace.Id;
        }

        var generated = _generator.Generate();
        var key = ApiKey.Issue(agent, generated.Prefix, generated.SecretHash, scopes ?? [], workspaceId, expiresAt, _clock);
        Db.ApiKeys.Add(key);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (agent, key, generated.Token);
    }
}
