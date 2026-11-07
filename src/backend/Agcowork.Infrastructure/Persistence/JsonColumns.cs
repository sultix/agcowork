using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Views;
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

    /// <summary>Property values of records and revisions: same format as template presets (keys sorted).</summary>
    public static ValueConverter<IReadOnlyDictionary<string, JsonElement>, string> PropertiesConverter => PresetConverter;

    public static ValueComparer<IReadOnlyDictionary<string, JsonElement>> PropertiesComparer => PresetComparer;

    /// <summary>Diffs of activity events: a plain JSON object.</summary>
    public static ValueConverter<JsonObject, string> DiffConverter { get; } =
        new(diff => diff.ToJsonString(Options), json => (JsonNode.Parse(json, null, default) as JsonObject) ?? new JsonObject());

    public static ValueComparer<JsonObject> DiffComparer { get; } =
        new(
            (a, b) => a!.ToJsonString(Options) == b!.ToJsonString(Options),
            diff => StringComparer.Ordinal.GetHashCode(diff.ToJsonString(Options)),
            diff => (JsonObject)diff.DeepClone());

    /// <summary>Filter DSL of views (nullable column: null = no filter).</summary>
    public static ValueConverter<ViewFilterNode?, string> ViewFilterConverter { get; } =
        new(filter => JsonSerializer.Serialize(filter, Options), json => JsonSerializer.Deserialize<ViewFilterNode>(json, Options)!);

    public static ValueComparer<ViewFilterNode?> ViewFilterComparer { get; } =
        new(
            (a, b) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options),
            filter => StringComparer.Ordinal.GetHashCode(JsonSerializer.Serialize(filter, Options)),
            filter => JsonSerializer.Deserialize<ViewFilterNode>(JsonSerializer.Serialize(filter, Options), Options)!);

    public static ValueConverter<IReadOnlyList<ViewSort>, string> ViewSortsConverter { get; } =
        new(sorts => JsonSerializer.Serialize(sorts, Options), json => JsonSerializer.Deserialize<List<ViewSort>>(json, Options)!);

    public static ValueComparer<IReadOnlyList<ViewSort>> ViewSortsComparer { get; } =
        new(
            (a, b) => a!.SequenceEqual(b!),
            sorts => sorts.Aggregate(0, (hash, sort) => HashCode.Combine(hash, sort)),
            sorts => sorts.ToList());

    public static ValueConverter<IReadOnlyList<string>, string> StringListConverter { get; } =
        new(list => JsonSerializer.Serialize(list, Options), json => JsonSerializer.Deserialize<List<string>>(json, Options)!);

    public static ValueComparer<IReadOnlyList<string>> StringListComparer { get; } =
        new(
            (a, b) => a!.SequenceEqual(b!),
            list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item)),
            list => list.ToList());

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
