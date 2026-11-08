using Agcowork.Domain.Records;

namespace Agcowork.Application.Search;

/// <summary>
/// The full-text index of records (implemented in Infrastructure: <c>records.search_vector</c>, PostgreSQL text search with
/// the <c>simple</c> configuration). Every method composes on the given source, so the tenant query filter stays in force;
/// the query text is always a parameter, never SQL text.
/// </summary>
public interface IRecordSearchIndex
{
    /// <summary>
    /// Parses user input in web search syntax (words, <c>"phrase"</c>, <c>-exclude</c>, <c>or</c>) into the canonical text
    /// of a <c>tsquery</c>; with <paramref name="prefixLastTerm"/> the last term also matches words starting with it.
    /// Null when the input has no word that must match (only punctuation, only exclusions).
    /// </summary>
    Task<string?> ParseAsync(string input, bool prefixLastTerm, CancellationToken cancellationToken);

    /// <summary>The records of <paramref name="records"/> that match <paramref name="query"/> (from <see cref="ParseAsync"/>), with their rank.</summary>
    IQueryable<SearchCandidate> Match(IQueryable<Record> records, string query);

    /// <summary>A highlighted excerpt per record (<see cref="RecordSearch.HighlightStart"/>/<see cref="RecordSearch.HighlightEnd"/> around matches).</summary>
    IQueryable<RecordSnippet> Snippets(IQueryable<Record> records, string query);
}

/// <summary>A record that matches a search, before its snippet is computed.</summary>
public sealed class SearchCandidate
{
    public Guid Id { get; init; }

    public Guid SpaceId { get; init; }

    public Guid? CollectionId { get; init; }

    public string Title { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ArchivedAt { get; init; }

    /// <summary>See <see cref="SearchHitDto.Rank"/>.</summary>
    public float Rank { get; init; }
}

/// <summary>The highlighted excerpt of one record.</summary>
public sealed class RecordSnippet
{
    public Guid Id { get; init; }

    public string? Snippet { get; init; }
}

/// <summary>Limits and conventions of the full-text search, shared by the use case, the adapters' descriptions and the index.</summary>
public static class RecordSearch
{
    /// <summary>Maximum length of the search input.</summary>
    public const int MaxQueryLength = 200;

    public const int DefaultLimit = 20;

    public const int MaxLimit = 50;

    /// <summary>
    /// Only the first characters of the body are indexed: PostgreSQL refuses a tsvector over 1 MB (1048575 bytes), a body may
    /// have 2 MiB, and the measured worst case (4-byte letters in hyphenated compounds) takes about 8 bytes per character.
    /// (500 title + <see cref="PropertiesPrefixLength"/> + this) × 8 ≈ 804 KB. The migration <c>AddRecordSearch</c> uses the
    /// same numbers (a test checks the body prefix) and falls back to shorter prefixes, then the title, should a vector still
    /// exceed the limit.
    /// </summary>
    public const int BodyPrefixLength = 75_000;

    /// <summary>Only the first characters of the indexed property text (text-like values and option labels) are indexed.</summary>
    public const int PropertiesPrefixLength = 25_000;

    /// <summary>
    /// Snippets are cut from the first characters of property text plus body prefix only: <c>ts_headline</c> parses its whole
    /// input, and on 100000 characters with dense matches it takes tens to hundreds of milliseconds per hit. A match further
    /// down still finds the record; its snippet then shows the beginning without markers.
    /// </summary>
    public const int SnippetSourceLength = 20_000;

    /// <summary>Marks the start of a match in a snippet. Never part of the snippet's source text (replaced by <c>[</c> there).</summary>
    public const string HighlightStart = "⟦";

    /// <summary>Marks the end of a match in a snippet. Never part of the snippet's source text (replaced by <c>]</c>).</summary>
    public const string HighlightEnd = "⟧";
}
