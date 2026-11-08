using System.Text.Json;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections.Templates;

/// <summary>
/// Keeps template presets in line with a changed schema: drops removed keys, wraps values of select → multi_select and of a
/// relation becoming <c>multiple</c> (single id → array with that id, like the record values), drops removed option values
/// from multi_select presets and drops every remaining value the new schema rejects (removed option, lowered precision,
/// markdown → text over length).
/// </summary>
public sealed class TemplatePresetSchemaHook(IAgcoworkDbContext db) : ISchemaChangedHook
{
    public async Task OnSchemaChangedAsync(Collection collection, SchemaChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(change);

        var templates = await db.Templates.Where(t => t.CollectionId == collection.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var template in templates)
        {
            var preset = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (key, value) in template.PropertiesPreset)
            {
                if (Adjust(change, key, value) is { } adjusted)
                {
                    preset[key] = adjusted;
                }
            }

            if (preset.Count != template.PropertiesPreset.Count
                || preset.Any(p => p.Value.GetRawText() != template.PropertiesPreset[p.Key].GetRawText()))
            {
                template.ChangePreset(preset);
            }
        }
    }

    private static JsonElement? Adjust(SchemaChange change, string key, JsonElement value)
    {
        if (change.After.Find(key) is not { } property)
        {
            return null;
        }

        if (((change.TypeChanges.TryGetValue(key, out var typeChange) && typeChange is { From: PropertyType.Select, To: PropertyType.MultiSelect })
                || change.RelationBecomesMultiple(key))
            && value.ValueKind == JsonValueKind.String)
        {
            value = JsonSerializer.SerializeToElement(new[] { value });
        }

        if (property.Type == PropertyType.MultiSelect && value.ValueKind == JsonValueKind.Array && change.RemovedOptions.TryGetValue(key, out var lost))
        {
            value = JsonSerializer.SerializeToElement(value.EnumerateArray()
                .Where(item => item.ValueKind != JsonValueKind.String || !lost.Contains(item.GetString()!, StringComparer.Ordinal))
                .ToList());
        }

        var errors = new FieldErrors();
        PropertyTypeRegistry.CheckValue(property, value, key, errors);
        return errors.Any ? null : value;
    }
}
