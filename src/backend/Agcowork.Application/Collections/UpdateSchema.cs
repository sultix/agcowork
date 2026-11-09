using System.Text.Json;
using Agcowork.Application.Activity;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections;

/// <summary>Changes to a collection schema, applied together or not at all. At least one of add, update, remove.</summary>
/// <param name="CollectionId">Collection to change.</param>
/// <param name="Add">New properties, appended in this order. Keys must not exist yet.</param>
/// <param name="Update">Changes to existing properties, by key; omitted fields stay unchanged.</param>
/// <param name="Remove">Keys of properties to remove (not <c>title</c>). Requires <paramref name="ConfirmRemoval"/>.</param>
/// <param name="ConfirmRemoval">
/// Must be true for changes that lose stored values: removing properties, removing option values, lowering
/// <c>precision</c> below the decimal places of stored values, markdown → text.
/// </param>
/// <param name="ExpectedSchemaVersion">If set, the change is applied only when the schema is still at this version (409 otherwise).</param>
public sealed record UpdateSchemaRequest(
    Guid CollectionId,
    IReadOnlyList<PropertyDefinitionInput>? Add = null,
    IReadOnlyList<PropertyChangeInput>? Update = null,
    IReadOnlyList<string>? Remove = null,
    bool ConfirmRemoval = false,
    int? ExpectedSchemaVersion = null);

/// <summary>Partial change of one existing property. Null leaves a field unchanged.</summary>
/// <param name="Key">Key of the property to change. Keys are immutable.</param>
/// <param name="NewKey">Not supported: keys cannot be renamed. Add a new property and remove the old one instead.</param>
/// <param name="Name">New display name.</param>
/// <param name="Type">New type; only text ↔ markdown and select → multi_select are allowed.</param>
/// <param name="Description">New description; an empty string clears it.</param>
/// <param name="Required">Whether a value is required.</param>
/// <param name="Options">New complete list of options (replaces the old list).</param>
/// <param name="Default">New default value.</param>
/// <param name="ClearDefault">true removes the default.</param>
/// <param name="Unit">number: new unit; an empty string clears it.</param>
/// <param name="Precision">number: new maximum decimal places.</param>
/// <param name="ClearPrecision">number: true removes the precision limit (like clearDefault); send it instead of precision, not together with it.</param>
/// <param name="Multiple">relation: true allows several references (cannot be undone).</param>
/// <param name="TargetCollectionId">relation: cannot change; listed so a change attempt is reported instead of ignored.</param>
public sealed record PropertyChangeInput(
    string? Key,
    string? NewKey = null,
    string? Name = null,
    string? Type = null,
    string? Description = null,
    bool? Required = null,
    IReadOnlyList<PropertyOptionInput>? Options = null,
    JsonElement? Default = null,
    bool? ClearDefault = null,
    string? Unit = null,
    int? Precision = null,
    bool? Multiple = null,
    Guid? TargetCollectionId = null,
    bool? ClearPrecision = null);

