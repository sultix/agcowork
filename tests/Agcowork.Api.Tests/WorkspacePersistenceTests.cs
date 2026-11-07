using Agcowork.Application.Common.Persistence;
using Agcowork.Domain.Workspaces;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Agcowork.Api.Tests;

[Collection(ApiTestGroup.Name)]
public sealed class WorkspacePersistenceTests(AgcoworkApiFactory factory)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task SaveChanges_NewWorkspace_CanBeReadBackBySlug()
    {
        var ct = TestContext.Current.CancellationToken;
        var slug = UniqueSlug();
        var workspace = Workspace.Create(slug, "Roundtrip", "About", await AdminIdAsync(), new FakeTimeProvider(Now));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync(ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
            var loaded = await db.Workspaces.AsNoTracking().SingleAsync(w => w.Slug == slug, ct);

            Assert.Equal(workspace.Id, loaded.Id);
            Assert.Equal("Roundtrip", loaded.Name);
            Assert.Equal("About", loaded.Description);
            Assert.Equal(Workspace.EmptySettings, loaded.Settings);
            Assert.Null(loaded.ArchivedAt);
            Assert.Equal(Now, loaded.CreatedAt);
        }
    }

    [Fact]
    public async Task SaveChanges_DuplicateSlug_ThrowsUniqueViolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var slug = UniqueSlug();
        var clock = new FakeTimeProvider(Now);
        var creator = await AdminIdAsync();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
            db.Workspaces.Add(Workspace.Create(slug, "First", null, creator, clock));
            await db.SaveChangesAsync(ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
            db.Workspaces.Add(Workspace.Create(slug, "Second", null, creator, clock));

            // AgcoworkDbContext translates unique violations; the provider exception stays the inner exception.
            var exception = await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => db.SaveChangesAsync(ct));
            Assert.Equal("ix_workspaces_slug", exception.ConstraintName);
            var postgres = Assert.IsType<PostgresException>(exception.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal("ix_workspaces_slug", postgres.ConstraintName);
        }
    }

    /// <summary>Workspaces reference their creator; the bootstrap admin always exists.</summary>
    private async Task<Guid> AdminIdAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
        return await db.Actors.Where(a => a.Email == factory.AdminEmail).Select(a => a.Id).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static string UniqueSlug() => $"ws-{Guid.NewGuid():N}"[..20];
}
