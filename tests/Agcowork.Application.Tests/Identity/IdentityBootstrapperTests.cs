using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agcowork.Application.Tests.Identity;

public sealed class IdentityBootstrapperTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private const string BootstrapPassword = "long-enough-password";

    [Fact]
    public async Task RunAsync_ValidConfiguration_CreatesHumanAdminWithLogin()
    {
        await Sut(new BootstrapOptions { AdminEmail = "Admin@Example.com", AdminPassword = "long-enough-password" }).RunAsync(Ct);

        var admin = await Db.Actors.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(("admin@example.com", ActorKind.Human), (admin.Email, admin.Kind));
        Assert.Equal([InstanceScope.Admin], admin.InstanceScopes);
        var login = await Db.LocalLogins.AsNoTracking().SingleAsync(l => l.ActorId == admin.Id, Ct);
        Assert.True(login.MustChangePassword);
    }

    [Fact]
    public async Task RunAsync_ExistingAdminStillOnBootstrapPassword_FlagsWithoutTouchingSessions()
    {
        var admin = Actor.CreateHuman("Administrator", "admin@example.com", null, [InstanceScope.Admin], TimeProvider.System);
        var login = LocalLogin.Create(admin, new AspNetPasswordHasher().Hash(BootstrapPassword), TimeProvider.System);
        Db.Actors.Add(admin);
        Db.LocalLogins.Add(login);
        await Db.SaveChangesAsync(Ct);
        var updatedAt = (await Db.LocalLogins.AsNoTracking().SingleAsync(Ct)).UpdatedAt;
        Db.ChangeTracker.Clear();

        await Sut(new BootstrapOptions { AdminEmail = "Admin@Example.com", AdminPassword = BootstrapPassword }).RunAsync(Ct);

        var after = await Db.LocalLogins.AsNoTracking().SingleAsync(Ct);
        Assert.True(after.MustChangePassword);
        Assert.Equal(updatedAt, after.UpdatedAt);
        Assert.Equal(1, await Db.Actors.CountAsync(Ct));
    }

    [Fact]
    public async Task RunAsync_ExistingAdminChangedPassword_NotFlagged()
    {
        await Sut(new BootstrapOptions { AdminEmail = "admin@example.com", AdminPassword = BootstrapPassword }).RunAsync(Ct);
        Db.ChangeTracker.Clear();
        var login = await Db.LocalLogins.SingleAsync(Ct);
        login.ChangePassword(new AspNetPasswordHasher().Hash("my-own-password-42"), mustChangePassword: false, TimeProvider.System);
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();

        await Sut(new BootstrapOptions { AdminEmail = "admin@example.com", AdminPassword = BootstrapPassword }).RunAsync(Ct);

        Assert.False((await Db.LocalLogins.AsNoTracking().SingleAsync(Ct)).MustChangePassword);
    }

    [Fact]
    public async Task RunAsync_PasswordShorterThanPolicy_CreatesNothing()
    {
        await Sut(new BootstrapOptions { AdminEmail = "admin@example.com", AdminPassword = "short" }).RunAsync(Ct);

        Assert.False(await Db.Actors.AnyAsync(Ct));
    }

    [Fact]
    public async Task RunAsync_NotConfigured_CreatesNothing()
    {
        await Sut(new BootstrapOptions()).RunAsync(Ct);

        Assert.False(await Db.Actors.AnyAsync(Ct));
    }

    [Fact]
    public async Task RunAsync_ActorsExist_CreatesNothing()
    {
        Db.Actors.Add(Actor.CreateAgent("Claude", null, null, [], TimeProvider.System));
        await Db.SaveChangesAsync(Ct);

        await Sut(new BootstrapOptions { AdminEmail = "admin@example.com", AdminPassword = "long-enough-password" }).RunAsync(Ct);

        Assert.Equal(1, await Db.Actors.CountAsync(Ct));
    }

    private IdentityBootstrapper Sut(BootstrapOptions options) =>
        new(Db, new AspNetPasswordHasher(), TimeProvider.System, Options.Create(options), NullLogger<IdentityBootstrapper>.Instance);
}
