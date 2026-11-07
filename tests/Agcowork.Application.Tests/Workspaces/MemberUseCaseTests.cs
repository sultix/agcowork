using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Tests.Tenancy;
using Agcowork.Application.Workspaces;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Workspaces;

public sealed class MemberUseCaseTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private readonly FakeTimeProvider _clock = new(TenancySeed.Now);

    [Fact]
    public async Task UpdateMember_DemoteLastOwner_ConflictLastOwner()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);

        var result = await new UpdateMember(Db, tenant).HandleAsync(new UpdateMemberRequest(seed.AlphaOwner.Id, "admin"), Ct);

        var error = Assert.IsType<ConflictError>(result.Error);
        Assert.Equal("last_owner", error.Code);
        Assert.Equal("role", error.Field);
    }

    [Fact]
    public async Task UpdateMember_DemoteOwnerWithSecondOwner_Succeeds()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        Assert.True((await new UpdateMember(Db, tenant).HandleAsync(new UpdateMemberRequest(seed.MemberOfBoth.Id, "owner"), Ct)).IsSuccess);

        var result = await new UpdateMember(Db, tenant).HandleAsync(new UpdateMemberRequest(seed.AlphaOwner.Id, "viewer"), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(WorkspaceRole.Viewer, result.Value.Role);
        Assert.Equal([WorkspaceScope.Read], result.Value.Scopes);
    }

    [Fact]
    public async Task RemoveMember_LastOwner_ConflictLastOwner()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);

        var result = await new RemoveMember(Db, tenant).HandleAsync(new RemoveMemberRequest(seed.AlphaOwner.Id), Ct);

        Assert.Equal("last_owner", Assert.IsType<ConflictError>(result.Error).Code);
        Assert.Equal(2, await Db.Memberships.CountAsync(Ct));
    }

    [Fact]
    public async Task UpdateMember_MemberOfOtherWorkspaceOnly_NotFound()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);

        var result = await new UpdateMember(Db, tenant).HandleAsync(new UpdateMemberRequest(seed.BetaOwner.Id, "viewer"), Ct);

        Assert.IsType<NotFoundError>(result.Error);
    }

    [Fact]
    public async Task AddMember_AdminAddsOwner_Forbidden()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var admin = await SeedActorAsync();
        await AddAsync(Enter(seed.Alpha, seed.AlphaOwner), seed.AlphaOwner, admin.Id, "admin");
        var newcomer = await SeedActorAsync();

        var result = await AddAsync(Enter(seed.Alpha, admin), admin, newcomer.Id, "owner");

        Assert.Equal("requires role owner", Assert.IsType<ForbiddenError>(result.Error).Message);
    }

    [Fact]
    public async Task AddMember_ScopesBeyondRole_ValidationErrorWithAllowed()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var newcomer = await SeedActorAsync();

        var result = await new AddMember(Db, TestActor.Agent(seed.AlphaOwner.Id), Enter(seed.Alpha, seed.AlphaOwner), _clock)
            .HandleAsync(new AddMemberRequest(newcomer.Id, "viewer", [WorkspaceScope.Read, WorkspaceScope.Write]), Ct);

        var field = Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors);
        Assert.Equal("scopes[1]", field.Field);
        Assert.Equal([WorkspaceScope.Read], field.Allowed);
    }

    [Fact]
    public async Task AddMember_StoresMembershipInCurrentTenantOnly()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var newcomer = await SeedActorAsync();

        var result = await AddAsync(Enter(seed.Alpha, seed.AlphaOwner), seed.AlphaOwner, newcomer.Id, "editor");

        Assert.True(result.IsSuccess);
        Assert.Equal(3, await Db.Memberships.CountAsync(Ct));
        Assert.Contains(await Db.Memberships.ToListAsync(Ct), m => m.ActorId == newcomer.Id && m.WorkspaceId == seed.Alpha.Id);
    }

    private TenantContext Enter(Workspace workspace, Actor actor)
    {
        var tenant = TenancySeed.TenantFor(Db, workspace, actor);
        Tenant.Current = tenant;
        return tenant;
    }

    private Task<Result<MemberDto>> AddAsync(TenantContext tenant, Actor caller, Guid actorId, string role) =>
        new AddMember(Db, TestActor.Agent(caller.Id), tenant, _clock).HandleAsync(new AddMemberRequest(actorId, role), Ct);

    private async Task<Actor> SeedActorAsync()
    {
        var actor = Actor.CreateAgent($"agent-{Guid.NewGuid():N}", null, null, [], _clock);
        Db.Actors.Add(actor);
        await Db.SaveChangesAsync(Ct);
        return actor;
    }
}
