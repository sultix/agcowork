using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Search;
using Agcowork.Domain.Records;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Infrastructure.Search;

/// <summary>
/// PostgreSQL full-text search over <c>records.search_vector</c> (migration <c>AddRecordSearch</c>): a trigger keeps the
/// vector up to date, weighted title A, text-like properties and option labels B, body prefix C, configuration
/// <c>simple</c>. GIN index <c>(workspace_id, search_vector)</c>; every query here composes on the tenant-filtered records,
/// so <c>workspace_id</c> is always part of the condition.
/// </summary>
internal sealed class RecordSearchIndex(IAgcoworkDbContext db) : IRecordSearchIndex
{
    public async Task<string?> ParseAsync(string input, bool prefixLastTerm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        // websearch_to_tsquery never fails on user input. numnode = 0: no lexeme at all (only punctuation); querytree 'T':
        // nothing the index can look up (only exclusions), which would scan every record of the workspace.
        var parsed = await db.Database
            .SqlQuery<ParsedQuery>($"""
                SELECT q::text AS text, numnode(q) AS nodes, querytree(q) AS tree
                FROM websearch_to_tsquery('simple', {input}) AS q
                """)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        if (parsed.Nodes == 0 || parsed.Tree == "T")
        {
            return null;
        }

        // The canonical text quotes every lexeme ('word'); a trailing lexeme becomes a prefix match ('word':*). A trailing
        // ')' (negated group) stays as it is.
        return prefixLastTerm && parsed.Text.EndsWith('\'') ? parsed.Text + ":*" : parsed.Text;
    }

    public IQueryable<SearchCandidate> Match(IQueryable<Record> records, string query)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(query);

        // Rank: cover density over the weighted vector, normalized to [0, 1) (rank / (rank + 1)), plus 1 when the title alone
        // matches the whole query. So a title hit always ranks before a hit only in properties or body, however often the
        // word occurs there.
        return records
            .Where(r => RecordSearchFunctions.Vector(r.Id).Matches(RecordSearchFunctions.Query(query)))
            .Select(r => new SearchCandidate
            {
                Id = r.Id,
                SpaceId = r.SpaceId,
                CollectionId = r.CollectionId,
                Title = r.Title,
                UpdatedAt = r.UpdatedAt,
                ArchivedAt = r.ArchivedAt,
                Rank = RecordSearchFunctions.Real(
                    RecordSearchFunctions.Vector(r.Id).RankCoverDensity(RecordSearchFunctions.Query(query), NpgsqlTsRankingNormalization.DivideByItselfPlusOne)
                    + (EF.Functions.ToTsVector(RecordSearchFunctions.Configuration, r.Title).Matches(RecordSearchFunctions.Query(query)) ? 1f : 0f)),
            });
    }

    public IQueryable<RecordSnippet> Snippets(IQueryable<Record> records, string query)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(query);
        var options = RecordSearchFunctions.HeadlineOptions;
        return records.Select(r => new RecordSnippet
        {
            Id = r.Id,
            Snippet = RecordSearchFunctions.Query(query).GetResultHeadline(
                RecordSearchFunctions.Configuration,
                RecordSearchFunctions.Document(r.WorkspaceId, r.CollectionId, r.Properties, r.Body),
                options),
        });
    }

    private sealed class ParsedQuery
    {
        public string Text { get; init; } = string.Empty;

        public int Nodes { get; init; }

        public string Tree { get; init; } = string.Empty;
    }
}
