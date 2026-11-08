using Agcowork.Application.Common;
using Agcowork.Application.Common.Paging;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Query;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Search;

/// <param name="Q">
/// Search input, 1..200 characters, web search syntax: words (all must occur), <c>"exact phrase"</c>, <c>-word</c> to exclude,
/// <c>or</c> between alternatives. The last word (at least 2 letters or digits) also matches as a prefix (<c>entsch</c> finds
/// "Entscheidung") unless it is quoted, excluded or <c>or</c>. Case-insensitive, no stemming.
/// </param>
/// <param name="SpaceId">Only records of this space.</param>
/// <param name="CollectionId">Only records of this collection.</param>
/// <param name="IncludeArchived">Also archived records, including records of archived collections and spaces. Default false.</param>
/// <param name="Limit">Page size 1..50, default 20.</param>
/// <param name="Cursor"><c>nextCursor</c> of the previous page, with the same <c>q</c>.</param>
public sealed record SearchRecordsRequest(
    string? Q,
    Guid? SpaceId = null,
    Guid? CollectionId = null,
    bool IncludeArchived = false,
    int? Limit = null,
    string? Cursor = null);

/// <summary>
/// Full-text search over title (weight A), text-like properties and option labels (B) and the first
/// <see cref="RecordSearch.BodyPrefixLength"/> characters of the body (C), across all collections and free pages of the
/// workspace. Ordered by rank (descending), then id; keyset-paginated. Snippets are computed for the returned hits only.
/// Requires <c>read</c>. Unknown <c>spaceId</c>/<c>collectionId</c>: 404. Input without a word that must match: 400
/// <c>empty_query</c>.
/// </summary>
public sealed class SearchRecords(IAgcoworkDbContext db, ITenantContext tenant, IRecordSearchIndex index) : IUseCase<SearchRecordsRequest, Page<SearchHitDto>>
{
    public async Task<Result<Page<SearchHitDto>>> HandleAsync(SearchRecordsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        var input = ValidateInput(errors, request.Q);
        var limit = request.Limit ?? RecordSearch.DefaultLimit;
        if (limit is < 1 or > RecordSearch.MaxLimit)
        {
            errors.Add("limit", ErrorCodes.OutOfRange, $"'limit' must be between 1 and {RecordSearch.MaxLimit}.");
        }

        if (errors.Any || input is null)
        {
            return errors.ToError();
        }

        if (request.SpaceId is { } spaceId && !await db.Spaces.AnyAsync(s => s.Id == spaceId, cancellationToken).ConfigureAwait(false))
        {
            return new NotFoundError("space");
        }

        if (request.CollectionId is { } collectionId && !await db.Collections.AnyAsync(c => c.Id == collectionId, cancellationToken).ConfigureAwait(false))
        {
            return new NotFoundError("collection");
        }

        var query = await index.ParseAsync(input, PrefixLastTerm(input), cancellationToken).ConfigureAwait(false);
        if (query is null)
        {
            errors.Add("q", ErrorCodes.EmptyQuery, "'q' contains no word to search for. Send at least one word (not only punctuation or -excluded words).");
            return errors.ToError();
        }

        SearchCursor? after = null;
        if (!string.IsNullOrEmpty(request.Cursor) && (after = SearchCursor.TryDecode(request.Cursor, query)) is null)
        {
            errors.Add("cursor", ErrorCodes.InvalidCursor, "'cursor' is not valid for this search. Pass 'nextCursor' of the previous page unchanged with the same 'q', or omit it for the first page.");
            return errors.ToError();
        }

        // Tenant filter of the context applies to every query below; nothing here bypasses it.
        var records = db.Records.AsNoTracking();
        if (!request.IncludeArchived)
        {
            // Records in an archived collection or space count as archived (they are read-only and hidden in lists too).
            records = records.Where(r => r.ArchivedAt == null
                && !db.Spaces.Any(s => s.Id == r.SpaceId && s.ArchivedAt != null)
                && !db.Collections.Any(c => c.Id == r.CollectionId && c.ArchivedAt != null));
        }

        if (request.SpaceId is { } space)
        {
            records = records.Where(r => r.SpaceId == space);
        }

        if (request.CollectionId is { } collection)
        {
            records = records.Where(r => r.CollectionId == collection);
        }

        var hits = index.Match(records, query);
        if (after is not null)
        {
            var (rank, id) = (after.Rank, after.Id);
            hits = hits.Where(h => h.Rank < rank || (h.Rank == rank && h.Id > id));
        }

        var rows = await hits.OrderByDescending(h => h.Rank).ThenBy(h => h.Id).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? new SearchCursor(page[^1].Rank, page[^1].Id).Encode(query) : null;

        var ids = page.Select(h => h.Id).ToList();
        IReadOnlyDictionary<Guid, string?> snippets = ids.Count == 0
            ? new Dictionary<Guid, string?>()
            : await index.Snippets(db.Records.AsNoTracking().Where(r => ids.Contains(r.Id)), query)
                .ToDictionaryAsync(s => s.Id, s => s.Snippet, cancellationToken)
                .ConfigureAwait(false);

        var archivedContainers = request.IncludeArchived && page.Count > 0
            ? await ArchivedContainersAsync(page, cancellationToken).ConfigureAwait(false)
            : [];
        return new Page<SearchHitDto>(
            [.. page.Select(h => new SearchHitDto(
                h.Id,
                h.Title,
                h.CollectionId,
                h.SpaceId,
                snippets.GetValueOrDefault(h.Id) ?? string.Empty,
                h.Rank,
                h.UpdatedAt,
                h.ArchivedAt is not null || archivedContainers.Contains(h.SpaceId) || (h.CollectionId is { } c && archivedContainers.Contains(c))))],
            next);
    }

