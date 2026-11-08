using System.Text.Json;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Collections.Schema;

namespace Agcowork.Application.Schema;

/// <summary>A property definition as sent by clients. Strings instead of enums, so unknown values get a field error with the allowed values.</summary>
/// <param name="Key">snake_case identifier, unique in the collection, immutable: <c>^[a-z][a-z0-9_]{0,62}$</c>, e.g. <c>due_date</c>.</param>
/// <param name="Name">Display name, e.g. <c>Due date</c>.</param>
/// <param name="Type">One of text, markdown, number, checkbox, select, multi_select, status, date, datetime, url, email, relation, actor, json, created_at, updated_at, created_by, updated_by.</param>
/// <param name="Description">What belongs in the property, written for agents. Strongly recommended.</param>
/// <param name="Required">A value must be present on every record. Default false (title: always true).</param>
/// <param name="Options">select, multi_select, status: the choices. Status options need a group (todo, in_progress, done).</param>
/// <param name="TargetCollectionId">relation: the collection (same workspace) whose records are referenced.</param>
/// <param name="Multiple">relation: allow several references. Default false.</param>
/// <param name="Default">Value for new records that leave the property out. Not for relation, actor and computed types.</param>
/// <param name="Unit">number: unit shown next to the value, e.g. <c>h</c>.</param>
/// <param name="Precision">number: maximum decimal places, 0-10 (0 = whole numbers).</param>
public sealed record PropertyDefinitionInput(
    string? Key,
    string? Name,
    string? Type,
    string? Description = null,
    bool? Required = null,
    IReadOnlyList<PropertyOptionInput>? Options = null,
    Guid? TargetCollectionId = null,
    bool? Multiple = null,
    JsonElement? Default = null,
    string? Unit = null,
    int? Precision = null);

/// <summary>A choice of a select, multi_select or status property.</summary>
/// <param name="Value">Stored value, unique in the property, case-sensitive, e.g. <c>high</c>.</param>
/// <param name="Label">Display text; defaults to the value.</param>
/// <param name="Color">gray, brown, orange, yellow, green, blue, purple, pink or red.</param>
/// <param name="Group">Status only, required there: todo, in_progress or done.</param>
public sealed record PropertyOptionInput(string? Value, string? Label = null, string? Color = null, string? Group = null);

/// <summary>Turns client input into <see cref="PropertyDefinition"/>s, reporting every problem as a field error.</summary>
internal static class SchemaInput
{
    /// <summary>Parses all definitions of a list (<paramref name="field"/> = <c>schema</c> → <c>schema[i].key</c>); null entries failed. Checks unique keys.</summary>
    public static List<PropertyDefinition?> ParseList(FieldErrors errors, string field, IReadOnlyList<PropertyDefinitionInput?> inputs)
    {
        if (inputs.Count > CollectionSchema.MaxProperties)
        {
            errors.Add(field, ErrorCodes.TooLong, $"A collection has at most {CollectionSchema.MaxProperties} properties.");
        }

        var parsed = new List<PropertyDefinition?>(inputs.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < inputs.Count; i++)
        {
            var itemField = $"{field}[{i}]";
            parsed.Add(Parse(errors, itemField, inputs[i]));
            if (inputs[i]?.Key is { Length: > 0 } key && !keys.Add(key))
            {
                errors.Add($"{itemField}.key", ErrorCodes.Duplicate, $"Key '{key}' occurs more than once. Keys must be unique in a collection.");
            }
        }

        return parsed;
    }

    /// <summary>Parses one definition; null (with errors added) when it is invalid.</summary>
    public static PropertyDefinition? Parse(FieldErrors errors, string field, PropertyDefinitionInput? input)
    {
        if (input is null)
        {
            errors.Add(field, ErrorCodes.Required, $"'{field}' must be a property definition object.");
            return null;
        }

        var before = errors.Items.Count;
        var type = ParseType(errors, $"{field}.type", input.Type);
        var options = ParseOptions(errors, $"{field}.options", input.Options);

        CheckStorable(errors, field, input.Name, input.Description, input.Unit, input.Default);
        var isTitle = string.Equals(input.Key, CollectionSchema.TitleKey, StringComparison.Ordinal);

        var definition = new PropertyDefinition
        {
            Key = input.Key ?? string.Empty,
            Name = input.Name?.Trim() ?? string.Empty,
            // An unknown type is reported above; Json has no type-specific rules, so only key/name rules apply below.
            Type = type ?? PropertyType.Json,
            Description = Optional(input.Description),
            Required = input.Required ?? isTitle,
            Options = options,
            TargetCollectionId = input.TargetCollectionId,
            Multiple = input.Multiple ?? false,
            Default = input.Default is { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } ? null : input.Default,
            Unit = Optional(input.Unit),
            Precision = input.Precision,
        };

        var violations = definition.Validate();
        AddViolations(errors, field, type is null ? violations.Where(v => v.Path is "key" or "name" or "description") : violations);
        if (errors.Items.Count != before)
        {
            return null;
        }

        definition = WithNormalizedDefault(definition);
        CheckDefault(errors, $"{field}.default", definition);
        return errors.Items.Count == before ? definition : null;
    }

