using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Domain.Tests.Workspaces;

public sealed class MembershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid WorkspaceId = Guid.CreateVersion7();
    private readonly FakeTimeProvider _clock = new(Now);

    private Membership Create(WorkspaceRole role, IEnumerable<string>? scopes = null) =>
        Membership.Create(WorkspaceId, Guid.CreateVersion7(), role, scopes, Guid.CreateVersion7(), _clock);

    [Theory]
    [InlineData(WorkspaceRole.Viewer, new[] { "read" })]
    [InlineData(WorkspaceRole.Editor, new[] { "read", "write" })]
    [InlineData(WorkspaceRole.Admin, new[] { "admin", "read", "schema", "write" })]
    [InlineData(WorkspaceRole.Owner, new[] { "admin", "read", "schema", "write" })]
    public void Create_WithoutScopes_GetsRoleDefaults(WorkspaceRole role, string[] expected)
    {
        var membership = Create(role);

        Assert.Equal(expected, membership.Scopes);
        Assert.Equal(expected.Order(StringComparer.Ordinal), WorkspaceScope.ForRole(role).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Create_ValidInput_SetsFields()
    {
        var actorId = Guid.CreateVersion7();
        var createdBy = Guid.CreateVersion7();

        var membership = Membership.Create(WorkspaceId, actorId, WorkspaceRole.Editor, null, createdBy, _clock);

        Assert.Equal(7, membership.Id.Version);
        Assert.Equal(WorkspaceId, membership.WorkspaceId);
        Assert.Equal(actorId, membership.ActorId);
        Assert.Equal(createdBy, membership.CreatedBy);
        Assert.Equal(Now, membership.CreatedAt);
        Assert.False(membership.IsOwner);
    }

    [Fact]
    public void Create_NarrowedScopes_StoresDistinctSortedSubset()
    {
        var membership = Create(WorkspaceRole.Admin, [WorkspaceScope.Write, WorkspaceScope.Read, WorkspaceScope.Write]);

        Assert.Equal([WorkspaceScope.Read, WorkspaceScope.Write], membership.Scopes);
    }

    [Theory]
    [InlineData(WorkspaceRole.Viewer, "write")]
    [InlineData(WorkspaceRole.Editor, "schema")]
    [InlineData(WorkspaceRole.Editor, "admin")]
    [InlineData(WorkspaceRole.Admin, "root")]
    public void Create_ScopeBeyondRole_Throws(WorkspaceRole role, string scope)
    {
        Assert.Throws<ArgumentException>(() => Create(role, [WorkspaceScope.Read, scope]));
        Assert.False(Membership.AreValidScopesFor(role, [scope]));
    }

    [Fact]
    public void Create_EmptyScopes_Throws()
    {
        Assert.Throws<ArgumentException>(() => Create(WorkspaceRole.Editor, []));
    }

    [Fact]
    public void Create_EmptyIds_Throw()
    {
        Assert.Throws<ArgumentException>(() => Membership.Create(Guid.Empty, Guid.CreateVersion7(), WorkspaceRole.Viewer, null, Guid.CreateVersion7(), _clock));
        Assert.Throws<ArgumentException>(() => Membership.Create(WorkspaceId, Guid.Empty, WorkspaceRole.Viewer, null, Guid.CreateVersion7(), _clock));
    }

    [Fact]
    public void Create_UndefinedRole_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create((WorkspaceRole)99));
    }

    [Fact]
    public void ChangeRole_WithoutScopes_ResetsToNewRoleDefaults()
    {
        var membership = Create(WorkspaceRole.Admin, [WorkspaceScope.Read]);

        membership.ChangeRole(WorkspaceRole.Editor, null);

        Assert.Equal(WorkspaceRole.Editor, membership.Role);
        Assert.Equal([WorkspaceScope.Read, WorkspaceScope.Write], membership.Scopes);
    }

    [Fact]
    public void ChangeRole_ScopesBeyondNewRole_ThrowsAndKeepsState()
    {
        var membership = Create(WorkspaceRole.Admin);

        Assert.Throws<ArgumentException>(() => membership.ChangeRole(WorkspaceRole.Viewer, [WorkspaceScope.Write]));

        Assert.Equal(WorkspaceRole.Admin, membership.Role);
    }

    [Fact]
    public void RestrictScopes_Subset_KeepsRole()
    {
        var membership = Create(WorkspaceRole.Editor);

        membership.RestrictScopes([WorkspaceScope.Read]);

        Assert.Equal(WorkspaceRole.Editor, membership.Role);
        Assert.Equal([WorkspaceScope.Read], membership.Scopes);
    }

    [Fact]
    public void WorkspaceScope_All_MatchesKeyScopes()
    {
        // Keys narrow membership scopes; both vocabularies must stay identical.
        Assert.Equal(KeyScope.All, WorkspaceScope.All);
    }

    [Fact]
    public void OwnerInvariant_RemovingOrDemotingLastOwner_Fails()
    {
        var owner = Create(WorkspaceRole.Owner);

        Assert.False(OwnerInvariant.HoldsAfter(1, owner, newRole: null));
        Assert.False(OwnerInvariant.HoldsAfter(1, owner, WorkspaceRole.Admin));
    }

    [Fact]
    public void OwnerInvariant_AnotherOwnerLeft_Holds()
    {
        var owner = Create(WorkspaceRole.Owner);

        Assert.True(OwnerInvariant.HoldsAfter(2, owner, newRole: null));
        Assert.True(OwnerInvariant.HoldsAfter(2, owner, WorkspaceRole.Viewer));
    }

    [Fact]
    public void OwnerInvariant_LastOwnerStaysOwnerOrTargetIsNoOwner_Holds()
    {
        Assert.True(OwnerInvariant.HoldsAfter(1, Create(WorkspaceRole.Owner), WorkspaceRole.Owner));
        Assert.True(OwnerInvariant.HoldsAfter(1, Create(WorkspaceRole.Admin), newRole: null));
        Assert.True(OwnerInvariant.HoldsAfter(1, Create(WorkspaceRole.Editor), WorkspaceRole.Viewer));
    }
}