    /// <summary>The archived spaces and collections among those of the hits.</summary>
    private async Task<HashSet<Guid>> ArchivedContainersAsync(IReadOnlyList<SearchCandidate> page, CancellationToken cancellationToken)
    {
        var spaceIds = page.Select(h => h.SpaceId).Distinct().ToList();
        var collectionIds = page.Where(h => h.CollectionId is not null).Select(h => h.CollectionId!.Value).Distinct().ToList();
        var spaces = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id) && s.ArchivedAt != null).Select(s => s.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var collections = await db.Collections.AsNoTracking().Where(c => collectionIds.Contains(c.Id) && c.ArchivedAt != null).Select(c => c.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. spaces, .. collections];
    }

    /// <summary>The trimmed input, or null with an error: missing, too long, control characters or nothing but whitespace.</summary>
    private static string? ValidateInput(FieldErrors errors, string? q)
    {
        if (q is null)
        {
            errors.Add("q", ErrorCodes.Required, "'q' is required: the words to search for.");
            return null;
        }

        // NUL and unpaired surrogates cannot reach PostgreSQL as text; other control characters are no search input.
        if (!QueryText.IsStorable(q) || q.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')))
        {
            errors.Add("q", ErrorCodes.InvalidValue, "'q' must not contain control characters.");
            return null;
        }

        var trimmed = q.Trim();
        if (trimmed.Length > RecordSearch.MaxQueryLength)
        {
            errors.Add("q", ErrorCodes.TooLong, $"'q' must not exceed {RecordSearch.MaxQueryLength} characters.");
            return null;
        }

        if (trimmed.Length == 0)
        {
            errors.Add("q", ErrorCodes.EmptyQuery, "'q' is empty. Send at least one word to search for.");
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// Whether the last term is a plain word being typed: it ends with at least 2 letters or digits (a single character as
    /// prefix would match almost everything), is not inside or after a quoted phrase, not excluded (<c>-word</c>) and not the
    /// operator <c>or</c>.
    /// </summary>
    public static bool PrefixLastTerm(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length < 2 || !char.IsLetterOrDigit(input[^1]) || !char.IsLetterOrDigit(input[^2]) || input.Count(c => c == '"') % 2 != 0)
        {
            return false;
        }

        var start = 0;
        for (var i = input.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(input[i]))
            {
                start = i + 1;
                break;
            }
        }

        var last = input[start..];
        return !last.StartsWith('-') && !last.Equals("or", StringComparison.OrdinalIgnoreCase);
    }
}
