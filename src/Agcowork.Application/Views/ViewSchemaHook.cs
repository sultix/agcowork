using System.Text.Json;
using Agcowork.Application.Collections;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Query;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Views;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Views;

/// <summary>
/// Keeps views in line with a changed schema: removed properties disappear from visible properties, sorts, filter conditions
/// (emptied groups vanish) and the cover; a lost <c>groupBy</c> becomes null (the view keeps its type, boards report
/// <c>needsAttention</c>). A <c>groupBy</c> whose property is no longer select/status is treated the same way; sorts on a property
/// that can no longer be sorted (select → multi_select) are removed.
/// </summary>
public sealed class ViewSchemaHook(IAgcoworkDbContext db) : ISchemaChangedHook
{
    public async Task OnSchemaChangedAsync(Collection collection, SchemaChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(change);

        if (change.RemovedKeys.Count == 0 && change.TypeChanges.Count == 0 && change.RemovedOptions.Count == 0)
        {
            return;
        }

        var cannotGroup = change.TypeChanges.Keys
            .Where(key => change.After.Find(key) is { Type: not (PropertyType.Select or PropertyType.Status) })
            .ToHashSet(StringComparer.Ordinal);
        var cannotSort = change.TypeChanges.Keys
            .Where(key => change.After.Find(key) is { } property && !OperatorMatrix.IsSortable(OperatorMatrix.ValueTypeOf(property.Type)))
            .ToHashSet(StringComparer.Ordinal);
        var views = await db.Views.Where(v => v.CollectionId == collection.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var affected = views.Count(view =>
            view.RemoveReferences(change.RemovedKeys.Contains, cannotGroup.Contains, cannotSort.Contains)
            | view.RewriteConditions(condition => AdjustCondition(change, condition)));
        if (affected > 0)
        {
            change.Effects["affectedViews"] = affected;
        }
    }

    /// <summary>
    /// Keeps a condition meaningful after option or type changes: lost option values leave in/nin lists (an emptied list removes the
    /// condition) and eq/neq/contains conditions on a lost value are removed; select → multi_select turns eq into contains and drops neq.
    /// Removing a condition below <c>not</c> narrows the filter (see <see cref="View.RemoveReferences"/>).
    /// </summary>
    private static ViewFilterNode? AdjustCondition(SchemaChange change, ViewFilterNode condition)
    {
        var prop = condition.Prop!;
        var node = condition;
        if (change.RemovedOptions.TryGetValue(prop, out var lost))
        {
            switch (node.Op)
            {
                case ViewFilterOperators.In or ViewFilterOperators.Nin when node.Value is { ValueKind: JsonValueKind.Array } list:
                    var kept = list.EnumerateArray()
                        .Where(item => item.ValueKind != JsonValueKind.String || !lost.Contains(item.GetString()!, StringComparer.Ordinal))
                        .ToList();
                    if (kept.Count == 0)
                    {
                        return null;
                    }

                    if (kept.Count != list.GetArrayLength())
                    {
                        node = node with { Value = JsonSerializer.SerializeToElement(kept) };
                    }

                    break;
                case ViewFilterOperators.Eq or ViewFilterOperators.Neq or ViewFilterOperators.Contains when node.Value is { ValueKind: JsonValueKind.String } single
                    && lost.Contains(single.GetString()!, StringComparer.Ordinal):
                    return null;
            }
        }

        if (change.TypeChanges.TryGetValue(prop, out var typeChange) && typeChange is { From: PropertyType.Select, To: PropertyType.MultiSelect })
        {
            if (node.Op == ViewFilterOperators.Neq)
            {
                return null;
            }

            if (node.Op == ViewFilterOperators.Eq)
            {
                node = node with { Op = ViewFilterOperators.Contains };
            }
        }

        return node;
    }
}
