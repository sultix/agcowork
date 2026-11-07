using System.Text.Json.Nodes;
using Agcowork.Application.Activity;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
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

    /// <summary>
    /// Creates a fresh, migrated, empty database and returns a context on it. Pass a tenant accessor to
    /// get the tenant query filter and interceptor behaviour of a request; without one the context acts
    /// like one outside any tenant (tenant entities invisible, writes to them rejected).
    /// </summary>
    public async Task<AgcoworkDbContext> CreateDatabaseAsync(ITenantContextAccessor? tenantAccessor = null)
    {
        var name = $"t_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(WithDatabase("postgres")))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE {TemplateDatabase}", connection);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return Open(WithDatabase(name), tenantAccessor);
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    private string WithDatabase(string database) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database, Pooling = false }.ConnectionString;

    private static AgcoworkDbContext Open(string connectionString, ITenantContextAccessor? tenantAccessor = null)
    {
        var options = new DbContextOptionsBuilder<AgcoworkDbContext>();
        AgcoworkDbContextOptions.Configure(options, connectionString);
        return new AgcoworkDbContext(options.Options, tenantAccessor);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// Base for use-case tests: a fresh database per test in <see cref="Db"/>. <see cref="Tenant"/> controls the
/// tenant <see cref="Db"/> sees (none by default); set it before calling workspace use cases.
/// </summary>
[Collection(PostgresGroup.Name)]
public abstract class DatabaseTest(PostgresFixture postgres) : IAsyncLifetime
{
    private AgcoworkDbContext? _db;

    protected AgcoworkDbContext Db => _db ?? throw new InvalidOperationException("Database not initialized.");

    /// <summary>Tenant of <see cref="Db"/>; switchable within a test, unlike the production accessor.</summary>
    protected TestTenantAccessor Tenant { get; } = new();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Activity recorder for use cases under test: actor and workspace of <see cref="Tenant"/> at the time of the call.</summary>
    internal IActivityRecorder Activity => new TestActivityRecorder(Db, Tenant, ActivityClock);

    /// <summary>Clock of <see cref="Activity"/>; tests with a fake clock return it here.</summary>
    protected virtual TimeProvider ActivityClock => TimeProvider.System;

    public async ValueTask InitializeAsync() => _db = await postgres.CreateDatabaseAsync(Tenant);

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>Tenant accessor for tests: <see cref="Current"/> can be replaced at any time.</summary>
public sealed class TestTenantAccessor : ITenantContextAccessor
{
    public ITenantContext? Current { get; set; }

    public void SetTenant(ITenantContext tenant) => Current = tenant;
}

/// <summary>Configurable <see cref="ICurrentActor"/> for use-case tests.</summary>
internal sealed class TestActor : ICurrentActor
{
    private readonly Guid? _id;

    private TestActor(Guid? id, ActorKind kind, IEnumerable<string> scopes, Guid? apiKeyId = null, Guid? keyWorkspace = null, IEnumerable<string>? keyScopes = null)
    {
        _id = id;
        Kind = kind;
        InstanceScopes = scopes.ToHashSet(StringComparer.Ordinal);
        ApiKeyId = apiKeyId;
        KeyWorkspaceRestriction = keyWorkspace;
        KeyScopes = (keyScopes ?? []).ToHashSet(StringComparer.Ordinal);
    }

    public static TestActor Anonymous { get; } = new(null, ActorKind.Human, []);

    public static TestActor Admin(Guid? id = null) => new(id ?? Guid.CreateVersion7(), ActorKind.Human, [InstanceScope.Admin]);

    public static TestActor Agent(Guid? id = null, params string[] scopes) => new(id ?? Guid.CreateVersion7(), ActorKind.Agent, scopes);

    /// <summary>The same actor calling with an API key that carries the given restrictions.</summary>
    public TestActor WithKey(Guid? workspaceRestriction = null, params string[] keyScopes) =>
        new(Id, Kind, InstanceScopes, Guid.CreateVersion7(), workspaceRestriction, keyScopes);

    public bool IsAuthenticated => _id is not null;

    public Guid Id => _id ?? throw new InvalidOperationException("Anonymous.");

    public ActorKind Kind { get; }

    public IReadOnlySet<string> InstanceScopes { get; }

    public Guid? ApiKeyId { get; }

    public Guid? KeyWorkspaceRestriction { get; }

    public IReadOnlySet<string> KeyScopes { get; }
}

/// <summary>The production <see cref="ActivityRecorder"/>, with the caller taken from the current test tenant.</summary>
internal sealed class TestActivityRecorder(AgcoworkDbContext db, TestTenantAccessor tenant, TimeProvider clock) : IActivityRecorder
{
    public ActivityEvent Record(string type, string targetType, Guid targetId, Guid? spaceId = null, Guid? collectionId = null, JsonObject? diff = null)
    {
        var current = tenant.Current ?? throw new InvalidOperationException("No tenant set.");
        return new ActivityRecorder(db, TestActor.Agent(current.Membership.ActorId), current, clock)
            .Record(type, targetType, targetId, spaceId, collectionId, diff);
    }
}
