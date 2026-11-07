using Agcowork.Application.Common.Security;
using Agcowork.Domain.Identity;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Agcowork.Application.Tests;

/// <summary>
/// One PostgreSQL container for all application tests. The schema is migrated once into a template
/// database; every test gets its own empty copy (<c>CREATE DATABASE … TEMPLATE</c>, a few ms), so tests
/// see real SQL behaviour (unique indexes, <c>ExecuteUpdate</c>, arrays) and never share data.
/// The EF in-memory provider is not used: it cannot execute bulk updates and ignores constraints.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string TemplateDatabase = "agcowork_template";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase(TemplateDatabase)
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(TestContext.Current.CancellationToken);

        await using (var db = Open(_postgres.GetConnectionString()))
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        // A template must have no open connections when it is copied.
        NpgsqlConnection.ClearAllPools();
    }

    /// <summary>Creates a fresh, migrated, empty database and returns a context on it.</summary>
    public async Task<AgcoworkDbContext> CreateDatabaseAsync()
    {
        var name = $"t_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(WithDatabase("postgres")))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE {TemplateDatabase}", connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return Open(WithDatabase(name));
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    private string WithDatabase(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database, Pooling = false }.ConnectionString;

    private static AgcoworkDbContext Open(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AgcoworkDbContext>();
        AgcoworkDbContextOptions.Configure(options, connectionString);
        return new AgcoworkDbContext(options.Options);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>Base for use-case tests: a fresh database per test in <see cref="Db"/>.</summary>
[Collection(PostgresGroup.Name)]
public abstract class DatabaseTest(PostgresFixture postgres) : IAsyncLifetime
{
    private AgcoworkDbContext? _db;

    protected AgcoworkDbContext Db => _db ?? throw new InvalidOperationException("Database not initialized.");

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _db = await postgres.CreateDatabaseAsync();

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Configurable <see cref="ICurrentActor"/> for use-case tests.</summary>
internal sealed class TestActor : ICurrentActor
{
    private readonly Guid? _id;

    private TestActor(Guid? id, ActorKind kind, IEnumerable<string> scopes)
    {
        _id = id;
        Kind = kind;
        InstanceScopes = scopes.ToHashSet(StringComparer.Ordinal);
    }

    public static TestActor Anonymous { get; } = new(null, ActorKind.Human, []);

    public static TestActor Admin(Guid? id = null) => new(id ?? Guid.CreateVersion7(), ActorKind.Human, [InstanceScope.Admin]);

    public static TestActor Agent(Guid? id = null, params string[] scopes) => new(id ?? Guid.CreateVersion7(), ActorKind.Agent, scopes);

    public bool IsAuthenticated => _id is not null;

    public Guid Id => _id ?? throw new InvalidOperationException("Anonymous.");

    public ActorKind Kind { get; }

    public IReadOnlySet<string> InstanceScopes { get; }

    public Guid? ApiKeyId => null;

    public Guid? KeyWorkspaceRestriction => null;

    public IReadOnlySet<string> KeyScopes { get; } = new HashSet<string>();
}
