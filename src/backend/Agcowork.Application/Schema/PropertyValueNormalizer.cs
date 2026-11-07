using System.Text.Json;
using Agcowork.Domain.Collections.Schema;

namespace Agcowork.Application.Schema;

/// <summary>
/// Canonical form of property values, applied before <see cref="PropertyValueValidator"/> and before storing:
/// trims text-like values (not Markdown), converts date-times to UTC (<c>2026-11-07T13:30:00Z</c>), writes ids in
/// lowercase <c>D</c> format and drops duplicates in arrays. Select values stay case-sensitive, exactly as the
/// options define them. Values of the wrong shape and unknown keys pass through unchanged for the validator to report.
/// </summary>
public static class PropertyValueNormalizer
{
    public static Dictionary<string, JsonElement> Normalize(IReadOnlyList<PropertyDefinition> schema, IReadOnlyDictionary<string, JsonElement> properties)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(properties);

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in properties)
        {
            var property = schema.Find(key);
            result[key] = property is null || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? value
                : PropertyTypeRegistry.Get(property.Type).Normalize(property, value);
        }

        return result;
    }

    /// <summary>
    /// Adds the <see cref="PropertyDefinition.Default"/> of every property missing in <paramref name="properties"/>.
    /// For new records, before validation (a required property with a default is then present).
    /// </summary>
    public static Dictionary<string, JsonElement> ApplyDefaults(IReadOnlyList<PropertyDefinition> schema, IReadOnlyDictionary<string, JsonElement> properties)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(properties);

        var result = new Dictionary<string, JsonElement>(properties, StringComparer.Ordinal);
        foreach (var property in schema)
        {
            if (property.Default is { } value && !result.ContainsKey(property.Key))
            {
                result[property.Key] = value;
            }
        }

        return result;
    }
}
