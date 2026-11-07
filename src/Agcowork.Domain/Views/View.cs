using Agcowork.Domain.Common;

namespace Agcowork.Domain.Views;

/// <summary>
/// A way to present a collection: type, filter, sorting, grouping and visible properties (architecture §3). Views are data;
/// the UI renders them. Every collection has system views <c>table</c> and <c>raw</c> (not deletable) and exactly one default.
/// Whether filter, sorts and property keys fit the collection schema is checked by the application layer.
/// </summary>
public sealed class View : ITenantEntity
{
    public const int NameMaxLength = 100;

    private View(
        Guid id,
        Guid workspaceId,
        Guid collectionId,
        string name,
        bool isDefault,
        bool isSystem,
        int sortOrder,
        Guid createdBy,
        DateTimeOffset createdAt)
    {
        Id = id;
        WorkspaceId = workspaceId;
        CollectionId = collectionId;
        Name = name;
        IsDefault = isDefault;
        IsSystem = isSystem;
        SortOrder = sortOrder;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        Sorts = [];
        VisibleProperties = [];
    }

    // For EF Core materialization.
    private View()
    {
        Name = string.Empty;
        Sorts = [];
        VisibleProperties = [];
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid CollectionId { get; private set; }

    public string Name { get; private set; }

    public ViewType Type { get; private set; }

    /// <summary>Filter DSL; null = all records.</summary>
    public ViewFilterNode? Filter { get; private set; }

    public IReadOnlyList<ViewSort> Sorts { get; private set; }

    /// <summary>Board columns / grouping: a select or status property. Required for boards.</summary>
    public string? GroupByProperty { get; private set; }

    /// <summary>Keys of the properties shown; empty = all.</summary>
    public IReadOnlyList<string> VisibleProperties { get; private set; }

    /// <summary>Gallery: property whose value is the card cover.</summary>
    public string? CardCoverProperty { get; private set; }

    /// <summary>Opened first; exactly one view per collection.</summary>
    public bool IsDefault { get; private set; }

    /// <summary>Standard views (<c>table</c>, <c>raw</c>) cannot be deleted.</summary>
    public bool IsSystem { get; private set; }

    public int SortOrder { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsDeletable => !IsSystem;

    /// <summary>A board lost its grouping property (the property was removed from the schema): it cannot render until reconfigured.</summary>
    public bool NeedsAttention => Type == ViewType.Board && GroupByProperty is null;

    public static View Create(
        Guid workspaceId,
        Guid collectionId,
        string name,
        ViewType type,
        ViewFilterNode? filter,
        IEnumerable<ViewSort>? sorts,
        string? groupByProperty,
        IEnumerable<string>? visibleProperties,
        string? cardCoverProperty,
        bool isDefault,
        bool isSystem,
        int sortOrder,
        Guid createdBy,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must be set.", nameof(workspaceId));
        }

        if (collectionId == Guid.Empty)
        {
            throw new ArgumentException("Collection id must be set.", nameof(collectionId));
        }

        if (createdBy == Guid.Empty)
        {
            throw new ArgumentException("Creator must be set.", nameof(createdBy));
        }

        var createdAt = timeProvider.GetUtcNow();
        var view = new View(
            Guid.CreateVersion7(createdAt), workspaceId, collectionId, GuardText.Required(name, NameMaxLength, nameof(name)), isDefault, isSystem, sortOrder, createdBy, createdAt);
        view.Configure(type, filter, sorts, groupByProperty, visibleProperties, cardCoverProperty);
        return view;
    }

    public void Rename(string name) => Name = GuardText.Required(name, NameMaxLength, nameof(name));

    /// <summary>Sets type and presentation settings together; a board needs a grouping property, an allowed type is required.</summary>
    public void Configure(
        ViewType type,
        ViewFilterNode? filter,
        IEnumerable<ViewSort>? sorts,
        string? groupByProperty,
        IEnumerable<string>? visibleProperties,
        string? cardCoverProperty)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown view type.");
        }

        var groupBy = string.IsNullOrWhiteSpace(groupByProperty) ? null : groupByProperty.Trim();
        if (type == ViewType.Board && groupBy is null)
        {
            throw new ArgumentException("A board view needs a grouping property.", nameof(groupByProperty));
        }