/// <summary>
/// Changes the schema of a collection (additive changes preferred) and increments its version. Requires
/// <c>schema</c>. Keys are immutable; types change only along text ↔ markdown and select → multi_select;
/// changes that lose values need <c>confirmRemoval: true</c>. A request without an actual change keeps the version.
/// Every <see cref="ISchemaChangedHook"/> runs before the save (template presets, record values, later views), inside one
/// transaction with the save and the activity event <c>collection.schema_changed</c>.
/// </summary>
public sealed class UpdateSchema(IAgcoworkDbContext db, ITenantContext tenant, IEnumerable<ISchemaChangedHook> hooks, IActivityRecorder activity)
    : IUseCase<UpdateSchemaRequest, CollectionSchemaDto>
{
    public async Task<Result<CollectionSchemaDto>> HandleAsync(UpdateSchemaRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireWritable() is { } archived)
        {
            return archived;
        }

        if (tenant.RequireScope(WorkspaceScope.Schema) is { } denied)
        {
            return denied;
        }

        var adds = request.Add ?? [];
        var updates = request.Update ?? [];
        var removes = request.Remove ?? [];

        var errors = new FieldErrors();
        if (adds.Count == 0 && updates.Count == 0 && removes.Count == 0)
        {
            errors.Add("add", ErrorCodes.Required, "Nothing to change: send at least one of 'add', 'update' or 'remove'.");
            return errors.ToError();
        }

        var parsedAdds = SchemaInput.ParseList(errors, "add", adds);
        ValidateUpdateShape(errors, updates);
        for (var i = 0; i < removes.Count; i++)
        {
            errors.Required($"remove[{i}]", removes[i]);
        }

        if (errors.Any)
        {
            return errors.ToError();
        }

        // Exclusive collection lock first (order: collection → record): record writes wait until the new schema and the
        // cleaned values are committed, and a concurrent schema change waits and then sees this one's version.
        await using var transaction = await db.BeginOrJoinTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.LockCollectionAsync(request.CollectionId, cancellationToken).ConfigureAwait(false);
        var collection = await db.Collections.SingleOrDefaultAsync(c => c.Id == request.CollectionId, cancellationToken).ConfigureAwait(false);
        if (collection is null)
        {
            return new NotFoundError("collection");
        }

        if (collection.IsArchived)
        {
            return CollectionConflicts.Archived;
        }

        if (request.ExpectedSchemaVersion is { } expected && expected != collection.SchemaVersion)
        {
            return CollectionConflicts.SchemaVersion(collection.SchemaVersion);
        }

        var current = collection.Schema;
        var losses = new List<string>();
        var removed = CheckRemovals(errors, current, removes, losses);
        var changed = ApplyUpdates(errors, current, updates, removed);
        foreach (var (key, after) in changed)
        {
            losses.AddRange(Losses(current.Find(key)!, after));
        }

        losses.AddRange(await PrecisionLossesAsync(collection.Id, current, changed, cancellationToken).ConfigureAwait(false));

        var added = CheckAdds(errors, current, parsedAdds, removed);
        if (losses.Count > 0 && !request.ConfirmRemoval)
        {
            errors.Add(
                "confirmRemoval",
                ErrorCodes.RemovalNotConfirmed,
                $"This change loses stored values on records: {string.Join("; ", losses)}. Send the same request with confirmRemoval: true to proceed.");
        }

        var next = current
            .Where(p => !removed.Contains(p.Key))
            .Select(p => changed.TryGetValue(p.Key, out var after) ? after : p)
            .Concat(added)
            .ToList();
        if (next.Count > CollectionSchema.MaxProperties)
        {
            errors.Add("add", ErrorCodes.TooLong, $"A collection has at most {CollectionSchema.MaxProperties} properties.");
        }

        await CollectionInputValidation.CheckRelationTargetsAsync(
            db, errors, [.. added.Select((d, i) => ($"add[{i}].targetCollectionId", d))], cancellationToken).ConfigureAwait(false);
        if (errors.Any)
        {
            return errors.ToError();
        }

        if (removed.Count == 0 && changed.Count == 0 && added.Count == 0)
        {
            // Only no-op updates: nothing to store, the version stays.
            return CollectionSchemaDto.From(collection);
        }

        var versionBefore = collection.SchemaVersion;
        collection.ReplaceSchema(next);
        var change = SchemaChange.Between(current, collection.Schema);

        // Hooks may update other rows directly (record values); they commit together with the schema or not at all.
        foreach (var hook in hooks)
        {
            await hook.OnSchemaChangedAsync(collection, change, cancellationToken).ConfigureAwait(false);
        }

        var diff = new ActivityDiff()
            .Change("schemaVersion", versionBefore, collection.SchemaVersion)
            .Set("added", ActivityDiff.Strings(added.Select(p => p.Key)))
            .Set("changed", ActivityDiff.Strings(changed.Keys.Order(StringComparer.Ordinal)))
            .Set("removed", ActivityDiff.Strings(removed.Order(StringComparer.Ordinal)));
        foreach (var (key, value) in change.Effects)
        {
            diff.Set(key, value);
        }

        activity.Record(ActivityTypes.CollectionSchemaChanged, ActivityTargets.Collection, collection.Id, collection.SpaceId, collection.Id, diff.ToJson());

        if (await CollectionSave.SaveAsync(db, collection.Id, "expectedSchemaVersion", cancellationToken).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return CollectionSchemaDto.From(collection);
    }

    private static void ValidateUpdateShape(FieldErrors errors, IReadOnlyList<PropertyChangeInput?> updates)
    {
        for (var i = 0; i < updates.Count; i++)
        {
            var change = updates[i];
            if (change is null)
            {
                errors.Add($"update[{i}]", ErrorCodes.Required, "Each entry needs at least 'key'.");
                continue;
            }

            errors.Required($"update[{i}].key", change.Key);
            if (change.ClearPrecision == true && change.Precision is not null)
            {
                errors.Add($"update[{i}].clearPrecision", ErrorCodes.Conflict, "'clearPrecision: true' removes the precision limit and cannot be combined with 'precision'. Send only one of them.");
            }

            if (change.NewKey is not null && !string.Equals(change.NewKey, change.Key, StringComparison.Ordinal))
            {
                errors.Add(
                    $"update[{i}].newKey",
                    ErrorCodes.KeyImmutable,
                    "Keys cannot be renamed: records, views and filters refer to them. Add a new property under the new key and remove the old one (confirmRemoval: true).");
            }
        }
    }

    private static HashSet<string> CheckRemovals(FieldErrors errors, IReadOnlyList<PropertyDefinition> current, IReadOnlyList<string> removes, List<string> losses)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < removes.Count; i++)
        {
            var key = removes[i];
            if (string.Equals(key, CollectionSchema.TitleKey, StringComparison.Ordinal))
            {
                errors.Add($"remove[{i}]", ErrorCodes.NotAllowed, "The title property cannot be removed.");
            }
            else if (current.Find(key) is null)
            {
                errors.Add($"remove[{i}]", ErrorCodes.NotFound, $"The collection has no property '{key}'.", [.. current.Select(p => p.Key)]);
            }
            else if (removed.Add(key))
            {
                losses.Add($"property '{key}' removed");
            }
        }

        return removed;
    }

    /// <summary>Changes of a kept property that lose stored values: removed options, lower precision, markdown → text.</summary>
    private static IEnumerable<string> Losses(PropertyDefinition before, PropertyDefinition after)
    {
        if (SchemaChange.LostOptions(before, after) is [_, ..] lost)
        {
            yield return $"'{before.Key}': option values {string.Join(", ", lost.Select(v => $"'{v}'"))} removed";
        }

        if (before.Type == PropertyType.Markdown && after.Type == PropertyType.Text)
        {
            yield return $"'{before.Key}': markdown → text (max. {PropertyTypeRegistry.TextMaxLength} characters, formatting becomes plain text)";
        }
    }

    /// <summary>
    /// Lowered (or first set) precisions that would round stored values: only numbers with more decimal places than the new precision
    /// count, in records and in template presets. Reads the collection's records under the exclusive collection lock, like the hook that rounds them.
    /// </summary>
    private async Task<List<string>> PrecisionLossesAsync(
        Guid collectionId,
        IReadOnlyList<PropertyDefinition> current,
        Dictionary<string, PropertyDefinition> changed,
        CancellationToken cancellationToken)
    {
        var lowered = changed
            .Where(c => c.Value.Type == PropertyType.Number && c.Value.Precision is not null
                && current.Find(c.Key) is { } before && (before.Precision is null || c.Value.Precision < before.Precision))
            .ToDictionary(c => c.Key, c => c.Value.Precision!.Value, StringComparer.Ordinal);
        var losses = new List<string>();
        if (lowered.Count == 0)
        {
            return losses;
        }

        var stored = await db.Records.AsNoTracking()
            .Where(r => r.CollectionId == collectionId)
            .Select(r => r.Properties)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // Template presets are stored values too: the hook rounds them, so they count like record values.
        var templates = await db.Templates.AsNoTracking()
            .Where(t => t.CollectionId == collectionId)
            .OrderBy(t => t.Name)
            .Select(t => new { t.Name, t.PropertiesPreset })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var (key, precision) in lowered.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            var records = stored.Count(properties => NeedsRounding(properties, key, precision));
            var verb = current.Find(key)!.Precision is null ? "set to" : "lowered to";
            if (records > 0)
            {
                losses.Add($"'{key}': precision {verb} {precision} decimal places, {records} record{(records == 1 ? " has a value" : "s have values")} with more decimal places that would be rounded");
            }

            foreach (var template in templates.Where(t => NeedsRounding(t.PropertiesPreset, key, precision)))
            {
                losses.Add($"template '{template.Name}': preset '{key}' would be rounded to {precision} decimal places");
            }
        }

        return losses;
    }

    private static bool NeedsRounding(IReadOnlyDictionary<string, JsonElement> values, string key, int precision) =>
        values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var number) && decimal.Round(number, precision) != number;

    private static Dictionary<string, PropertyDefinition> ApplyUpdates(
        FieldErrors errors,
        IReadOnlyList<PropertyDefinition> current,
        IReadOnlyList<PropertyChangeInput> updates,
        HashSet<string> removed)
    {
        var changed = new Dictionary<string, PropertyDefinition>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < updates.Count; i++)
        {
            var change = updates[i];
            var field = $"update[{i}]";
            var before = current.Find(change.Key!);
            if (before is null)
            {
                errors.Add($"{field}.key", ErrorCodes.NotFound, $"The collection has no property '{change.Key}'. Use 'add' for new properties.", [.. current.Select(p => p.Key)]);
                continue;
            }

            if (removed.Contains(before.Key))
            {
                errors.Add($"{field}.key", ErrorCodes.NotAllowed, $"'{before.Key}' is both updated and removed in this request.");
                continue;
            }

            if (!seen.Add(before.Key))
            {
                errors.Add($"{field}.key", ErrorCodes.Duplicate, $"'{before.Key}' is updated more than once; combine the changes into one entry.");
                continue;
            }

            // Entries that change nothing are dropped, so they do not bump the version.
            if (Apply(errors, field, before, change) is { } after && !SchemaInput.SameDefinition(before, after))
            {
                changed[before.Key] = after;
            }
        }

        return changed;
    }

    private static PropertyDefinition? Apply(FieldErrors errors, string field, PropertyDefinition before, PropertyChangeInput change)
    {
        var count = errors.Items.Count;
        var type = change.Type is null ? before.Type : SchemaInput.ParseType(errors, $"{field}.type", change.Type);
        var options = change.Options is null ? before.Options : SchemaInput.ParseOptions(errors, $"{field}.options", change.Options);
        SchemaInput.CheckStorable(errors, field, change.Name, change.Description, change.Unit, change.Default);
        if (type is null || errors.Items.Count != count)
        {
            return null;
        }

        var defaultValue = change.ClearDefault == true
            ? null
            : change.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } newDefault ? newDefault : before.Default;
        if (before.Type == PropertyType.Select && type == PropertyType.MultiSelect && defaultValue is { ValueKind: JsonValueKind.String } single)
        {
            defaultValue = JsonSerializer.SerializeToElement(new[] { single });
        }

        var after = before with
        {
            Name = change.Name?.Trim() ?? before.Name,
            Type = type.Value,
            Description = change.Description is null ? before.Description : SchemaInput.Optional(change.Description),
            Required = change.Required ?? before.Required,
            Options = options,
            TargetCollectionId = change.TargetCollectionId ?? before.TargetCollectionId,
            Multiple = change.Multiple ?? before.Multiple,
            Default = defaultValue,
            Unit = change.Unit is null ? before.Unit : SchemaInput.Optional(change.Unit),
            Precision = change.ClearPrecision == true ? null : change.Precision ?? before.Precision,
        };

        SchemaInput.AddViolations(errors, field, CollectionSchema.ValidateChange(before, after));
        SchemaInput.AddViolations(errors, field, after.Validate());
        if (errors.Items.Count != count)
        {
            return null;
        }

        after = SchemaInput.WithNormalizedDefault(after);
        SchemaInput.CheckDefault(errors, $"{field}.default", after);
        return errors.Items.Count == count ? after : null;
    }

    private static List<PropertyDefinition> CheckAdds(
        FieldErrors errors,
        IReadOnlyList<PropertyDefinition> current,
        List<PropertyDefinition?> parsedAdds,
        HashSet<string> removed)
    {
        var added = new List<PropertyDefinition>();
        for (var i = 0; i < parsedAdds.Count; i++)
        {
            var property = parsedAdds[i]!;
            if (removed.Contains(property.Key))
            {
                // Removing and re-adding in one step would be a disguised type change; the old values must be gone first.
                errors.Add(
                    $"add[{i}].key",
                    ErrorCodes.Duplicate,
                    $"'{property.Key}' is removed in this request. Re-adding the key with a new definition needs a separate request after the removal.");
                continue;
            }

            if (current.Find(property.Key) is not null)
            {
                errors.Add($"add[{i}].key", ErrorCodes.Duplicate, $"The collection already has a property '{property.Key}'. Use 'update' to change it.");
                continue;
            }

            added.Add(property);
        }

        return added;
    }
}
