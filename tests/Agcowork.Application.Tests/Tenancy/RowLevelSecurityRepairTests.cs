using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Agcowork.Application.Tests.Tenancy;

/// <summary>
/// Startup heals row-level security (AP-036): privileges, default privileges and LEAKPROOF marks lost after the migration
/// (manual change, restore without privileges, pg_upgrade) are back after <c>ApplyMigrationsAsync</c>.
/// </summary>
public sealed class RowLevelSecurityRepairTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task ApplyMigrations_PrivilegesAndLeakproofLost_StartRestoresThem()
    {
        await using (var owner = PostgresFixture.OpenOwner(Db))
        {
            await owner.Database.ExecuteSqlRawAsync("REVOKE ALL ON TABLE records FROM agcowork_app", Ct);
            await owner.Database.ExecuteSqlRawAsync("REVOKE EXECUTE ON FUNCTION agcowork_records_search_vector() FROM agcowork_app", Ct);
            await owner.Database.ExecuteSqlRawAsync("ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM agcowork_app", Ct);
            await owner.Database.ExecuteSqlRawAsync("ALTER FUNCTION pg_catalog.jsonb_contains(jsonb, jsonb) NOT LEAKPROOF", Ct);
            await owner.Database.ExecuteSqlRawAsync("ALTER FUNCTION pg_catalog.ts_match_vq(tsvector, tsquery) NOT LEAKPROOF", Ct);
        }

        var broken = await Assert.ThrowsAsync<PostgresException>(() => Db.Records.IgnoreQueryFilters().CountAsync(Ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, broken.SqlState);
        Assert.False(await StateAsync(Leakproof));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{Infrastructure.DependencyInjection.ConnectionStringName}"] = Db.Database.GetConnectionString(),
            })
            .Build();
        await using (var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider())
        {
            await Infrastructure.DependencyInjection.ApplyMigrationsAsync(services, Ct);
        }

        Assert.Equal(0, await Db.Records.IgnoreQueryFilters().CountAsync(Ct));
        Assert.True(await StateAsync("has_table_privilege('agcowork_app', 'records', 'SELECT') AND has_table_privilege('agcowork_app', 'records', 'INSERT') AND has_table_privilege('agcowork_app', 'records', 'UPDATE') AND has_table_privilege('agcowork_app', 'records', 'DELETE')"));
        Assert.True(await StateAsync("has_function_privilege('agcowork_app', 'agcowork_records_search_vector()', 'EXECUTE')"));
        Assert.True(await StateAsync("EXISTS (SELECT FROM pg_default_acl d, aclexplode(d.defaclacl) a JOIN pg_roles r ON r.oid = a.grantee WHERE r.rolname = 'agcowork_app' AND d.defaclobjtype = 'r' AND a.privilege_type = 'INSERT')"));
        Assert.True(await StateAsync(Leakproof));
    }

    private const string Leakproof =
        "(SELECT bool_and(proleakproof) FROM pg_proc WHERE oid IN ('pg_catalog.jsonb_contains(jsonb, jsonb)'::regprocedure, 'pg_catalog.ts_match_vq(tsvector, tsquery)'::regprocedure))";

    private async Task<bool> StateAsync(string expression)
    {
        await using var owner = PostgresFixture.OpenOwner(Db);
        // Fixed expressions of this class, no input.
        var sql = "SELECT " + expression + " AS \"Value\"";
        return await owner.Database.SqlQueryRaw<bool>(sql).SingleAsync(Ct);
    }
}
