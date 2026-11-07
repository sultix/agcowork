using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Identity;
using Agcowork.Application.Query;
using Agcowork.Infrastructure.Identity;
using Agcowork.Infrastructure.Persistence;
using Agcowork.Infrastructure.Query;
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
        services.AddScoped<IAgcoworkDbContext>(sp => sp.GetRequiredService<AgcoworkDbContext>());
        services.AddSingleton<IRecordQueryTranslator, SqlQueryTranslator>();

        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<IApiKeyGenerator, ApiKeyGenerator>();
        services.AddOptions<BootstrapOptions>().Bind(configuration.GetSection(BootstrapOptions.SectionName));
        services.AddScoped<IdentityBootstrapper>();

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

    /// <summary>
    /// Creates the bootstrap admin from <see cref="BootstrapOptions"/> if the installation has no
    /// actors yet. Call after <see cref="ApplyMigrationsAsync"/>.
    /// </summary>
    public static async Task BootstrapIdentityAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<IdentityBootstrapper>();
        await bootstrapper.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetConnectionString(IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionStringName)
        ?? throw new InvalidOperationException(
            $"Connection string '{ConnectionStringName}' is not configured. Set ConnectionStrings__{ConnectionStringName} or run via the Aspire AppHost.");
}
