using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Identity;
using Agcowork.Application.Identity.Throttling;
using Agcowork.Application.Query;
using Agcowork.Application.Search;
using Agcowork.Infrastructure.Identity;
using Agcowork.Infrastructure.Persistence;
using Agcowork.Infrastructure.Query;
using Agcowork.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        services.AddScoped<IRecordSearchIndex, RecordSearchIndex>();

        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<IApiKeyGenerator, ApiKeyGenerator>();
        services.AddOptions<BootstrapOptions>().Bind(configuration.GetSection(BootstrapOptions.SectionName));
        services.AddScoped<IdentityBootstrapper>();

        services.AddHealthChecks()
            .AddNpgSql(_ => GetConnectionString(configuration), name: "postgres", tags: [ReadyTag]);

        return services;
    }

    /// <summary>
    /// Login and password-change throttling (AP-043): binds <see cref="CredentialThrottlingOptions"/> from
    /// <c>Agcowork:CredentialThrottling</c>, validates them on start and registers the in-memory limiter (single instance,
    /// architecture §8). Switching it off (<c>Enabled=false</c>) is for tests: outside Development the host refuses to start.
    /// </summary>
    public static IServiceCollection AddCredentialThrottling(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<CredentialThrottlingOptions>()
            .Bind(configuration.GetSection(CredentialThrottlingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CredentialThrottlingOptions>>(
            new CredentialThrottlingValidation(allowDisabled: environment.IsDevelopment()));
        services.AddSingleton<ICredentialAttemptLimiter, InMemoryCredentialAttemptLimiter>();
        return services;
    }

    /// <summary>
    /// Applies pending EF Core migrations. Startup-time migration assumes a single instance (architecture §8). Runs as the
    /// login of the connection string, without the switch to the application role (<see cref="AgcoworkDbContextOptions.ConfigureOwner"/>):
    /// migrations create the role and the policies, and their backfills must see every workspace. Afterwards it re-applies the
    /// role, its privileges and the LEAKPROOF marks of row-level security (<see cref="RowLevelSecurity.RepairAsync"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection string enables Npgsql multiplexing, which row-level security cannot work with.</exception>
    public static async Task ApplyMigrationsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var connectionString = GetConnectionString(services.GetRequiredService<IConfiguration>());
        RowLevelSecurity.EnsureSupported(connectionString);
        var options = new DbContextOptionsBuilder<AgcoworkDbContext>();
        AgcoworkDbContextOptions.ConfigureOwner(options, connectionString);
        await using var db = new AgcoworkDbContext(options.Options);
        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(RowLevelSecurity).FullName!) ?? NullLogger.Instance;
        await RowLevelSecurity.RepairAsync(db, logger, cancellationToken).ConfigureAwait(false);
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
