using Agcowork.Domain.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Domain.Tests.Identity;

public sealed class ActorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Now);

    [Fact]
    public void CreateHuman_ValidInput_NormalizesAndSetsFields()
    {
        var actor = Actor.CreateHuman("  Ada  ", " Ada@Example.COM ", " Tech-Lead ", [InstanceScope.Admin, InstanceScope.Admin], _clock);

        Assert.Equal(ActorKind.Human, actor.Kind);
        Assert.Equal("Ada", actor.Name);
        Assert.Equal("ada@example.com", actor.Email);
        Assert.Equal("Tech-Lead", actor.Role);
        Assert.Equal([InstanceScope.Admin], actor.InstanceScopes);
        Assert.True(actor.IsActive);
        Assert.Equal(Now, actor.CreatedAt);
        Assert.Equal(7, actor.Id.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateHuman_WithoutEmail_Throws(string email)
    {
        Assert.ThrowsAny<ArgumentException>(() => Actor.CreateHuman("Ada", email, null, [], _clock));
    }

    [Fact]
    public void CreateAgent_WithoutEmail_IsAllowed()
    {
        var actor = Actor.CreateAgent("Claude", "Reviewer", email: null, [], _clock);

        Assert.Equal(ActorKind.Agent, actor.Kind);
        Assert.Null(actor.Email);
        Assert.Empty(actor.InstanceScopes);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("Ada <ada@example.com>")]
    [InlineData("ada @example.com")]
    [InlineData("ada@localhost")]
    public void CreateHuman_InvalidEmail_Throws(string email)
    {
        Assert.False(Actor.IsValidEmail(email));
        Assert.Throws<ArgumentException>(() => Actor.CreateHuman("Ada", email, null, [], _clock));
    }

    [Fact]
    public void Create_UnknownInstanceScope_Throws()
    {
        Assert.Throws<ArgumentException>(() => Actor.CreateAgent("Claude", null, null, ["instance:root"], _clock));
    }

    [Fact]
    public void Create_NameTooLong_Throws()
    {
        Assert.Throws<ArgumentException>(() => Actor.CreateAgent(new string('n', Actor.NameMaxLength + 1), null, null, [], _clock));
    }

    [Fact]
    public void ChangeRole_Whitespace_ClearsRole()
    {
        var actor = Actor.CreateAgent("Claude", "Reviewer", null, [], _clock);

        actor.ChangeRole("  ");

        Assert.Null(actor.Role);
    }

    [Fact]
    public void SetInstanceScopes_Duplicates_AreDistinctAndSorted()
    {
        var actor = Actor.CreateAgent("Claude", null, null, [], _clock);

        actor.SetInstanceScopes([InstanceScope.WorkspacesCreate, InstanceScope.Admin, InstanceScope.WorkspacesCreate]);

        Assert.Equal([InstanceScope.Admin, InstanceScope.WorkspacesCreate], actor.InstanceScopes);
        Assert.True(actor.HasInstanceScope(InstanceScope.Admin));
    }

    [Fact]
    public void Deactivate_ThenActivate_TogglesIsActive()
    {
        var actor = Actor.CreateAgent("Claude", null, null, [], _clock);

        actor.Deactivate();
        Assert.False(actor.IsActive);

        actor.Activate();
        Assert.True(actor.IsActive);
    }

    [Fact]
    public void LocalLoginCreate_ForAgent_Throws()
    {
        var agent = Actor.CreateAgent("Claude", null, null, [], _clock);

        Assert.Throws<ArgumentException>(() => LocalLogin.Create(agent, "hash", _clock));
    }

    [Fact]
    public void LocalLoginCreate_ForHuman_LinksActor()
    {
        var human = Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock);

        var login = LocalLogin.Create(human, "hash", _clock);

        Assert.Equal(human.Id, login.ActorId);
        Assert.Equal(Now, login.UpdatedAt);
        Assert.False(login.MustChangePassword);
    }

    [Fact]
    public void LocalLoginChangePassword_MovesUpdatedAtAndSetsFlag()
    {
        var login = LocalLogin.Create(Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock), "hash", _clock, mustChangePassword: true);
        _clock.Advance(TimeSpan.FromMinutes(1));

        login.ChangePassword("new-hash", mustChangePassword: false, _clock);

        Assert.Equal(("new-hash", Now.AddMinutes(1), false), (login.PasswordHash, login.UpdatedAt, login.MustChangePassword));
    }

    [Fact]
    public void LocalLoginChangePassword_TruncatesToMicroseconds()
    {
        var login = LocalLogin.Create(Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock), "hash", _clock);
        _clock.Advance(TimeSpan.FromTicks(12_345_678));

        login.ChangePassword("new-hash", mustChangePassword: false, _clock);

        Assert.Equal(Now.AddTicks(12_345_670), login.UpdatedAt);
    }

    [Fact]
    public void LocalLoginRequirePasswordChange_SetsFlagOnly()
    {
        var login = LocalLogin.Create(Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock), "hash", _clock);
        _clock.Advance(TimeSpan.FromMinutes(1));

        login.RequirePasswordChange();

        Assert.Equal(("hash", Now, true), (login.PasswordHash, login.UpdatedAt, login.MustChangePassword));
    }
}
