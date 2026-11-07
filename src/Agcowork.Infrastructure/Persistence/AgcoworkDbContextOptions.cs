using Microsoft.EntityFrameworkCore;

namespace Agcowork.Infrastructure.Persistence;

internal static class AgcoworkDbContextOptions
{
    /// <summary>Single place for provider settings, shared by runtime registration and design-time factory.</summary>
    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();
}
