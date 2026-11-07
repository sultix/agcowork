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
        var workspace = Workspace.Create(slug, "Roundtrip", new FakeTimeProvider(Now));

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
            Assert.Equal(Now, loaded.CreatedAt);
        }
    }

    [Fact]
    public async Task SaveChanges_DuplicateSlug_ThrowsUniqueViolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var slug = UniqueSlug();
        var clock = new FakeTimeProvider(Now);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
            db.Workspaces.Add(Workspace.Create(slug, "First", clock));
            await db.SaveChangesAsync(ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
            db.Workspaces.Add(Workspace.Create(slug, "Second", clock));

            // AgcoworkDbContext translates unique violations; the provider exception stays the inner exception.
            var exception = await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => db.SaveChangesAsync(ct));
            Assert.Equal("ix_workspaces_slug", exception.ConstraintName);
            var postgres = Assert.IsType<PostgresException>(exception.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal("ix_workspaces_slug", postgres.ConstraintName);
        }
    }

    private static string UniqueSlug() => $"ws-{Guid.NewGuid():N}"[..20];
}