        Type = type;
        Filter = filter is null || filter.IsEmpty ? null : filter;
        Sorts = sorts is null ? [] : [.. sorts];
        GroupByProperty = groupBy;
        VisibleProperties = visibleProperties is null ? [] : [.. visibleProperties];
        CardCoverProperty = string.IsNullOrWhiteSpace(cardCoverProperty) ? null : cardCoverProperty.Trim();
    }

    public void MoveTo(int sortOrder) => SortOrder = sortOrder;

    public void MarkDefault(bool isDefault) => IsDefault = isDefault;

    /// <summary>
    /// Drops references to properties that no longer exist (or no longer fit): visible properties, sorts, filter conditions
    /// (groups left empty vanish), cover. A lost grouping property becomes null; the view type stays and
    /// <see cref="NeedsAttention"/> reports a board that cannot render. Returns true when anything changed.
    /// </summary>
    /// <param name="isGone">true for keys that must no longer be referenced.</param>
    /// <param name="isInvalidGroupBy">true when a kept key can no longer group (type changed away from select/status).</param>
    /// <param name="isUnsortable">true when a kept key can no longer be sorted (e.g. select → multi_select): its sorts go.</param>
    /// <remarks>
    /// Removing a condition below <c>not</c> makes the filter narrower, not wider: <c>not (a or b)</c> without <c>a</c> is <c>not b</c>,
    /// which excludes more records. A view never shows records it would not have shown under its stated intent on the remaining keys.
    /// </remarks>
    public bool RemoveReferences(Func<string, bool> isGone, Func<string, bool>? isInvalidGroupBy = null, Func<string, bool>? isUnsortable = null)
    {
        ArgumentNullException.ThrowIfNull(isGone);
        var changed = false;

        var visible = VisibleProperties.Where(k => !isGone(k)).ToList();
        if (visible.Count != VisibleProperties.Count)
        {
            // A list emptied this way means "all properties", like any empty list.
            VisibleProperties = visible;
            changed = true;
        }

        var sorts = Sorts.Where(s => !isGone(s.Prop) && isUnsortable?.Invoke(s.Prop) != true).ToList();
        if (sorts.Count != Sorts.Count)
        {
            Sorts = sorts;
            changed = true;
        }

        changed |= RewriteConditions(condition => isGone(condition.Prop!) ? null : condition);

        if (GroupByProperty is { } groupBy && (isGone(groupBy) || isInvalidGroupBy?.Invoke(groupBy) == true))
        {
            GroupByProperty = null;
            changed = true;
        }

        if (CardCoverProperty is { } cover && isGone(cover))
        {
            CardCoverProperty = null;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Rewrites every filter condition with <paramref name="rewrite"/> (return the same instance to keep it, a new node to replace it,
    /// null to remove it); groups left empty vanish. Returns true when the filter changed.
    /// </summary>
    public bool RewriteConditions(Func<ViewFilterNode, ViewFilterNode?> rewrite)
    {
        ArgumentNullException.ThrowIfNull(rewrite);
        var filter = PruneFilter(Filter, rewrite);
        if (ReferenceEquals(filter, Filter))
        {
            return false;
        }

        Filter = filter;
        return true;
    }

    /// <summary>The rewritten node; the same instance when nothing changed, null when nothing is left.</summary>
    private static ViewFilterNode? PruneFilter(ViewFilterNode? node, Func<ViewFilterNode, ViewFilterNode?> rewrite)
    {
        if (node is null)
        {
            return null;
        }

        if (node.Prop is not null)
        {
            return rewrite(node);
        }

        if (node.Not is { } negated)
        {
            // A negation whose condition is gone goes too (as an emptied group does).
            var child = PruneFilter(negated, rewrite);
            return ReferenceEquals(child, negated) ? node : child is null ? null : node with { Not = child };
        }

        var (children, isAnd) = node.And is not null ? (node.And, true) : (node.Or ?? [], false);
        var kept = new List<ViewFilterNode>();
        var changed = false;
        foreach (var child in children)
        {
            var pruned = PruneFilter(child, rewrite);
            changed |= !ReferenceEquals(pruned, child);
            if (pruned is not null)
            {
                kept.Add(pruned);
            }
        }

        if (!changed)
        {
            return node;
        }

        if (kept.Count == 0)
        {
            return null;
        }

        return isAnd ? node with { And = kept } : node with { Or = kept };
    }
}
