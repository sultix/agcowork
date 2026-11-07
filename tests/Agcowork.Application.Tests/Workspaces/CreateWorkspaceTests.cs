using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Workspaces;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Workspaces;

public sealed class CreateWorkspaceTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);

    [Fact]
    public async Task HandleAsync_WorkspacesCreate_CreatorBecomesOwnerInSameSave()
    {
        var caller = await SeedCallerAsync(InstanceScope.WorkspacesCreate);

        var result = await Sut(caller).HandleAsync(new CreateWorkspaceRequest("acme", " Acme ", "Product work"), Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("acme", result.Value.Slug);
        Assert.Equal("Acme", result.Value.Name);
        Assert.Equal(WorkspaceRole.Owner, result.Value.Role);
        Assert.Equal(WorkspaceScope.All.Order(StringComparer.Ordinal), result.Value.Scopes);
        Assert.Equal(caller.Id, result.Value.CreatedBy);

        var membership = await Db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).AsNoTracking().SingleAsync(Ct);
        Assert.Equal(result.Value.Id, membership.WorkspaceId);
        Assert.Equal(caller.Id, membership.ActorId);
        Assert.Equal(WorkspaceRole.Owner, membership.Role);
    }

    [Fact]
    public async Task HandleAsync_InstanceAdmin_ImpliesWorkspacesCreate()
    {
        var caller = await SeedCallerAsync(InstanceScope.Admin);

        var result = await Sut(caller).HandleAsync(new CreateWorkspaceRequest("acme", "Acme", TemplateId: "empty"), Ct);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task HandleAsync_WithoutRight_ForbiddenAndNothingStored()
    {
        var caller = await SeedCallerAsync();

        var result = await Sut(caller).HandleAsync(new CreateWorkspaceRequest("acme", "Acme"), Ct);

        var error = Assert.IsType<ForbiddenError>(result.Error);
        Assert.Equal("requires workspaces:create", error.Message);
        Assert.Empty(Db.Workspaces);
    }

    [Fact]
    public async Task HandleAsync_UnknownTemplate_ValidationErrorWithAllowed()
    {
        var caller = await SeedCallerAsync(InstanceScope.WorkspacesCreate);

        var result = await Sut(caller).HandleAsync(new CreateWorkspaceRequest("acme", "Acme", TemplateId: "unknown"), Ct);

        var error = Assert.IsType<ValidationError>(result.Error);
        var field = Assert.Single(error.Errors);
        Assert.Equal("templateId", field.Field);
        Assert.Equal(ErrorCodes.NotInOptions, field.Code);
        Assert.Equal(["software-projekt", "empty"], field.Allowed);
    }

    [Theory]
    [InlineData("Acme", "invalid_format")]
    [InlineData("acme\n", "invalid_format")]
    [InlineData("", "required")]
    public async Task HandleAsync_InvalidSlug_ValidationError(string slug, string code)
    {
        var caller = await SeedCallerAsync(InstanceScope.WorkspacesCreate);

        var result = await Sut(caller).HandleAsync(new CreateWorkspaceRequest(slug, "Acme"), Ct);

        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Equal(code, Assert.Single(error.Errors, e => e.Field == "slug").Code);
    }

    [Fact]
    public async Task HandleAsync_SlugTaken_Conflict()
    {
        var caller = await SeedCallerAsync(InstanceScope.WorkspacesCreate);
        await Sut(caller).HandleAsync(new CreateWorkspaceRequest("acme", "Acme"), Ct);

        var result = await Sut(caller).HandleAsync(new CreateWorkspaceRequest("acme", "Other"), Ct);

        var error = Assert.IsType<ConflictError>(result.Error);
        Assert.Equal("slug_taken", error.Code);
        Assert.Equal("slug", error.Field);
    }

    private CreateWorkspace Sut(TestActor caller) => Templates.WorkspaceTemplateHost.CreateWorkspace(Db, Tenant, caller, _clock);

    private async Task<TestActor> SeedCallerAsync(params string[] instanceScopes)
    {
        var actor = Actor.CreateAgent("Creator", null, null, instanceScopes, _clock);
        Db.Actors.Add(actor);
        await Db.SaveChangesAsync(Ct);
        return TestActor.Agent(actor.Id, instanceScopes);
    }
}
