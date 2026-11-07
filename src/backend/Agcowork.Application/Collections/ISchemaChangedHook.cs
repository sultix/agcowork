using System.Text.Json.Nodes;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;

namespace Agcowork.Application.Collections;

/// <summary>
/// Extension point "the schema of a collection changed": clean up data that depends on it (template presets here,
/// record values in AP-007, views in AP-008). Every registered hook runs inside <see cref="UpdateSchema"/> after the new
/// schema is applied and before it is saved, so whatever a hook changes is stored in the same SaveChanges, or not at all.
/// </summary>
/// <remarks>Hooks run in the caller's tenant; they must not save themselves.</remarks>
public interface ISchemaChangedHook
{
    Task OnSchemaChangedAsync(Collection collection, SchemaChange change, CancellationToken cancellationToken);
}

/// <summary>Type change of a kept property.</summary>
public sealed record PropertyTypeChange(PropertyType From, PropertyType To);

/// <summary>What a schema update changed, for <see cref="ISchemaChangedHook"/>s.</summary>
/// <param name="Before">Schema before the change.</param>
/// <param name="After">Schema after the change.</param>
/// <param name="RemovedKeys">Keys that no longer exist; their values are lost.</param>
/// <param name="TypeChanges">Kept keys whose type changed (text ↔ markdown, select → multi_select).</param>
/// <param name="RemovedOptions">Kept keys and the option values they lost.</param>
public sealed record SchemaChange(
    IReadOnlyList<PropertyDefinition> Before,
    IReadOnlyList<PropertyDefinition> After,
    IReadOnlySet<string> RemovedKeys,
    IReadOnlyDictionary<string, PropertyTypeChange> TypeChanges,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RemovedOptions)
{
    /// <summary>
    /// What the hooks did, for the activity event <c>collection.schema_changed</c>: hooks add members here (e.g.
    /// <c>affectedRecords</c>), <see cref="UpdateSchema"/> merges them into the event's diff.
    /// </summary>
    public JsonObject Effects { get; } = [];

    public static SchemaChange Between(IReadOnlyList<PropertyDefinition> before, IReadOnlyList<PropertyDefinition> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var removed = new HashSet<string>(StringComparer.Ordinal);
        var typeChanges = new Dictionary<string, PropertyTypeChange>(StringComparer.Ordinal);
        var removedOptions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var old in before)
        {
            if (after.Find(old.Key) is not { } now)
            {
                removed.Add(old.Key);
                continue;
            }

            if (old.Type != now.Type)
            {
                typeChanges[old.Key] = new PropertyTypeChange(old.Type, now.Type);
            }

            if (LostOptions(old, now) is [_, ..] lost)
            {
                removedOptions[old.Key] = lost;
            }
        }

        return new SchemaChange(before, after, removed, typeChanges, removedOptions);
    }

    /// <summary>Option values of <paramref name="before"/> that <paramref name="after"/> no longer has.</summary>
    public static IReadOnlyList<string> LostOptions(PropertyDefinition before, PropertyDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var kept = (after.Options ?? []).Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
        return [.. (before.Options ?? []).Select(o => o.Value).Where(v => !kept.Contains(v))];
    }
}
