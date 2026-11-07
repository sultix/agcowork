using System.Text.Json;
using System.Text.Json.Serialization;
using Agcowork.Domain.Collections.Schema;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Agcowork.Infrastructure.Persistence;

/// <summary>
/// Storage format of JSONB columns with domain types. Stable and explicit: camelCase members, snake_case enum
/// names (<c>multi_select</c>, <c>in_progress</c>), nulls omitted, no type discriminators. The schema is wrapped
/// in a versioned envelope (<c>{"version":1,"properties":[…]}</c>) so the format can evolve with a migration path.
/// </summary>
internal static class JsonColumns
{
    public const int SchemaFormatVersion = 1;

    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    public static ValueConverter<IReadOnlyList<PropertyDefinition>, string> SchemaConverter { get; } =
        new(schema => SerializeSchema(schema), json => DeserializeSchema(json));

    public static ValueComparer<IReadOnlyList<PropertyDefinition>> SchemaComparer { get; } =
        new(
            (a, b) => SerializeSchema(a!) == SerializeSchema(b!),
            schema => StringComparer.Ordinal.GetHashCode(SerializeSchema(schema)),
            schema => DeserializeSchema(SerializeSchema(schema)));

    public static ValueConverter<IReadOnlyDictionary<string, JsonElement>, string> PresetConverter { get; } =
        new(preset => SerializePreset(preset), json => DeserializePreset(json));

    public static ValueComparer<IReadOnlyDictionary<string, JsonElement>> PresetComparer { get; } =
        new(
            (a, b) => SerializePreset(a!) == SerializePreset(b!),
            preset => StringComparer.Ordinal.GetHashCode(SerializePreset(preset)),
            preset => DeserializePreset(SerializePreset(preset)));

    public static string SerializeSchema(IReadOnlyList<PropertyDefinition> schema) =>
        JsonSerializer.Serialize(new SchemaDocument(SchemaFormatVersion, [.. schema]), Options);

    public static IReadOnlyList<PropertyDefinition> DeserializeSchema(string json)
    {
        var document = JsonSerializer.Deserialize<SchemaDocument>(json, Options)
            ?? throw new InvalidOperationException("Stored schema is null.");
        if (document.Version != SchemaFormatVersion)
        {
            throw new InvalidOperationException($"Unsupported schema format version {document.Version}.");
        }

        return document.Properties;
    }

    // Keys sorted, so equal presets serialize equally (change detection compares the JSON).
    public static string SerializePreset(IReadOnlyDictionary<string, JsonElement> preset) =>
        JsonSerializer.Serialize(new SortedDictionary<string, JsonElement>(preset.ToDictionary(), StringComparer.Ordinal), Options);

    public static IReadOnlyDictionary<string, JsonElement> DeserializePreset(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Options) ?? [];

    private sealed record SchemaDocument(int Version, List<PropertyDefinition> Properties);
}
