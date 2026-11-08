using System.Data.Common;
using Agcowork.Application.Collections;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;
using Agcowork.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agcowork.Application.Tests.Records;

/// <summary>
/// The backlink statement on 100,000 records (AP-037, acceptance criterion 3): run as the application role under row-level
/// security, the planner uses the GIN index <c>(workspace_id, properties jsonb_path_ops)</c> and never scans all records.
/// The plan (EXPLAIN ANALYZE of the very statement the use case sends) is written to the test output.
/// </summary>
[Trait("Category", "Slow")]
public sealed class BacklinksPlanTests(PostgresFixture postgres, ITestOutputHelper output) : RecordTestBase(postgres)
{
    private const int SourceRecords = 100_000;
    private const int Targets = 1_000;

    [Fact]
    public async Task Backlinks_On100kRecords_UseGinIndex_NoSeqScan()
    {
        var a = await ArrangeAsync();
        var decisions = await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Entscheidungen");
        Db.ChangeTracker.Clear();
        var schema = await new UpdateSchema(Db, a.Tenant, [], Activity).HandleAsync(new UpdateSchemaRequest(
            a.Collection.Id,
            Add: [new PropertyDefinitionInput("decisions", "Decisions", "relation", TargetCollectionId: decisions.Id, Multiple: true)]), Ct);
        Assert.True(schema.IsSuccess, Describe(schema.Error));
        Db.ChangeTracker.Clear();
        var selfRelation = await new UpdateSchema(Db, a.Tenant, [], Activity).HandleAsync(new UpdateSchemaRequest(
            decisions.Id,
            Add: [new PropertyDefinitionInput("supersedes", "Supersedes", "relation", TargetCollectionId: decisions.Id)]), Ct);
        Assert.True(selfRelation.IsSuccess, Describe(selfRelation.Error));
        Db.ChangeTracker.Clear();

        // Seed as the owner, past the search trigger (session_replication_role; empty search vectors): 1,000 decisions and 100,000 tasks, each
        // referencing one decision (100 backlinks per decision), spread over time.
        var workspace = a.Seed.Alpha.Id;
        var actor = a.Seed.AlphaOwner.Id;
        await using (var owner = PostgresFixture.OpenOwner(Db))
        {
            owner.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
            await owner.Database.OpenConnectionAsync(Ct);
            await owner.Database.ExecuteSqlRawAsync("SET session_replication_role = replica", Ct);
            await owner.Database.ExecuteSqlAsync($"""
                INSERT INTO records (id, workspace_id, space_id, collection_id, title, properties, body, revision_no, created_by, created_at, updated_by, updated_at, search_vector)
                SELECT gen_random_uuid(), {workspace}, {a.SpaceId}, {decisions.Id}, 'Decision ' || i, jsonb_build_object(), '', 1, {actor},
                       now() - make_interval(secs => i), {actor}, now(), ''::tsvector
                FROM generate_series(1, {Targets}) AS i
                """, Ct);
            await owner.Database.ExecuteSqlAsync($"""
                INSERT INTO records (id, workspace_id, space_id, collection_id, title, properties, body, revision_no, created_by, created_at, updated_by, updated_at, search_vector)
                SELECT gen_random_uuid(), {workspace}, {a.SpaceId}, {a.Collection.Id}, 'Task ' || s.i,
                       jsonb_build_object('status', 'todo', 'decisions', jsonb_build_array(d.id::text)), '', 1, {actor},
                       now() - make_interval(secs => s.i), {actor}, now(), ''::tsvector
                FROM generate_series(1, {SourceRecords}) AS s(i)
                JOIN (SELECT id, row_number() OVER (ORDER BY id) - 1 AS n FROM records WHERE collection_id = {decisions.Id}) AS d
                  ON d.n = s.i % {Targets}
                """, Ct);
            // Every decision supersedes its predecessor (self-relation, scalar values).
            await owner.Database.ExecuteSqlAsync($"""
                UPDATE records AS r SET properties = jsonb_build_object('supersedes', p.previous::text)
                FROM (SELECT id, lag(id) OVER (ORDER BY id) AS previous FROM records WHERE collection_id = {decisions.Id}) AS p
                WHERE r.id = p.id AND p.previous IS NOT NULL
                """, Ct);
            await owner.Database.ExecuteSqlRawAsync("SET session_replication_role = DEFAULT", Ct);
            await owner.Database.ExecuteSqlRawAsync("ANALYZE records", Ct);
        }

        // The first decision: 100 tasks and its successor reference it.
        var target = await Db.Records.AsNoTracking().Where(r => r.CollectionId == decisions.Id).OrderBy(r => r.Id).Select(r => r.Id).FirstAsync(Ct);
        var explain = new ExplainBacklinks();
        await using var explained = new AgcoworkDbContext(
            new DbContextOptionsBuilder<AgcoworkDbContext>((DbContextOptions<AgcoworkDbContext>)Db.GetService<IDbContextOptions>())
                .AddInterceptors(explain)
                .Options,
            Tenant);

        var result = await new GetBacklinks(explained, a.Tenant, RecordTranslator).HandleAsync(new GetBacklinksRequest(target, Limit: 50), Ct);

        Assert.True(result.IsSuccess, Describe(result.Error));
        Assert.Equal(["decisions", "supersedes"], result.Value.Groups.Select(g => g.PropertyKey));
        Assert.Equal(50, result.Value.Groups[0].Items.Count);
        Assert.NotNull(result.Value.Groups[0].NextCursor);
        Assert.Single(result.Value.Groups[1].Items);
        var plan = Assert.Single(explain.Plans);
        output.WriteLine(explain.Statement);
        output.WriteLine(plan);
        Assert.Contains("UNION ALL", explain.Statement, StringComparison.Ordinal);
        Assert.Contains("Bitmap Index Scan on ix_records_workspace_id_properties", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Seq Scan on records", plan, StringComparison.Ordinal);
    }

    /// <summary>Runs EXPLAIN ANALYZE of the backlink statement (the one on records with <c>@&gt;</c>) on the same connection right before it.</summary>
    private sealed class ExplainBacklinks : DbCommandInterceptor
    {
        public List<string> Plans { get; } = [];

        public string Statement { get; private set; } = string.Empty;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            // The backlink statement; the prefilter of the source collections (schema @> …) reads collections, not records.
            if (!command.CommandText.Contains("@>", StringComparison.Ordinal) || !command.CommandText.Contains("FROM records", StringComparison.Ordinal))
            {
                return result;
            }

            Statement = command.CommandText;
            await using var explain = command.Connection!.CreateCommand();
            explain.Transaction = command.Transaction;
            explain.CommandText = "EXPLAIN (ANALYZE, BUFFERS, COSTS OFF) " + command.CommandText;
            foreach (DbParameter parameter in command.Parameters)
            {
                explain.Parameters.Add(((ICloneable)parameter).Clone());
            }

            var lines = new List<string>();
            await using (var reader = await explain.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    lines.Add(reader.GetString(0));
                }
            }

            Plans.Add(string.Join('\n', lines));
            return result;
        }
    }
}
