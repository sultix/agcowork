using Agcowork.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Infrastructure.Persistence;

internal static class AgcoworkDbContextOptions
{
    /// <summary>
    /// Single place for provider settings, shared by runtime registration, design-time factory and tests.
    /// Includes the tenant interceptor, so no context can be created without it.
    /// </summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(TenantSaveChangesInterceptor.Instance);
}
