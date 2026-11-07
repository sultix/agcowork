using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agcowork.Application.Tests.Identity;

public sealed class IdentityBootstrapperTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task RunAsync_ValidConfiguration_CreatesHumanAdminWithLogin()
    {
        await Sut(new BootstrapOptions { AdminEmail = "Admin@Example.com", AdminPassword = "long-enough-password" }).RunAsync(Ct);

        var admin = await Db.Actors.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(("admin@example.com", ActorKind.Human), (admin.Email, admin.Kind));
        Assert.Equal([InstanceScope.Admin], admin.InstanceScopes);
        Assert.True(await Db.LocalLogins.AnyAsync(l => l.ActorId == admin.Id, Ct));
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
