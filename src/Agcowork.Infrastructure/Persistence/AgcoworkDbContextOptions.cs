using Agcowork.Infrastructure.Query;
using Agcowork.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agcowork.Infrastructure.Persistence;

internal static class AgcoworkDbContextOptions
{
    /// <summary>
    /// Single place for provider settings, shared by runtime registration, design-time factory and tests.
    /// Includes the tenant interceptor, so no context can be created without it, and the JSONB functions of the query translator.
    /// </summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString)
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
