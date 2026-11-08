using Agcowork.Infrastructure.Query;
using Agcowork.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agcowork.Infrastructure.Persistence;

internal static class AgcoworkDbContextOptions
{
    /// <summary>
    /// Single place for provider settings, shared by runtime registration and tests.
    /// Includes the tenant interceptor, so no context can be created without it, the JSONB functions of the query translator,
    /// and the database session interceptor: every command runs as <see cref="RowLevelSecurity.AppRole"/> under the tenant's
    /// row-level security (architecture §2a, step 5).
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection string enables Npgsql multiplexing (<see cref="RowLevelSecurity.EnsureSupported"/>).</exception>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString)
    {
        RowLevelSecurity.EnsureSupported(connectionString);
        ConfigureOwner(options, connectionString);
        options.AddInterceptors(DatabaseSessionInterceptor.Instance);
        return options;
    }

    /// <summary>
    /// The same settings without the role switch: the context works as the login of the connection string (the owner of the
    /// schema). Only for migrations (<see cref="DependencyInjection.ApplyMigrationsAsync"/>, design time, test setup): they
    /// create tables, roles and policies, and their data backfills must see every workspace.
    /// </summary>
    public static DbContextOptionsBuilder ConfigureOwner(DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(TenantSaveChangesInterceptor.Instance);
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new JsonbQueryOptionsExtension());
        return options;
    }
}
