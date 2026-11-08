using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agcowork.Infrastructure.Persistence;

/// <summary>
/// Keeps the PostgreSQL session of every <see cref="AgcoworkDbContext"/> connection in line with the tenant and the instance
/// area of its DI scope (architecture §2a, step 5; <see cref="RowLevelSecurity"/>): role <see cref="RowLevelSecurity.AppRole"/>,
/// <see cref="RowLevelSecurity.WorkspaceSetting"/> and <see cref="RowLevelSecurity.ScopeSetting"/>.
/// </summary>
/// <remarks>
/// <para>
/// When EF opens the connection, one statement sets role and both settings (one extra round trip per open). Before each
/// command the context's wanted settings (<see cref="AgcoworkDbContext.WantedSession"/>) are compared with those last applied;
/// if they differ, the same statement runs first, on the same connection and in the same transaction. So a tenant or instance
/// block that changes while the connection is open (workspace creation inside its transaction, a test that switches tenants)
/// takes effect with the very next command. A connection opened past EF (<c>GetDbConnection().Open()</c>) gets nothing:
/// never do that.
/// </para>
/// <para>
/// PostgreSQL undoes settings made inside a transaction that rolls back (also to a savepoint). Settings applied inside a
/// transaction therefore count only once it commits; after a rollback, a failed commit or a transaction that ends unseen
/// (disposed without commit: noticed by the next command outside a transaction or the next transaction start), they are
/// applied again before the next command. Opening the connection forgets everything:
/// a pooled connection comes back reset by Npgsql (<c>DISCARD ALL</c>, which also resets the role).
/// </para>
/// </remarks>
internal sealed class DatabaseSessionInterceptor : IDbConnectionInterceptor, IDbCommandInterceptor, IDbTransactionInterceptor
{
    public static DatabaseSessionInterceptor Instance { get; } = new();

    // Connection: applied right after opening, so that even a command that bypasses EF's command pipeline on this connection
    // (e.g. DbCommand from CreateDbCommand) does not run as the login.
    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (eventData.Context is not AgcoworkDbContext db)
        {
            return;
        }

