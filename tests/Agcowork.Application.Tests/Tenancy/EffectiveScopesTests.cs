using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Tenancy;

public sealed class EffectiveScopesTests
{
    private static readonly HashSet<string> NoKeyRestriction = [];

    [Fact]
    public void Compute_NoKeyScopes_MembershipScopesApplyInFull()
    {
        var effective = EffectiveScopes.Compute(WorkspaceScope.ForRole(WorkspaceRole.Admin), NoKeyRestriction);

        Assert.Equal(new HashSet<string>(WorkspaceScope.All), effective);
    }

    [Fact]
    public void Compute_KeyScopes_IntersectWithMembership()
    {
        var effective = EffectiveScopes.Compute(WorkspaceScope.ForRole(WorkspaceRole.Editor), new HashSet<string> { WorkspaceScope.Read, WorkspaceScope.Admin });

        // The key cannot add "admin" to an editor; it can only narrow.
        Assert.Equal(new HashSet<string> { WorkspaceScope.Read }, effective);
    }

    [Fact]
    public void Compute_DisjointKeyScopes_Empty()
    {
        var effective = EffectiveScopes.Compute(WorkspaceScope.ForRole(WorkspaceRole.Viewer), new HashSet<string> { WorkspaceScope.Schema });

        Assert.Empty(effective);
    }

    [Fact]
    public void Compute_NarrowedMembership_KeyCannotWiden()
    {
        var effective = EffectiveScopes.Compute([WorkspaceScope.Read], new HashSet<string>(WorkspaceScope.All));

        Assert.Equal(new HashSet<string> { WorkspaceScope.Read }, effective);
    }

    [Fact]
    public void RequireScope_MissingScope_ForbiddenNamingScope()
    {
        var tenant = Tenant(WorkspaceRole.Owner, keyScopes: [WorkspaceScope.Read]);

        Assert.Null(tenant.RequireScope(WorkspaceScope.Read));
        var error = Assert.IsType<ForbiddenError>(tenant.RequireScope(WorkspaceScope.Write));
        Assert.Equal("requires write", error.Message);
    }

    [Fact]
    public void RequireOwner_OwnerWithReadOnlyKey_Forbidden()
    {
        Assert.Null(Tenant(WorkspaceRole.Owner).RequireOwner());
        Assert.IsType<ForbiddenError>(Tenant(WorkspaceRole.Owner, keyScopes: [WorkspaceScope.Read]).RequireOwner());
        Assert.IsType<ForbiddenError>(Tenant(WorkspaceRole.Admin).RequireOwner());
    }

    [Fact]
    public void RequireWritable_Archived_Conflict()
    {
        Assert.Null(Tenant(WorkspaceRole.Owner).RequireWritable());
        var error = Assert.IsType<ConflictError>(Tenant(WorkspaceRole.Owner, archived: true).RequireWritable());
        Assert.Equal("workspace_archived", error.Code);
    }

    private static TenantContext Tenant(WorkspaceRole role, string[]? keyScopes = null, bool archived = false)
    {
        var workspaceId = Guid.CreateVersion7();
        var membership = Membership.Create(workspaceId, Guid.CreateVersion7(), role, null, Guid.CreateVersion7(), new FakeTimeProvider());
        var effective = EffectiveScopes.Compute(membership.Scopes, (keyScopes ?? []).ToHashSet(StringComparer.Ordinal));
        return new TenantContext(workspaceId, "acme", archived, membership, effective);
    }
}
