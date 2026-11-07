using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Name of the connection string (and of the Aspire database resource).</summary>
    public const string ConnectionStringName = "agcowork";

    /// <summary>Health check tag for checks that only matter for readiness, not liveness.</summary>
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Resolve the connection string lazily so hosts and test factories can supply it late.
        services.AddDbContext<AgcoworkDbContext>((sp, options) =>
            AgcoworkDbContextOptions.Configure(options, GetConnectionString(configuration)));

        services.AddHealthChecks()
            .AddNpgSql(_ => GetConnectionString(configuration), name: "postgres", tags: [ReadyTag]);

        return services;
    }

    /// <summary>Applies pending EF Core migrations. Startup-time migration assumes a single instance (architecture §8).</summary>
    public static async Task ApplyMigrationsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgcoworkDbContext>();
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionStringName)
        ?? throw new InvalidOperationException(
            $"Connection string '{ConnectionStringName}' is not configured. Set ConnectionStrings__{ConnectionStringName} or run via the Aspire AppHost.");
}
