using Agcowork.Domain.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Domain.Tests.Identity;

public sealed class ApiKeyTests
{
    private const string Prefix = "Ab3dEf7h";
    private const string Secret = "0123456789abcdefghijABCDEFGHIJxy";
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Hash = new byte[ApiKey.SecretHashLength];

    private readonly FakeTimeProvider _clock = new(Now);

    [Fact]
    public void TokenFormat_ValidParts_RoundTrips()
    {
        var token = ApiKeyToken.Format(Prefix, Secret);

        Assert.Equal($"agck_{Prefix}_{Secret}", token);
        Assert.Equal(ApiKeyToken.Length, token.Length);
        Assert.True(ApiKeyToken.TryParse(token, out var prefix, out var secret));
        Assert.Equal(Prefix, prefix);
        Assert.Equal(Secret, secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("agck_Ab3dEf7h")]
    [InlineData("agcx_Ab3dEf7h_0123456789abcdefghijABCDEFGHIJxy")]
    [InlineData("agck_Ab3dEf7_h0123456789abcdefghijABCDEFGHIJxy")]
    [InlineData("agck_Ab3dEf7h_0123456789abcdefghijABCDEFGHIJx-")]
    [InlineData("agck_Ab3dEf7h_0123456789abcdefghijABCDEFGHIJxyz")]
    [InlineData(" agck_Ab3dEf7h_0123456789abcdefghijABCDEFGHIJx")]
    public void TokenTryParse_Malformed_ReturnsFalse(string? token)
    {
        Assert.False(ApiKeyToken.TryParse(token, out _, out _));
    }

    [Fact]
    public void Issue_ForHuman_Throws()
    {
        var human = Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock);

        Assert.Throws<ArgumentException>(() => ApiKey.Issue(human, Prefix, Hash, [], null, null, _clock));
    }

    [Fact]
    public void Issue_ForAgent_StoresMetadataAndNormalizesScopes()
    {
        var agent = Agent();
        var workspaceId = Guid.CreateVersion7();

        var key = ApiKey.Issue(agent, Prefix, Hash, [KeyScope.Write, KeyScope.Read, KeyScope.Write], workspaceId, Now.AddDays(1), _clock);

        Assert.Equal(agent.Id, key.ActorId);
        Assert.Equal(Prefix, key.Prefix);
        Assert.Equal([KeyScope.Read, KeyScope.Write], key.Scopes);
        Assert.Equal(workspaceId, key.WorkspaceId);
        Assert.Equal(Now, key.CreatedAt);
        Assert.Null(key.LastUsedAt);
        Assert.Null(key.RevokedAt);
    }

    [Fact]
    public void Issue_UnknownScope_Throws()
    {
        Assert.Throws<ArgumentException>(() => ApiKey.Issue(Agent(), Prefix, Hash, ["delete"], null, null, _clock));
    }

    [Fact]
    public void Issue_ExpiryInPast_Throws()
    {
        Assert.Throws<ArgumentException>(() => ApiKey.Issue(Agent(), Prefix, Hash, [], null, Now, _clock));
    }

    [Fact]
    public void Issue_WrongHashLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => ApiKey.Issue(Agent(), Prefix, new byte[16], [], null, null, _clock));
    }

    [Fact]
    public void GetStatus_FreshKey_IsActive()
    {
        var agent = Agent();
        var key = ApiKey.Issue(agent, Prefix, Hash, [], null, Now.AddHours(1), _clock);

        Assert.Equal(ApiKeyStatus.Active, key.GetStatus(agent, Now));
    }

    [Fact]
    public void GetStatus_AfterExpiry_IsExpired()
    {
        var agent = Agent();
        var key = ApiKey.Issue(agent, Prefix, Hash, [], null, Now.AddHours(1), _clock);

        Assert.Equal(ApiKeyStatus.Expired, key.GetStatus(agent, Now.AddHours(1)));
    }

    [Fact]
    public void GetStatus_Revoked_IsRevokedEvenIfAlsoExpired()
    {
        var agent = Agent();
        var key = ApiKey.Issue(agent, Prefix, Hash, [], null, Now.AddHours(1), _clock);

        key.Revoke(_clock);

        Assert.Equal(ApiKeyStatus.Revoked, key.GetStatus(agent, Now.AddHours(2)));
    }

    [Fact]
    public void GetStatus_InactiveActor_IsActorInactive()
    {
        var agent = Agent();
        var key = ApiKey.Issue(agent, Prefix, Hash, [], null, null, _clock);

        agent.Deactivate();

        Assert.Equal(ApiKeyStatus.ActorInactive, key.GetStatus(agent, Now));
    }

    [Fact]
    public void GetStatus_OtherActor_Throws()
    {
        var key = ApiKey.Issue(Agent(), Prefix, Hash, [], null, null, _clock);

        Assert.Throws<ArgumentException>(() => key.GetStatus(Agent(), Now));
    }

    [Fact]
    public void Revoke_Twice_KeepsFirstTimestamp()
    {
        var key = ApiKey.Issue(Agent(), Prefix, Hash, [], null, null, _clock);

        key.Revoke(_clock);
        _clock.Advance(TimeSpan.FromHours(1));
        key.Revoke(_clock);

        Assert.Equal(Now, key.RevokedAt);
    }

    [Fact]
    public void IsLastUsedUpdateDue_NeverUsed_IsTrue()
    {
        var key = ApiKey.Issue(Agent(), Prefix, Hash, [], null, null, _clock);

        // The throttling against an existing LastUsedAt is covered by AuthenticateApiKeyTests (needs persistence).
        Assert.True(key.IsLastUsedUpdateDue(Now));
    }

    private Actor Agent() => Actor.CreateAgent("Claude", null, null, [], _clock);
}
