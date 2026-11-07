using Agcowork.Domain.Views;

namespace Agcowork.Application.Views;

/// <summary>A view of a collection: how its records are presented.</summary>
/// <param name="Id">View id.</param>
/// <param name="CollectionId">Collection the view belongs to.</param>
/// <param name="Name">Display name, unique within the collection.</param>
/// <param name="Type">table, board, gallery, list, document or raw.</param>
/// <param name="Filter">Filter DSL (and/or/not around { prop, op, value } conditions); null = all records.</param>
/// <param name="Sorts">Sort criteria in order: { prop, dir (asc|desc) }.</param>
/// <param name="GroupBy">Key of the select/status property that groups the records (board columns). Required for boards.</param>
/// <param name="VisibleProperties">Keys of the properties shown; empty = all.</param>
/// <param name="CardCoverProperty">Gallery: key of the property used as card cover.</param>
/// <param name="IsDefault">The collection's default view; exactly one per collection.</param>
/// <param name="IsSystem">Standard view (table, raw): cannot be deleted, type cannot change.</param>
/// <param name="SortOrder">Position among the collection's views.</param>
/// <param name="NeedsAttention">true when a board lost its groupBy property because the property was removed from the schema; set a new groupBy.</param>
/// <param name="CreatedBy">Actor that created the view.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
public sealed record ViewDto(
    Guid Id,
    Guid CollectionId,
    string Name,
    string Type,
    ViewFilterNode? Filter,
    IReadOnlyList<ViewSort> Sorts,
    string? GroupBy,
    IReadOnlyList<string> VisibleProperties,
    string? CardCoverProperty,
    bool IsDefault,
    bool IsSystem,
    int SortOrder,
    bool NeedsAttention,
    Guid CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static ViewDto From(View view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new ViewDto(
            view.Id,
            view.CollectionId,
            view.Name,
            ViewTypes.ToWire(view.Type),
            view.Filter,
            [.. view.Sorts],
            view.GroupByProperty,
            [.. view.VisibleProperties],
            view.CardCoverProperty,
            view.IsDefault,
            view.IsSystem,
            view.SortOrder,
            view.NeedsAttention,
            view.CreatedBy,
            view.CreatedAt);
    }
}
