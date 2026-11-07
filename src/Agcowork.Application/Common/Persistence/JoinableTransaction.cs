namespace Agcowork.Application.Common.Persistence;

/// <summary>
/// A transaction that is either owned (started here) or joined (an outer unit of work already runs one, e.g. the
/// workspace template creating its content in the transaction of <c>CreateWorkspace</c>). Joined transactions are
/// committed and rolled back by their owner; <see cref="CommitAsync"/> and disposal are no-ops for them.
/// <para>
/// The owner must abort on every failure of the inner work and never commit after an inner failure: the inner use case
/// returns its error without rolling back, because it cannot roll back what it does not own.
/// </para>
/// </summary>
public sealed class JoinableTransaction : IAsyncDisposable
{
    private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _owned;

    internal JoinableTransaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? owned) => _owned = owned;

    public Task CommitAsync(CancellationToken cancellationToken) =>
        _owned is null ? Task.CompletedTask : _owned.CommitAsync(cancellationToken);

    public ValueTask DisposeAsync() => _owned?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public static class JoinableTransactionExtensions
{
    /// <summary>
    /// Starts a transaction, or joins the one that is already open on the context. The owner must abort on every failure of the
    /// inner work; never commit after an inner failure.
    /// </summary>
    public static async Task<JoinableTransaction> BeginOrJoinTransactionAsync(this IAgcoworkDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is not null)
        {
            return new JoinableTransaction(null);
        }

        return new JoinableTransaction(await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false));
    }
}
