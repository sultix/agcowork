using System.Text.Json;
using System.Text.Json.Nodes;
using Agcowork.Application.Activity;
using Agcowork.Application.Collections;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Records;

/// <summary>
/// Keeps stored record values in line with a changed schema (the destructive changes confirmed with
/// <c>confirmRemoval</c>, plus the widenings): removed properties lose their values; select → multi_select and a relation
/// becoming <c>multiple</c> wrap the single value in an array (a stored null, which the normalizer never writes, is dropped
/// like any value the new schema rejects: no value either way);
/// removed options are dropped (select/status: the value, multi_select: the item); lowered <c>precision</c> rounds numbers
/// (midpoint away from zero); markdown → text cuts values to the text limit. A value the new schema still rejects is
/// dropped. Trimming is normalizer behaviour and not done here; nothing is composed from other values.
/// </summary>
/// <remarks>
/// Runs inside <see cref="UpdateSchema"/>'s transaction, under its exclusive collection lock: record writers hold a
/// shared lock on the collection, so no record of it changes while it is read and cleaned. Only changed records are
/// updated, one tenant-filtered <c>ExecuteUpdate</c> each (no <c>IgnoreQueryFilters</c>), in id order; a failing schema
/// save rolls the cleanup back too.
/// <para>
/// Documented exception to "every change creates a revision": the cleanup creates no revisions and does not change
/// revision numbers. It follows from the schema change, which is in the activity log as <c>collection.schema_changed</c>
/// with <c>affectedRecords</c> (number of cleaned records), <c>cleanedKeys</c> (keys whose values were changed or dropped)
/// and <c>removedOptions</c> (key → removed option values).
/// </para>
/// </remarks>
public sealed class RecordValuesSchemaHook(IAgcoworkDbContext db) : ISchemaChangedHook
{
    public async Task OnSchemaChangedAsync(Collection collection, SchemaChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(change);
        var affected = 0;
        var cleanedKeys = new SortedSet<string>(StringComparer.Ordinal);
        if (Affects(change))
        {
            var records = await db.Records.AsNoTracking()
                .Where(r => r.CollectionId == collection.Id)
                .OrderBy(r => r.Id)
                .Select(r => new { r.Id, r.Properties })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in records)
            {
                var adjusted = Adjust(change, record.Properties);
                if (adjusted is null)
                {
                    continue;
                }

                affected++;
                cleanedKeys.UnionWith(record.Properties.Keys.Where(key =>
                    !adjusted.TryGetValue(key, out var now) || now.GetRawText() != record.Properties[key].GetRawText()));
                IReadOnlyDictionary<string, JsonElement> properties = adjusted;
                await db.Records
                    .Where(r => r.Id == record.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.Properties, properties), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        change.Effects["affectedRecords"] = affected;
        change.Effects["cleanedKeys"] = ActivityDiff.Strings(cleanedKeys);
        var removedOptions = new JsonObject();
        foreach (var (key, values) in change.RemovedOptions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            removedOptions[key] = ActivityDiff.Strings(values);
        }

        change.Effects["removedOptions"] = removedOptions;
    }

    /// <summary>The first <paramref name="maxLength"/> characters, one fewer when that would split a surrogate pair.</summary>
    internal static string Cut(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength)];

    /// <summary>Whether stored values can be affected at all; most schema changes (additions, renames) cannot.</summary>
    private static bool Affects(SchemaChange change) =>
        change.RemovedKeys.Count > 0
        || change.TypeChanges.Count > 0
        || change.RemovedOptions.Count > 0
        || change.Before.Any(before => change.After.Find(before.Key) is { Type: PropertyType.Number, Precision: { } precision }
            && (before.Precision is null || precision < before.Precision))
        || change.Before.Any(before => change.RelationBecomesMultiple(before.Key));

    /// <summary>The adjusted values, or null when nothing changes.</summary>
    internal static Dictionary<string, JsonElement>? Adjust(SchemaChange change, IReadOnlyDictionary<string, JsonElement> properties)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var changed = false;
        foreach (var (key, value) in properties)
        {
            var adjusted = AdjustValue(change, key, value);
            if (adjusted is { } kept)
            {
                result[key] = kept;
                changed |= kept.GetRawText() != value.GetRawText();
            }
            else
            {
                changed = true;
            }
        }

        return changed ? result : null;
    }

    private static JsonElement? AdjustValue(SchemaChange change, string key, JsonElement value)
    {
        if (change.After.Find(key) is not { } property)
        {
            return change.Before.Find(key) is null ? value : null;
        }

        change.TypeChanges.TryGetValue(key, out var typeChange);
        if ((typeChange is { From: PropertyType.Select, To: PropertyType.MultiSelect } || change.RelationBecomesMultiple(key))
            && value.ValueKind == JsonValueKind.String)
        {
            value = JsonSerializer.SerializeToElement(new[] { value });
        }

        if (typeChange is { From: PropertyType.Markdown, To: PropertyType.Text }
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > PropertyTypeRegistry.TextMaxLength } text)
        {
            value = JsonSerializer.SerializeToElement(Cut(text, PropertyTypeRegistry.TextMaxLength));
        }

        if (change.RemovedOptions.TryGetValue(key, out var lost))
        {
            if (value.ValueKind == JsonValueKind.String && lost.Contains(value.GetString()!, StringComparer.Ordinal))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                value = JsonSerializer.SerializeToElement(value.EnumerateArray()
                    .Where(item => item.ValueKind != JsonValueKind.String || !lost.Contains(item.GetString()!, StringComparer.Ordinal))
                    .ToList());
            }
        }

        if (property is { Type: PropertyType.Number, Precision: { } precision } && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            var rounded = decimal.Round(number, precision, MidpointRounding.AwayFromZero);
            if (rounded != number)
            {
                value = JsonSerializer.SerializeToElement(rounded);
            }
        }

        var errors = new FieldErrors();
        PropertyTypeRegistry.CheckValue(property, value, key, errors);
        return errors.Any ? null : value;
    }
}
