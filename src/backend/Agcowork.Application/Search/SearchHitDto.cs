namespace Agcowork.Application.Search;

/// <summary>One search hit: where the record lives and an excerpt around the match.</summary>
/// <param name="RecordId">The record; read it with <c>GET …/records/{id}</c> or <c>record_get</c>.</param>
/// <param name="Title">Current title.</param>
/// <param name="CollectionId">Collection of the record; null for a free page of the space.</param>
/// <param name="SpaceId">Space of the record.</param>
/// <param name="Snippet">
/// Excerpt of the indexed property text and the body with every match between <c>⟦</c> and <c>⟧</c> (U+27E6, U+27E7). The
/// snippet is plain text and can contain <c>&lt;</c>, <c>&gt;</c> and markup from the content: clients must render it as text
/// and highlight between the markers themselves. Only <c>⟦</c> and <c>⟧</c> come from the server (in the content they appear
/// as <c>[</c> and <c>]</c>). Without markers when only the title matched or the match lies beyond the first 20000 characters.
/// </param>
/// <param name="Rank">
/// Relevance, higher is better: 1 or more when the title alone matches the whole query, then by how densely the words occur
/// (title before properties before body). Only comparable within one search.
/// </param>
/// <param name="UpdatedAt">Last change of the record.</param>
/// <param name="Archived">
/// True when the record, its collection or its space is archived (such hits only come with <c>includeArchived</c>).
/// </param>
public sealed record SearchHitDto(
    Guid RecordId,
    string Title,
    Guid? CollectionId,
    Guid SpaceId,
    string Snippet,
    float Rank,
    DateTimeOffset UpdatedAt,
    bool Archived);
