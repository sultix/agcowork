using System.Text.Json;
using System.Text.Json.Nodes;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;

namespace Agcowork.Application.Schema;

/// <summary>
/// JSON Schema (draft 2020-12) of the <c>properties</c> object of a collection's records, for agents
/// (architecture §4.1, "Schema maschinenlesbar"): per property the value schema of its type, <c>title</c> and
/// <c>description</c> from the definition, <c>enum</c> from the options, <c>default</c>, an example and
/// <c>x-agcowork-type</c>; <c>required</c> lists the required keys, which must not be empty (<c>minLength</c>/<c>minItems</c> 1);
/// optional properties also accept <c>null</c>, which clears the value; unknown keys are rejected
/// (<c>additionalProperties: false</c>). Computed properties are <c>readOnly</c>.
/// </summary>
public static class JsonSchemaBuilder
{
    public const string Draft = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>Appended to the root description: how to clear values.</summary>
    public const string NullHint = "Optional properties accept null, which clears the value; required properties must not be empty.";

    public static JsonObject Build(Collection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return Build(collection.Id, collection.Name, collection.Description, collection.Schema);
    }

    public static JsonObject Build(Guid collectionId, string name, string? description, IReadOnlyList<PropertyDefinition> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var property in schema)
        {
            properties[property.Key] = PropertySchema(property);
            if (property.Required)
            {
                required.Add(property.Key);
            }
        }

        var root = new JsonObject
        {
            ["$schema"] = Draft,
            ["$id"] = $"agcowork://collection/{collectionId:D}/schema",
            ["title"] = name,
        };
        root["description"] = string.IsNullOrWhiteSpace(description) ? NullHint : $"{description.Trim()} {NullHint}";

        root["type"] = "object";
        root["properties"] = properties;
        root["required"] = required;
        root["additionalProperties"] = false;
        return root;
    }

    /// <summary>
    /// An example <c>properties</c> object: one valid value per writable property that has one. Relation and actor
    /// properties are left out: their values must be real ids of records and members.
    /// </summary>
    public static JsonObject Example(IReadOnlyList<PropertyDefinition> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var example = new JsonObject();
        foreach (var property in schema.Where(p => !p.IsComputed))
        {
            if (ExampleValue(property) is { } value)
            {
                example[property.Key] = value;
            }
        }

        return example;
    }

    private static JsonObject PropertySchema(PropertyDefinition property)
    {
        var descriptor = PropertyTypeRegistry.Get(property.Type);
        var schema = new JsonObject { ["title"] = property.Name };
        if (!string.IsNullOrWhiteSpace(property.Description))
        {
            schema["description"] = property.Description;
        }

        foreach (var (name, value) in descriptor.JsonSchema(property).ToList())
        {
            schema[name] = value?.DeepClone();
        }

        ApplyPresence(schema, property);

        if (property.Default is { } defaultValue)
        {
            schema["default"] = JsonNode.Parse(defaultValue.GetRawText());
        }

        if (ExampleValue(property) is { } example)
        {
            schema["examples"] = new JsonArray(example);
        }

        schema["x-agcowork-type"] = PropertyTypes.ToWire(property.Type);
        return schema;
    }

    /// <summary>Required: no empty strings or arrays. Optional: <c>null</c> is accepted as well (it clears the value).</summary>
    private static void ApplyPresence(JsonObject schema, PropertyDefinition property)
    {
        if (schema["type"] is not JsonValue typeNode || typeNode.GetValue<string>() is not { } type)
        {
            return;
        }

        if (property.Required)
        {
            if (type == "string")
            {
                schema["minLength"] = 1;
            }
            else if (type == "array")
            {
                schema["minItems"] = 1;
            }

            return;
        }

        schema["type"] = new JsonArray(type, "null");
        if (schema["enum"] is JsonArray values)
        {
            values.Add(null);
        }
    }

    private static JsonNode? ExampleValue(PropertyDefinition property)
    {
        if (property.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } defaultValue)
        {
            return JsonNode.Parse(defaultValue.GetRawText());
        }

        return PropertyTypeRegistry.Get(property.Type).Example(property);
    }
}
