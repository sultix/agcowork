using Agcowork.Application.Common.Persistence;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Spaces;
using Agcowork.Domain.Workspaces;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agcowork.Application.Tests.Collections;

/// <summary>
/// Delegates to the real context, but runs <paramref name="beforeFirstSave"/> right before the first save: simulates a
/// concurrent request that changes the same rows after the use case loaded them.
/// </summary>
internal sealed class RacingDbContext(AgcoworkDbContext inner, Func<Task> beforeFirstSave) : IAgcoworkDbContext
{
    private bool _raced;

    public DbSet<Actor> Actors => inner.Actors;

    public DbSet<LocalLogin> LocalLogins => inner.LocalLogins;

    public DbSet<ApiKey> ApiKeys => inner.ApiKeys;

    public DbSet<Workspace> Workspaces => inner.Workspaces;

    public DbSet<Membership> Memberships => inner.Memberships;

    public DbSet<Space> Spaces => inner.Spaces;

    public DbSet<Collection> Collections => inner.Collections;

    public DbSet<Template> Templates => inner.Templates;

    public DatabaseFacade Database => inner.Database;

    public Task LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken) => inner.LockWorkspaceAsync(workspaceId, cancellationToken);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!_raced)
        {
            _raced = true;
            await beforeFirstSave();
        }

        return await inner.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The concurrent change: another schema update bumped the version.</summary>
    public static Func<Task> BumpSchemaVersion(AgcoworkDbContext db, Guid collectionId) =>
        () => db.Database.ExecuteSqlAsync($"UPDATE collections SET schema_version = schema_version + 1 WHERE id = {collectionId}");
}
