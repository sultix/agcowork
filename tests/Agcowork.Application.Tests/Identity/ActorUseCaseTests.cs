using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Identity.Actors;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Identity;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Identity;

public sealed class ActorUseCaseTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task CreateActor_InvalidInput_ReportsEveryField()
    {
        var result = await Create(TestActor.Admin()).HandleAsync(
            new CreateActorRequest("robot", Name: " ", InstanceScopes: ["instance:root"]),
            TestContext.Current.CancellationToken);

        var errors = Assert.IsType<ValidationError>(result.Error).Errors;
        Assert.Contains(errors, e => e is { Field: "kind", Code: ErrorCodes.NotInOptions } && e.Allowed!.SequenceEqual(ActorKinds.All));
        Assert.Contains(errors, e => e is { Field: "name", Code: ErrorCodes.Required });
        Assert.Contains(errors, e => e is { Field: "instanceScopes[0]", Code: ErrorCodes.NotInOptions } && e.Allowed!.SequenceEqual(InstanceScope.All));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateActor_AgentWithBlankEmail_CreatedWithoutEmail(string email)
    {
        var result = await Create(TestActor.Admin()).HandleAsync(new CreateActorRequest("agent", "Claude", Email: email), Ct);

        Assert.Null(result.Value.Email);
    }

    [Fact]
    public async Task CreateActor_HumanWithBlankEmail_RequiresEmail()
    {
        var result = await Create(TestActor.Admin()).HandleAsync(new CreateActorRequest("human", "Ada", Email: "  "), Ct);

        var field = Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors);
        Assert.Equal(("email", ErrorCodes.Required), (field.Field, field.Code));
    }

    [Fact]
    public async Task SaveChangesOrConflict_DuplicateEmail_TranslatedToEmailTaken()
    {
        // The race the pre-check cannot see: two rows with the same email reach the database in one save.
        Db.Actors.Add(Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock));
        Db.Actors.Add(Actor.CreateHuman("Ada 2", "ADA@example.com", null, [], _clock));

        var conflict = await Db.SaveChangesOrConflictAsync(Ct, Agcowork.Application.Identity.IdentityConflicts.ActorEmail);

        Assert.Equal("email_taken", conflict?.Code);
    }

    [Fact]
    public async Task CreateActor_HumanWithoutEmail_RequiresEmail()
    {
        var result = await Create(TestActor.Admin()).HandleAsync(new CreateActorRequest("human", "Ada"), TestContext.Current.CancellationToken);

        var field = Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors);
        Assert.Equal(("email", ErrorCodes.Required), (field.Field, field.Code));
    }

    [Fact]
    public async Task CreateActor_AgentWithPassword_NotAllowed()
    {
        var result = await Create(TestActor.Admin()).HandleAsync(
            new CreateActorRequest("agent", "Claude", Password: "long-enough-password"),
            TestContext.Current.CancellationToken);

        var field = Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors);
        Assert.Equal(("password", ErrorCodes.NotAllowed), (field.Field, field.Code));
    }

    [Fact]
    public async Task CreateActor_DuplicateEmailCaseInsensitive_Conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = Create(TestActor.Admin());
        Assert.True((await sut.HandleAsync(new CreateActorRequest("human", "Ada", Email: "ada@example.com"), ct)).IsSuccess);

        var result = await sut.HandleAsync(new CreateActorRequest("human", "Ada 2", Email: "ADA@example.com"), ct);

        var conflict = Assert.IsType<ConflictError>(result.Error);
        Assert.Equal(("email_taken", "email"), (conflict.Code, conflict.Field));
    }

    [Fact]
    public async Task CreateActor_HumanWithPassword_CreatesLocalLogin()
    {
        var result = await Create(TestActor.Admin()).HandleAsync(
            new CreateActorRequest("Human", "Ada", Email: "ada@example.com", Password: "correct horse battery"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ActorKind.Human, result.Value.Kind);
        var login = Assert.Single(Db.LocalLogins);
        Assert.Equal(result.Value.Id, login.ActorId);
        Assert.DoesNotContain("correct horse battery", login.PasswordHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateActor_NonAdmin_Forbidden()
    {
        var result = await Create(TestActor.Agent()).HandleAsync(new CreateActorRequest("agent", "Claude"), TestContext.Current.CancellationToken);

        Assert.Equal("requires instance:admin", Assert.IsType<ForbiddenError>(result.Error).Message);
    }

    [Fact]
    public async Task UpdateActor_DeactivateSelf_Conflict()
    {
        var admin = await SeedAdminAsync();

        var result = await Update(TestActor.Admin(admin.Id)).HandleAsync(new UpdateActorRequest(admin.Id, IsActive: false), TestContext.Current.CancellationToken);

        Assert.Equal("self_lockout", Assert.IsType<ConflictError>(result.Error).Code);
        Assert.True(admin.IsActive);
    }

    [Fact]
    public async Task UpdateActor_RemoveOwnAdminScope_Conflict()
    {
        var admin = await SeedAdminAsync();

        var result = await Update(TestActor.Admin(admin.Id)).HandleAsync(new UpdateActorRequest(admin.Id, InstanceScopes: []), TestContext.Current.CancellationToken);

        Assert.Equal("self_lockout", Assert.IsType<ConflictError>(result.Error).Code);
    }

    [Fact]
    public async Task UpdateActor_PartialFields_ChangesOnlyGivenOnes()
    {
        var agent = Actor.CreateAgent("Claude", "Reviewer", null, [], _clock);
        Db.Actors.Add(agent);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await Update(TestActor.Admin()).HandleAsync(
            new UpdateActorRequest(agent.Id, Role: "", InstanceScopes: [InstanceScope.WorkspacesCreate]),
            TestContext.Current.CancellationToken);

        Assert.Equal("Claude", result.Value.Name);
        Assert.Null(result.Value.Role);
        Assert.Equal([InstanceScope.WorkspacesCreate], result.Value.InstanceScopes);
        Assert.True(result.Value.IsActive);
    }

    [Fact]
    public async Task UpdateActor_UnknownId_NotFound()
    {
        var result = await Update(TestActor.Admin()).HandleAsync(new UpdateActorRequest(Guid.CreateVersion7(), Name: "x"), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundError>(result.Error);
    }

    private CreateActor Create(ICurrentActor actor) => new(Db, actor, new AspNetPasswordHasher(), _clock);

    private UpdateActor Update(ICurrentActor actor) => new(Db, actor);

    private async Task<Actor> SeedAdminAsync()
    {
        var admin = Actor.CreateHuman("Admin", "admin@example.com", null, [InstanceScope.Admin], _clock);
        Db.Actors.Add(admin);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return admin;
    }
}
