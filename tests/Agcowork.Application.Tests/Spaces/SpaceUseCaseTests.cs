using Agcowork.Application.Common.Results;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tests.Collections;
using Agcowork.Application.Tests.Tenancy;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Tests.Spaces;

public sealed class SpaceUseCaseTests(PostgresFixture postgres) : SchemaTestBase(postgres)
{
    [Fact]
    public async Task CreateSpace_DefaultSortOrder_AfterLast()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.MemberOfBoth);

        var first = await CreateSpaceAsync(tenant, seed.MemberOfBoth, "Produkt");
        var second = await CreateSpaceAsync(tenant, seed.MemberOfBoth, "Technik");

        Assert.Equal((0, 1), (first.SortOrder, second.SortOrder));
    }

    [Fact]
    public async Task CreateSpace_Viewer_Forbidden_InvalidInput_Errors()
    {
        var seed = await TenancySeed.CreateAsync(Db);

        var viewer = await new CreateSpace(Db, TestActor.Agent(seed.AlphaOwner.Id), Enter(seed.Alpha, seed.AlphaOwner, WorkspaceScope.Read), Clock, Activity)
            .HandleAsync(new CreateSpaceRequest("X"), Ct);
        var invalid = await new CreateSpace(Db, TestActor.Agent(seed.AlphaOwner.Id), Enter(seed.Alpha, seed.AlphaOwner), Clock, Activity)
            .HandleAsync(new CreateSpaceRequest(" ", Icon: new string('x', 65)), Ct);

        Assert.Equal("requires write", Assert.IsType<ForbiddenError>(viewer.Error).Message);
        var error = Invalid(invalid);
        Assert.Equal(ErrorCodes.Required, Field(error, "name").Code);
        Assert.Equal(ErrorCodes.TooLong, Field(error, "icon").Code);
    }

    [Fact]
    public async Task ListSpaces_OrderedAndArchivedHidden()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var notes = await new CreateSpace(Db, TestActor.Agent(seed.AlphaOwner.Id), tenant, Clock, Activity).HandleAsync(new CreateSpaceRequest("Notizen", SortOrder: 9), Ct);
        await CreateSpaceAsync(tenant, seed.AlphaOwner, "Technik");
        var old = await CreateSpaceAsync(tenant, seed.AlphaOwner, "Alt");
        await CreateSpaceAsync(Enter(seed.Beta, seed.BetaOwner), seed.BetaOwner, "Beta only");
        Tenant.Current = tenant;
        Assert.True((await new ArchiveSpace(Db, tenant, Clock, Activity).HandleAsync(new ArchiveSpaceRequest(old.Id), Ct)).IsSuccess);

        var visible = await new ListSpaces(Db, tenant).HandleAsync(new ListSpacesRequest(), Ct);
        var all = await new ListSpaces(Db, tenant).HandleAsync(new ListSpacesRequest(IncludeArchived: true), Ct);

        Assert.Equal(["Notizen", "Technik"], visible.Value.Select(s => s.Name));
        Assert.Equal(notes.Value.Id, visible.Value[0].Id);
        Assert.Equal(["Notizen", "Technik", "Alt"], all.Value.Select(s => s.Name));
    }

    [Fact]
    public async Task UpdateSpace_PartialAndArchived()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var tenant = Enter(seed.Alpha, seed.AlphaOwner);
        var space = await CreateSpaceAsync(tenant, seed.AlphaOwner);

        var updated = await new UpdateSpace(Db, tenant, Activity).HandleAsync(new UpdateSpaceRequest(space.Id, Description: "Product work.", Icon: "📦"), Ct);
        var cleared = await new UpdateSpace(Db, tenant, Activity).HandleAsync(new UpdateSpaceRequest(space.Id, Icon: ""), Ct);
        Assert.True((await new ArchiveSpace(Db, tenant, Clock, Activity).HandleAsync(new ArchiveSpaceRequest(space.Id), Ct)).IsSuccess);
        var archived = await new UpdateSpace(Db, tenant, Activity).HandleAsync(new UpdateSpaceRequest(space.Id, Name: "X"), Ct);

        Assert.Equal(("Produkt", "Product work.", "📦"), (updated.Value.Name, updated.Value.Description, updated.Value.Icon));
        Assert.Null(cleared.Value.Icon);
        Assert.Equal("space_archived", Assert.IsType<ConflictError>(archived.Error).Code);
    }

    [Fact]
    public async Task ArchiveSpace_Editor_Forbidden()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var space = await CreateSpaceAsync(Enter(seed.Alpha, seed.AlphaOwner), seed.AlphaOwner);

        var result = await new ArchiveSpace(Db, Enter(seed.Alpha, seed.MemberOfBoth), Clock, Activity).HandleAsync(new ArchiveSpaceRequest(space.Id), Ct);

        Assert.Equal("requires admin", Assert.IsType<ForbiddenError>(result.Error).Message);
    }

    [Fact]
    public async Task GetSpace_OtherWorkspace_NotFound()
    {
        var seed = await TenancySeed.CreateAsync(Db);
        var betaSpace = await CreateSpaceAsync(Enter(seed.Beta, seed.BetaOwner), seed.BetaOwner);

        var result = await new GetSpace(Db, Enter(seed.Alpha, seed.MemberOfBoth)).HandleAsync(new GetSpaceRequest(betaSpace.Id), Ct);

        Assert.IsType<NotFoundError>(result.Error);
    }
}
