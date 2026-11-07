using Agcowork.Application.Common.Persistence;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Records;
using Agcowork.Domain.Spaces;
using Agcowork.Domain.Workspaces;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Record = Agcowork.Domain.Records.Record;

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

    public DbSet<Record> Records => inner.Records;

    public DbSet<Revision> Revisions => inner.Revisions;

    public DbSet<ActivityEvent> ActivityEvents => inner.ActivityEvents;

    public DbSet<IdempotencyKey> IdempotencyKeys => inner.IdempotencyKeys;

    public DatabaseFacade Database => inner.Database;

    public Task LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken) => inner.LockWorkspaceAsync(workspaceId, cancellationToken);

    public Task LockRecordAsync(Guid recordId, CancellationToken cancellationToken) => inner.LockRecordAsync(recordId, cancellationToken);

    public Task LockCollectionAsync(Guid collectionId, CancellationToken cancellationToken) => inner.LockCollectionAsync(collectionId, cancellationToken);

    public Task LockCollectionSharedAsync(Guid collectionId, CancellationToken cancellationToken) => inner.LockCollectionSharedAsync(collectionId, cancellationToken);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!_raced)
        {
            _raced = true;
            await beforeFirstSave();
        }

        return await inner.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The concurrent change: another schema update bumped the version. Runs on its own connection and commits, like a
    /// real concurrent request; on the use case's connection it would join (and be rolled back with) its transaction.
    /// </summary>
    public static Func<Task> BumpSchemaVersion(AgcoworkDbContext db, Guid collectionId) =>
        async () =>
        {
            var options = (DbContextOptions<AgcoworkDbContext>)db.GetService<IDbContextOptions>();
            await using var other = new AgcoworkDbContext(options);
            await other.Database.ExecuteSqlAsync($"UPDATE collections SET schema_version = schema_version + 1 WHERE id = {collectionId}");
        };

    /// <summary>
    /// A version bump inside the use case's own transaction: simulates a writer that got past the collection lock
    /// (which UpdateSchema holds, so a real concurrent request would wait). Rolled back together with the use case.
    /// </summary>
    public static Func<Task> BumpSchemaVersionInTransaction(AgcoworkDbContext db, Guid collectionId) =>
        () => db.Database.ExecuteSqlAsync($"UPDATE collections SET schema_version = schema_version + 1 WHERE id = {collectionId}");
}
