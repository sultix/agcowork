using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Tests.Tenancy;

public sealed class ResolveTenantTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task HandleAsync_Member_ReturnsTenantWithMembershipScopes()
    {
        var seed = await TenancySeed.CreateAsync(Db);

        var result = await new ResolveTenant(Db, TestActor.Agent(seed.AlphaOwner.Id)).HandleAsync(new ResolveTenantRequest("alpha"), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(seed.Alpha.Id, result.Value.WorkspaceId);
        Assert.Equal("alpha", result.Value.Slug);
        Assert.False(result.Value.IsArchived);
        Assert.Equal(WorkspaceRole.Owner, result.Value.Membership.Role);
        Assert.Equal(new HashSet<string>(WorkspaceScope.All), result.Value.EffectiveScopes);
    }

    [Fact]
    public async Task HandleAsync_KeyScopes_NarrowEffectiveScopes()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var caller = TestActor.Agent(seed.AlphaOwner.Id).WithKey(keyScopes: [KeyScope.Read]);

        var result = await new ResolveTenant(Db, caller).HandleAsync(new ResolveTenantRequest("alpha"), Ct);

        Assert.Equal(new HashSet<string> { WorkspaceScope.Read }, result.Value.EffectiveScopes);
    }

    [Fact]
    public async Task HandleAsync_OtherWorkspace_NotFound()
    {
        var seed = await TenancySeed.CreateAsync(Db);

        var result = await new ResolveTenant(Db, TestActor.Agent(seed.AlphaOwner.Id)).HandleAsync(new ResolveTenantRequest("beta"), Ct);

        Assert.IsType<NotFoundError>(result.Error);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Alpha")]
    [InlineData("alpha\n")]
    [InlineData("")]
    public async Task HandleAsync_UnknownOrMalformedSlug_NotFound(string slug)
    {
        var seed = await TenancySeed.CreateAsync(Db);

        var result = await new ResolveTenant(Db, TestActor.Agent(seed.AlphaOwner.Id)).HandleAsync(new ResolveTenantRequest(slug), Ct);

        Assert.IsType<NotFoundError>(result.Error);
    }

    [Fact]
    public async Task HandleAsync_KeyRestrictedToOtherWorkspace_NotFoundEvenThoughMember()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var restricted = TestActor.Agent(seed.MemberOfBoth.Id).WithKey(seed.Alpha.Id);

        var inAlpha = await new ResolveTenant(Db, restricted).HandleAsync(new ResolveTenantRequest("alpha"), Ct);
        var inBeta = await new ResolveTenant(Db, restricted).HandleAsync(new ResolveTenantRequest("beta"), Ct);

        Assert.True(inAlpha.IsSuccess);
        Assert.IsType<NotFoundError>(inBeta.Error);
    }

    [Fact]
    public async Task HandleAsync_Anonymous_Unauthorized()
    {
        var result = await new ResolveTenant(Db, TestActor.Anonymous).HandleAsync(new ResolveTenantRequest("alpha"), Ct);

        Assert.IsType<UnauthorizedError>(result.Error);
    }
}