    public static PropertyType? ParseType(FieldErrors errors, string field, string? type)
    {
        if (type is null)
        {
            errors.Add(field, ErrorCodes.Required, $"'{field}' is required.", PropertyTypes.AllWireNames);
            return null;
        }

        if (PropertyTypes.TryParse(type, out var parsed))
        {
            return parsed;
        }

        errors.Add(field, ErrorCodes.NotInOptions, $"'{type}' is not a property type.", PropertyTypes.AllWireNames);
        return null;
    }

    /// <summary>Options with labels defaulted to values; group and shape errors are added (rule checks follow in the domain).</summary>
    public static IReadOnlyList<PropertyOption>? ParseOptions(FieldErrors errors, string field, IReadOnlyList<PropertyOptionInput?>? inputs)
    {
        if (inputs is null)
        {
            return null;
        }

        var options = new List<PropertyOption>(inputs.Count);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (input is null)
            {
                errors.Add($"{field}[{i}]", ErrorCodes.Required, "Option must be an object with at least 'value'.");
                continue;
            }

            StatusGroup? group = null;
            if (input.Group is not null)
            {
                if (PropertyOption.TryParseGroup(input.Group, out var parsed))
                {
                    group = parsed;
                }
                else
                {
                    errors.Add($"{field}[{i}].group", ErrorCodes.NotInOptions, $"'{input.Group}' is not a status group.", PropertyOption.StatusGroups);
                    continue;
                }
            }

            var valueStorable = errors.Storable($"{field}[{i}].value", input.Value);
            var labelStorable = errors.Storable($"{field}[{i}].label", input.Label);
            if (!valueStorable || !labelStorable)
            {
                continue;
            }

            var value = input.Value?.Trim() ?? string.Empty;
            options.Add(new PropertyOption(value, Optional(input.Label) ?? value, Optional(input.Color), group));
        }

        return options;
    }

    /// <summary>
    /// Display texts and the default of a definition are stored with the schema (jsonb): NUL and unpaired surrogates
    /// (<see cref="StorableText"/>) are field errors <c>invalid_value</c> on <c>{field}.name</c>, <c>.description</c>,
    /// <c>.unit</c> and <c>.default</c>. Option values and labels are checked by <see cref="ParseOptions"/>.
    /// </summary>
    public static void CheckStorable(FieldErrors errors, string field, string? name, string? description, string? unit, JsonElement? defaultValue)
    {
        errors.Storable($"{field}.name", name);
        errors.Storable($"{field}.description", description);
        errors.Storable($"{field}.unit", unit);
        if (defaultValue is { } value && !StorableText.IsStorable(value))
        {
            errors.Add($"{field}.default", ErrorCodes.InvalidValue, $"'{field}.default' {StorableText.Hint}");
        }
    }

    public static void AddViolations(FieldErrors errors, string prefix, IEnumerable<SchemaViolation> violations)
    {
        foreach (var violation in violations)
        {
            var anchored = violation.Prefixed(prefix);
            errors.Add(anchored.Path, anchored.Code, anchored.Message, anchored.Allowed);
        }
    }

    /// <summary>Checks <see cref="PropertyDefinition.Default"/> (if any) against the property's own type.</summary>
    public static void CheckDefault(FieldErrors errors, string field, PropertyDefinition property)
    {
        if (property.Default is not { } value)
        {
            return;
        }

        if (property.IsComputed || property.Type is PropertyType.Relation or PropertyType.Actor)
        {
            errors.Add(field, ErrorCodes.NotAllowed, $"{PropertyTypes.ToWire(property.Type)} properties cannot have a default.");
            return;
        }

        PropertyTypeRegistry.CheckValue(property, value, field, errors);
    }

    public static PropertyDefinition WithNormalizedDefault(PropertyDefinition property) =>
        property.Default is { } value && !property.IsComputed
            ? property with { Default = PropertyTypeRegistry.Get(property.Type).Normalize(property, value) }
            : property;

    private static readonly JsonSerializerOptions ComparisonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>True when both definitions are equal in every member (options and default compared by value).</summary>
    public static bool SameDefinition(PropertyDefinition a, PropertyDefinition b) =>
        string.Equals(JsonSerializer.Serialize(a, ComparisonOptions), JsonSerializer.Serialize(b, ComparisonOptions), StringComparison.Ordinal);

    /// <summary>Trimmed text or null for null/whitespace.</summary>
    public static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
