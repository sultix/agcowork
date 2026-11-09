using Agcowork.Application.Common.Results;
using Agcowork.Application.Identity;
using Agcowork.Application.Identity.Passwords;
using Agcowork.Application.Identity.Throttling;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Identity;

public sealed class PasswordUseCaseTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private const string OldPassword = "the-old-password-123";
    private const string NewPassword = "the-new-password-456";
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly AspNetPasswordHasher _hasher = new();

    [Fact]
    public async Task ChangeOwnPassword_CorrectCurrent_ReplacesHashMovesUpdatedAtAndClearsFlag()
    {
        var human = await SeedHumanAsync(mustChange: true);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await Change(TestActor.Human(human.Id)).HandleAsync(new ChangeOwnPasswordRequest(OldPassword, NewPassword), Ct);

        Assert.Equal(human.Id, result.Value.ActorId);
        var login = await LoginOfAsync(human.Id);
        Assert.Equal(PasswordVerification.Success, _hasher.Verify(login.PasswordHash, NewPassword));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify(login.PasswordHash, OldPassword));
        Assert.Equal(Start.AddMinutes(5), login.UpdatedAt);
        Assert.Equal(login.UpdatedAt, result.Value.PasswordStamp);
        Assert.False(login.MustChangePassword);
    }

    [Fact]
    public async Task ChangeOwnPassword_WrongCurrent_InvalidCredentialsOnField()
    {
        var human = await SeedHumanAsync();

        var result = await Change(TestActor.Human(human.Id)).HandleAsync(new ChangeOwnPasswordRequest("not-the-password", NewPassword), Ct);

        var error = Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors);
        Assert.Equal(("currentPassword", ErrorCodes.InvalidCredentials), (error.Field, error.Code));
        Assert.Equal(Start, (await LoginOfAsync(human.Id)).UpdatedAt);
    }

    [Fact]
    public async Task ChangeOwnPassword_SamePassword_Unchanged()
    {
        var human = await SeedHumanAsync();

        var result = await Change(TestActor.Human(human.Id)).HandleAsync(new ChangeOwnPasswordRequest(OldPassword, OldPassword), Ct);

        var error = Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors);
        Assert.Equal(("newPassword", ErrorCodes.Unchanged), (error.Field, error.Code));
    }

    [Theory]
    [InlineData("short", ErrorCodes.TooShort)]
    [InlineData(null, ErrorCodes.Required)]
    public async Task ChangeOwnPassword_InvalidNewPassword_FieldError(string? newPassword, string code)
    {
        var human = await SeedHumanAsync();

        var result = await Change(TestActor.Human(human.Id)).HandleAsync(new ChangeOwnPasswordRequest(OldPassword, newPassword), Ct);

        var error = Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors);
        Assert.Equal(("newPassword", code), (error.Field, error.Code));
    }

    [Fact]
    public async Task ChangeOwnPassword_TooLongNewPassword_TooLong()
    {
        var human = await SeedHumanAsync();

        var result = await Change(TestActor.Human(human.Id)).HandleAsync(
            new ChangeOwnPasswordRequest(OldPassword, new string('x', PasswordPolicy.MaxLength + 1)), Ct);

        Assert.Equal(ErrorCodes.TooLong, Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors).Code);
    }

    [Fact]
    public async Task ChangeOwnPassword_Agent_Forbidden()
    {
        var result = await Change(TestActor.Agent()).HandleAsync(new ChangeOwnPasswordRequest(OldPassword, NewPassword), Ct);

        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task ChangeOwnPassword_WithApiKey_Forbidden()
    {
        var human = await SeedHumanAsync();

        var result = await Change(TestActor.Human(human.Id).WithKey()).HandleAsync(new ChangeOwnPasswordRequest(OldPassword, NewPassword), Ct);

        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task ChangeOwnPassword_Anonymous_Unauthorized()
    {
        var result = await Change(TestActor.Anonymous).HandleAsync(new ChangeOwnPasswordRequest(OldPassword, NewPassword), Ct);

        Assert.IsType<UnauthorizedError>(result.Error);
    }

    [Fact]
    public async Task ChangeOwnPassword_HumanWithoutLogin_InvalidCredentials()
    {
        var human = Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock);
        Db.Actors.Add(human);
        await Db.SaveChangesAsync(Ct);

        var result = await Change(TestActor.Human(human.Id)).HandleAsync(new ChangeOwnPasswordRequest(OldPassword, NewPassword), Ct);

        Assert.Equal(ErrorCodes.InvalidCredentials, Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors).Code);
    }

    [Fact]
    public async Task ResetPassword_ExistingLogin_ReplacesHashAndSetsFlag()
    {
        var human = await SeedHumanAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));

        var result = await Reset(TestActor.Admin()).HandleAsync(new ResetPasswordRequest(human.Id, NewPassword), Ct);

        Assert.True(result.IsSuccess);
        var login = await LoginOfAsync(human.Id);
        Assert.Equal(PasswordVerification.Success, _hasher.Verify(login.PasswordHash, NewPassword));
        Assert.Equal(Start.AddMinutes(1), login.UpdatedAt);
        Assert.True(login.MustChangePassword);
    }

    [Fact]
    public async Task ResetPassword_MustChangeFalse_FlagCleared()
    {
        var human = await SeedHumanAsync(mustChange: true);

        var result = await Reset(TestActor.Admin()).HandleAsync(new ResetPasswordRequest(human.Id, NewPassword, MustChange: false), Ct);

        Assert.True(result.IsSuccess);
        Assert.False((await LoginOfAsync(human.Id)).MustChangePassword);
    }

    [Fact]
    public async Task ResetPassword_HumanWithoutLogin_CreatesLogin()
    {
        var human = Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock);
        Db.Actors.Add(human);
        await Db.SaveChangesAsync(Ct);

        var result = await Reset(TestActor.Admin()).HandleAsync(new ResetPasswordRequest(human.Id, NewPassword), Ct);

        Assert.True(result.IsSuccess);
        var login = await LoginOfAsync(human.Id);
        Assert.Equal(PasswordVerification.Success, _hasher.Verify(login.PasswordHash, NewPassword));
        Assert.True(login.MustChangePassword);
    }

    [Fact]
    public async Task ResetPassword_Agent_NotHuman()
    {
        var agent = Actor.CreateAgent("Claude", null, null, [], _clock);
        Db.Actors.Add(agent);
        await Db.SaveChangesAsync(Ct);

        var result = await Reset(TestActor.Admin()).HandleAsync(new ResetPasswordRequest(agent.Id, NewPassword), Ct);

        var error = Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors);
        Assert.Equal(("actorId", ErrorCodes.NotHuman), (error.Field, error.Code));
        Assert.False(await Db.LocalLogins.AnyAsync(Ct));
    }

    [Fact]
    public async Task ResetPassword_UnknownActor_NotFound()
    {
        var result = await Reset(TestActor.Admin()).HandleAsync(new ResetPasswordRequest(Guid.CreateVersion7(), NewPassword), Ct);

        Assert.IsType<NotFoundError>(result.Error);
    }

    [Fact]
    public async Task ResetPassword_NonAdmin_Forbidden()
    {
        var human = await SeedHumanAsync();

        var result = await Reset(TestActor.Human(human.Id)).HandleAsync(new ResetPasswordRequest(human.Id, NewPassword), Ct);

        Assert.IsType<ForbiddenError>(result.Error);
    }

    [Fact]
    public async Task ResetPassword_TooShort_ValidationBeforeLookup()
    {
        var result = await Reset(TestActor.Admin()).HandleAsync(new ResetPasswordRequest(Guid.CreateVersion7(), "short"), Ct);

        var error = Assert.Single(Assert.IsType<ValidationError>(result.Error).Errors);
        Assert.Equal(("newPassword", ErrorCodes.TooShort), (error.Field, error.Code));
    }

    [Fact]
    public async Task ChangeOwnPassword_SixthAfterFiveWrong_RateLimitedEvenWithRightPasswordAndWithoutHash()
    {
        var human = await SeedHumanAsync();
        var hasher = new CountingHasher(_hasher);
        var limiter = Limiters.Create(_clock);
        for (var i = 0; i < 5; i++)
        {
            var wrong = await Change(TestActor.Human(human.Id), hasher, limiter).HandleAsync(new ChangeOwnPasswordRequest("not-the-password", NewPassword), Ct);
            Assert.IsType<ValidationError>(wrong.Error);
        }

        var locked = await Change(TestActor.Human(human.Id), hasher, limiter).HandleAsync(new ChangeOwnPasswordRequest(OldPassword, NewPassword), Ct);

        Assert.Equal(TimeSpan.FromMinutes(1), Assert.IsType<RateLimitedError>(locked.Error).RetryAfter);
        Assert.Equal((5, 0), (hasher.Verifications, hasher.Hashes));
        Assert.Equal(PasswordVerification.Success, _hasher.Verify((await LoginOfAsync(human.Id)).PasswordHash, OldPassword));

        _clock.Advance(TimeSpan.FromMinutes(1));
        var afterLock = await Change(TestActor.Human(human.Id), hasher, limiter).HandleAsync(new ChangeOwnPasswordRequest(OldPassword, NewPassword), Ct);
        Assert.True(afterLock.IsSuccess);
    }

    [Fact]
    public async Task ChangeOwnPassword_ApiKeyCalls_ForbiddenAndNotCounted()
    {
        var human = await SeedHumanAsync();
        var limiter = Limiters.Create(_clock);

        for (var i = 0; i < 10; i++)
        {
            var result = await Change(TestActor.Human(human.Id).WithKey(), _hasher, limiter).HandleAsync(new ChangeOwnPasswordRequest("not-the-password", NewPassword), Ct);
            Assert.IsType<ForbiddenError>(result.Error);
        }

        Assert.Equal(0, limiter.Count);
    }

    [Fact]
    public async Task ChangeOwnPassword_RightCurrentButUnchanged_ResetsCounter()
    {
        var human = await SeedHumanAsync();
        var limiter = Limiters.Create(_clock);
        for (var i = 0; i < 4; i++)
        {
            await Change(TestActor.Human(human.Id), _hasher, limiter).HandleAsync(new ChangeOwnPasswordRequest("not-the-password", NewPassword), Ct);
        }

        var unchanged = await Change(TestActor.Human(human.Id), _hasher, limiter).HandleAsync(new ChangeOwnPasswordRequest(OldPassword, OldPassword), Ct);
        for (var i = 0; i < 4; i++)
        {
            await Change(TestActor.Human(human.Id), _hasher, limiter).HandleAsync(new ChangeOwnPasswordRequest("not-the-password", NewPassword), Ct);
        }

        Assert.Equal(ErrorCodes.Unchanged, Assert.Single(Assert.IsType<ValidationError>(unchanged.Error).Errors).Code);
        var result = await Change(TestActor.Human(human.Id), _hasher, limiter).HandleAsync(new ChangeOwnPasswordRequest(OldPassword, NewPassword), Ct);
        Assert.True(result.IsSuccess);
    }

    private ChangeOwnPassword Change(TestActor actor, IPasswordHasher hasher, ICredentialAttemptLimiter limiter)
    {
        Db.ChangeTracker.Clear();
        return new ChangeOwnPassword(Db, actor, hasher, limiter, _clock);
    }

    private ChangeOwnPassword Change(TestActor actor)
    {
        Db.ChangeTracker.Clear();
        return new ChangeOwnPassword(Db, actor, _hasher, UnlimitedCredentialAttempts.Instance, _clock);
    }

    private ResetPassword Reset(TestActor actor)
    {
        Db.ChangeTracker.Clear();
        return new ResetPassword(Db, actor, _hasher, _clock);
    }

    private async Task<LocalLogin> LoginOfAsync(Guid actorId)
    {
        Db.ChangeTracker.Clear();
        return await Db.LocalLogins.AsNoTracking().SingleAsync(l => l.ActorId == actorId, Ct);
    }

    private async Task<Actor> SeedHumanAsync(bool mustChange = false)
    {
        var human = Actor.CreateHuman("Ada", "ada@example.com", null, [], _clock);
        Db.Actors.Add(human);
        Db.LocalLogins.Add(LocalLogin.Create(human, _hasher.Hash(OldPassword), _clock, mustChange));
        await Db.SaveChangesAsync(Ct);
        return human;
    }
}