        db.Session.Forget();
        var wanted = db.WantedSession;
        using var set = CreateSetCommand(connection, transaction: null, commandTimeout: null, wanted);
        set.ExecuteNonQuery();
        db.Session.Applied(wanted, transaction: null);
    }

    public async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not AgcoworkDbContext db)
        {
            return;
        }

        db.Session.Forget();
        var wanted = db.WantedSession;
        var set = CreateSetCommand(connection, transaction: null, commandTimeout: null, wanted);
        await using (set.ConfigureAwait(false))
        {
            await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        db.Session.Applied(wanted, transaction: null);
    }

    public void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) => SessionOf(eventData)?.Forget();

    public Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        SessionOf(eventData)?.Forget();
        return Task.CompletedTask;
    }

    // Commands: apply the wanted settings first when they differ from the applied ones.
    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Ensure(command, eventData);
        return result;
    }

    public async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(command, eventData, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Ensure(command, eventData);
        return result;
    }

    public async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(command, eventData, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Ensure(command, eventData);
        return result;
    }

    public async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await EnsureAsync(command, eventData, cancellationToken).ConfigureAwait(false);
        return result;
    }

    // Transactions: settings made inside one hold only after its commit. Npgsql hands out the same transaction object for every
    // transaction of a connection, and a transaction disposed without commit reaches no interceptor; so a new transaction
    // means that settings still pending from an earlier one were undone.
    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        SessionOf(eventData)?.TransactionStarted();
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        SessionOf(eventData)?.TransactionStarted();
        return ValueTask.FromResult(result);
    }

    public DbTransaction TransactionUsed(DbConnection connection, TransactionEventData eventData, DbTransaction result)
    {
        SessionOf(eventData)?.TransactionStarted();
        return result;
    }

    public ValueTask<DbTransaction> TransactionUsedAsync(
        DbConnection connection, TransactionEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        SessionOf(eventData)?.TransactionStarted();
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => SessionOf(eventData)?.Committed(transaction);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        SessionOf(eventData)?.Committed(transaction);
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => SessionOf(eventData)?.Forget();

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        SessionOf(eventData)?.Forget();
        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => SessionOf(eventData)?.Forget();

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        SessionOf(eventData)?.Forget();
        return Task.CompletedTask;
    }

    public void RolledBackToSavepoint(DbTransaction transaction, TransactionEventData eventData) => SessionOf(eventData)?.Forget();

    public Task RolledBackToSavepointAsync(DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
    {
        SessionOf(eventData)?.Forget();
        return Task.CompletedTask;
    }

    private static DatabaseSession? SessionOf(DbContextEventData eventData) => (eventData.Context as AgcoworkDbContext)?.Session;

    private static void Ensure(DbCommand command, CommandEventData eventData)
    {
        if (Pending(command, eventData) is not { } pending)
        {
            return;
        }

        using var set = CreateSetCommand(command, pending.Wanted);
        set.ExecuteNonQuery();
        pending.Session.Applied(pending.Wanted, command.Transaction);
    }

    private static async Task EnsureAsync(DbCommand command, CommandEventData eventData, CancellationToken cancellationToken)
    {
        if (Pending(command, eventData) is not { } pending)
        {
            return;
        }

        var set = CreateSetCommand(command, pending.Wanted);
        await using (set.ConfigureAwait(false))
        {
            await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        pending.Session.Applied(pending.Wanted, command.Transaction);
    }

    private static (DatabaseSession Session, SessionSettings Wanted)? Pending(DbCommand command, CommandEventData eventData)
    {
        if (eventData.Context is not AgcoworkDbContext db)
        {
            return null;
        }

        var wanted = db.WantedSession;
        return db.Session.NeedsApply(wanted, command.Transaction) ? (db.Session, wanted) : null;
    }

    private static DbCommand CreateSetCommand(DbCommand command, SessionSettings wanted) =>
        CreateSetCommand(
            command.Connection ?? throw new InvalidOperationException("A command without a connection cannot carry the tenant session settings."),
            command.Transaction,
            command.CommandTimeout,
            wanted);

    private static DbCommand CreateSetCommand(DbConnection connection, DbTransaction? transaction, int? commandTimeout, SessionSettings wanted)
    {
        var set = connection.CreateCommand();
        set.Transaction = transaction;
        if (commandTimeout is { } timeout)
        {
            set.CommandTimeout = timeout;
        }

        set.CommandText = RowLevelSecurity.SetSessionSql;
        AddParameter(set, "agcowork_workspace_id", wanted.WorkspaceId == Guid.Empty ? string.Empty : wanted.WorkspaceId.ToString("D"));
        AddParameter(set, "agcowork_scope", wanted.Instance ? RowLevelSecurity.InstanceScope : string.Empty);
        return set;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        parameter.DbType = System.Data.DbType.String;
        command.Parameters.Add(parameter);
    }
}

/// <summary>The session settings a context wants: tenant workspace (<see cref="Guid.Empty"/> = none) and instance area.</summary>
internal readonly record struct SessionSettings(Guid WorkspaceId, bool Instance);

/// <summary>
/// What <see cref="DatabaseSessionInterceptor"/> last applied on a context's open connection. One per context; like the
/// context, not thread-safe.
/// </summary>
internal sealed class DatabaseSession
{
    private SessionSettings? _applied;

    // Transaction the applied settings were made in; they are undone unless it commits.
    private DbTransaction? _uncommittedIn;

    /// <summary>Whether <paramref name="wanted"/> has to be applied before a command in <paramref name="transaction"/>.</summary>
    public bool NeedsApply(SessionSettings wanted, DbTransaction? transaction)
    {
        if (_uncommittedIn is not null && !ReferenceEquals(_uncommittedIn, transaction))
        {
            // The transaction the settings were made in ended without a commit we saw: PostgreSQL rolled them back.
            Forget();
        }

        return _applied != wanted;
    }

    public void Applied(SessionSettings settings, DbTransaction? transaction)
    {
        _applied = settings;
        _uncommittedIn = transaction;
    }

    /// <summary>A transaction begins: settings still pending from an earlier, unseen-ended transaction were rolled back.</summary>
    public void TransactionStarted()
    {
        if (_uncommittedIn is not null)
        {
            Forget();
        }
    }

    public void Committed(DbTransaction transaction)
    {
        if (ReferenceEquals(_uncommittedIn, transaction))
        {
            _uncommittedIn = null;
        }
    }

    public void Forget()
    {
        _applied = null;
        _uncommittedIn = null;
    }
}
