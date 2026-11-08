using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Identity;

namespace Agcowork.Application.Schema;

/// <summary>Checks one non-null value of a property. Adds field errors; collects references that need a database lookup.</summary>
public delegate void PropertyValueCheck(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references);

/// <summary>Everything the system knows about one property type: value rules, JSON Schema fragment, example, normalization.</summary>
/// <param name="Type">The type.</param>
/// <param name="Summary">How values look, written for agents (used in error messages).</param>
/// <param name="Check">Validates a non-null value.</param>
/// <param name="JsonSchema">JSON Schema (draft 2020-12) of a value, without title/description.</param>
/// <param name="Example">A valid example value, or null when none can be given (e.g. select without options, computed).</param>
/// <param name="Normalize">Canonical form of a value; returns the input unchanged when it does not have the expected shape.</param>
public sealed record PropertyTypeDescriptor(
    PropertyType Type,
    string Summary,
    PropertyValueCheck Check,
    Func<PropertyDefinition, JsonObject> JsonSchema,
    Func<PropertyDefinition, JsonNode?> Example,
    Func<PropertyDefinition, JsonElement, JsonElement> Normalize);

/// <summary>References found in values; <see cref="PropertyValueValidator"/> checks them against the database.</summary>
public sealed class ValueReferences
{
    internal List<(string Field, Guid TargetCollectionId, Guid RecordId)> Records { get; } = [];

    internal List<(string Field, Guid ActorId)> Actors { get; } = [];

    internal void AddRecord(string field, Guid targetCollectionId, Guid recordId) => Records.Add((field, targetCollectionId, recordId));

    internal void AddActor(string field, Guid actorId) => Actors.Add((field, actorId));
}

/// <summary>
/// Type system of property values (architecture §2, "Property-Typen"): one <see cref="PropertyTypeDescriptor"/>
/// per <see cref="PropertyType"/>. Used by <see cref="PropertyValueValidator"/>, <see cref="PropertyValueNormalizer"/>
/// and <see cref="JsonSchemaBuilder"/>, so all three agree on what a valid value is.
/// </summary>
public static partial class PropertyTypeRegistry
{
    public const int TextMaxLength = 10_000;
    public const int MarkdownMaxLength = 100_000;
    public const int UrlMaxLength = 2048;
    public const int JsonMaxLength = 100_000;
    public const int MaxItems = 1000;

    /// <summary>Example id used in error messages of relation and actor values.</summary>
    public const string ExampleId = "0190f5e2-7a4c-7000-8000-000000000001";

    private static readonly Dictionary<PropertyType, PropertyTypeDescriptor> Descriptors = Create().ToDictionary(d => d.Type);

    /// <summary>All descriptors, one per type.</summary>
    public static IReadOnlyCollection<PropertyTypeDescriptor> All => Descriptors.Values;

    public static PropertyTypeDescriptor Get(PropertyType type) =>
        Descriptors.TryGetValue(type, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown property type.");

    /// <summary>Validates a single value without database lookups (references are collected and ignored). For defaults and tests.</summary>
    public static void CheckValue(PropertyDefinition property, JsonElement value, string field, FieldErrors errors)
    {
        ArgumentNullException.ThrowIfNull(property);
        Get(property.Type).Check(property, value, field, errors, new ValueReferences());
    }

    private static IEnumerable<PropertyTypeDescriptor> Create()
    {
        yield return new(
            PropertyType.Text,
            $"a string (max. {TextMaxLength} characters)",
            (p, v, f, e, _) => CheckString(p, v, f, e, TextMaxLength),
            _ => new JsonObject { ["type"] = "string", ["maxLength"] = TextMaxLength },
            p => p.Key == CollectionSchema.TitleKey ? "Use PostgreSQL instead of MongoDB" : "Short text",
            (_, v) => TrimString(v));

        yield return new(
            PropertyType.Markdown,
            $"a Markdown string (max. {MarkdownMaxLength} characters)",
            (p, v, f, e, _) => CheckString(p, v, f, e, MarkdownMaxLength),
            _ => new JsonObject { ["type"] = "string", ["contentMediaType"] = "text/markdown", ["maxLength"] = MarkdownMaxLength },
            _ => "Text with **Markdown**.",
            (_, v) => v);

        yield return new(
            PropertyType.Number,
            "a JSON number",
            CheckNumber,
            NumberSchema,
            p => p.Precision is 0 ? 3 : 2.5m,
            (_, v) => v);

        yield return new(
            PropertyType.Checkbox,
            "true or false",
            (p, v, f, e, _) =>
            {
                if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    InvalidType(p, v, f, e);
                }
            },
            _ => new JsonObject { ["type"] = "boolean" },
            _ => true,
            (_, v) => v);

        yield return new(
            PropertyType.Select,
            "one option value as a string",
            CheckSingleOption,
            p => new JsonObject { ["type"] = "string", ["enum"] = OptionValuesArray(p) },
            FirstOption,
            (_, v) => TrimString(v));

        yield return new(
            PropertyType.MultiSelect,
            "an array of distinct option values",
            CheckMultiOption,
            p => new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["enum"] = OptionValuesArray(p) },
                ["uniqueItems"] = true,
                ["maxItems"] = MaxItems,
            },
            p => FirstOption(p) is { } first ? new JsonArray(first) : new JsonArray(),
            (_, v) => NormalizeArray(v, item => TrimString(item)));

        yield return new(
            PropertyType.Status,
            "one status option value as a string",
            CheckSingleOption,
            StatusSchema,
            FirstOption,
            (_, v) => TrimString(v));

        yield return new(
            PropertyType.Date,
            "an ISO 8601 date string 'YYYY-MM-DD', e.g. '2026-11-07'",
            CheckDate,
            _ => new JsonObject { ["type"] = "string", ["format"] = "date" },
            _ => "2026-11-07",
            (_, v) => TrimString(v));

        yield return new(
            PropertyType.DateTime,
            "an ISO 8601 date-time string with offset, e.g. '2026-11-07T14:30:00Z'",
            CheckDateTime,
            _ => new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            _ => "2026-11-07T14:30:00Z",
            (_, v) => NormalizeDateTime(v));

        yield return new(
            PropertyType.Url,
            "an absolute http(s) URL string",
            CheckUrl,
            _ => new JsonObject { ["type"] = "string", ["format"] = "uri", ["maxLength"] = UrlMaxLength },
            _ => "https://example.com/docs",
            (_, v) => TrimString(v));

        yield return new(
            PropertyType.Email,
            "an e-mail address string",
            CheckEmail,
            _ => new JsonObject { ["type"] = "string", ["format"] = "email", ["maxLength"] = Actor.EmailMaxLength },
            _ => "someone@example.com",
            (_, v) => TrimString(v));

        yield return new(
            PropertyType.Relation,
            "a record id (UUID string), or an array of record ids when the relation allows several",
            CheckRelation,
            RelationSchema,
            // No example: a made-up id would not pass validation; values must be ids of real records.
            _ => null,
            (_, v) => v.ValueKind == JsonValueKind.Array ? NormalizeArray(v, NormalizeId) : NormalizeId(v));

        yield return new(
            PropertyType.Actor,
            "the id (UUID string) of a member of this workspace",
            CheckActor,
            _ => new JsonObject { ["type"] = "string", ["format"] = "uuid", ["x-agcowork-ref"] = "member" },
            // No example: values must be ids of real members (GET /members).
            _ => null,
            (_, v) => NormalizeId(v));

        yield return new(
            PropertyType.Json,
            $"any JSON value (max. {JsonMaxLength} characters serialized)",
            CheckJson,
            _ => new JsonObject(),
            _ => new JsonObject { ["key"] = "value" },
            (_, v) => v);

        foreach (var computed in new[] { PropertyType.CreatedAt, PropertyType.UpdatedAt })
        {
            yield return new(
                computed,
                "computed by the system (read-only)",
                CheckComputed,
                _ => new JsonObject { ["type"] = "string", ["format"] = "date-time", ["readOnly"] = true },
                _ => null,
                (_, v) => v);
        }

        foreach (var computed in new[] { PropertyType.CreatedBy, PropertyType.UpdatedBy })
        {
            yield return new(
                computed,
                "computed by the system (read-only)",
                CheckComputed,
                _ => new JsonObject { ["type"] = "string", ["format"] = "uuid", ["readOnly"] = true, ["x-agcowork-ref"] = "member" },
                _ => null,
                (_, v) => v);
        }
    }

    private static void InvalidType(PropertyDefinition property, JsonElement value, string field, FieldErrors errors) =>
        errors.Add(
            field,
            ErrorCodes.InvalidType,
            $"'{field}' ({PropertyTypes.ToWire(property.Type)}) expects {Get(property.Type).Summary}, not {Describe(value.ValueKind)}.");

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => "null",
    };

    /// <summary>The string value; null with an error when the value is no string or has text that cannot be stored.</summary>
    private static string? ExpectString(PropertyDefinition property, JsonElement value, string field, FieldErrors errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            InvalidType(property, value, field, errors);
            return null;
        }

        if (StorableText.TryRead(value, out var text))
        {
            return text;
        }

        Unstorable(field, errors);
        return null;
    }

    /// <summary>NUL or an unpaired surrogate (<see cref="StorableText"/>): PostgreSQL cannot store the value.</summary>
    private static void Unstorable(string field, FieldErrors errors) =>
        errors.Add(field, ErrorCodes.InvalidValue, $"'{field}' {StorableText.Hint}");

    private static void CheckString(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, int maxLength)
    {
        if (ExpectString(property, value, field, errors) is { } text && text.Length > maxLength)
        {
            errors.Add(field, ErrorCodes.TooLong, $"'{field}' must not exceed {maxLength} characters.");
        }
    }

    private static void CheckNumber(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            InvalidType(property, value, field, errors);
            return;
        }

        if (!value.TryGetDecimal(out var number))
        {
            errors.Add(field, ErrorCodes.OutOfRange, $"'{field}' is too large or too precise to store.");
            return;
        }

        if (property.Precision is { } precision && decimal.Round(number, precision) != number)
        {
            errors.Add(
                field,
                ErrorCodes.InvalidFormat,
                precision == 0
                    ? $"'{field}' must be a whole number."
                    : $"'{field}' allows at most {precision} decimal places.");
        }
    }

    private static JsonObject NumberSchema(PropertyDefinition property)
    {
        var schema = new JsonObject { ["type"] = property.Precision is 0 ? "integer" : "number" };
        if (property.Precision is { } precision)
        {
            schema["x-agcowork-precision"] = precision;
        }

        if (property.Unit is { } unit)
        {
            schema["x-agcowork-unit"] = unit;
        }

        return schema;
    }

    private static IReadOnlyList<string> OptionValues(PropertyDefinition property) =>
        [.. (property.Options ?? []).Select(o => o.Value)];

    private static JsonArray OptionValuesArray(PropertyDefinition property) =>
        new([.. OptionValues(property).Select(v => (JsonNode?)JsonValue.Create(v))]);

    private static JsonValue? FirstOption(PropertyDefinition property) =>
        property.Options is [var first, ..] ? JsonValue.Create(first.Value) : null;

    private static void CheckSingleOption(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references)
    {
        if (ExpectString(property, value, field, errors) is { } text)
        {
            errors.OneOf(field, text, OptionValues(property));
        }
    }

    private static void CheckMultiOption(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            InvalidType(property, value, field, errors);
            return;
        }

        if (value.GetArrayLength() > MaxItems)
        {
            errors.Add(field, ErrorCodes.TooLong, $"'{field}' allows at most {MaxItems} values.");
            return;
        }

        var allowed = OptionValues(property);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var itemField = $"{field}[{index++}]";
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add(itemField, ErrorCodes.InvalidType, $"'{itemField}' must be an option value (string), not {Describe(item.ValueKind)}.");
                continue;
            }

            if (!StorableText.TryRead(item, out var text))
            {
                Unstorable(itemField, errors);
                continue;
            }

            if (errors.OneOf(itemField, text, allowed) && !seen.Add(text))
            {
                errors.Add(itemField, ErrorCodes.Duplicate, $"'{text}' occurs more than once in '{field}'.");
            }
        }
    }

    private static JsonObject StatusSchema(PropertyDefinition property)
    {
        var groups = new JsonObject();
        foreach (var option in property.Options ?? [])
        {
            if (option.Group is { } group)
            {
                groups[option.Value] = PropertyOption.ToWire(group);
            }
        }

        return new JsonObject { ["type"] = "string", ["enum"] = OptionValuesArray(property), ["x-agcowork-groups"] = groups };
    }

    private static void CheckDate(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references)
    {
        if (ExpectString(property, value, field, errors) is { } text
            && !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add(field, ErrorCodes.InvalidFormat, $"'{field}' must be an ISO 8601 date 'YYYY-MM-DD', e.g. '2026-11-07'.");
        }
    }

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    /// <summary>ISO 8601 date-time with explicit offset (<c>Z</c> or <c>±hh:mm</c>); local times are ambiguous and rejected.</summary>
    public static bool TryParseDateTime(string text, out DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!OffsetSuffix().IsMatch(text))
        {
            value = default;
            return false;
        }

        return DateTimeOffset.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static void CheckDateTime(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references)
    {
        if (ExpectString(property, value, field, errors) is { } text && !TryParseDateTime(text, out _))
        {
            errors.Add(
                field,
                ErrorCodes.InvalidFormat,
                $"'{field}' must be an ISO 8601 date-time with offset, e.g. '2026-11-07T14:30:00Z' or '2026-11-07T15:30:00+01:00'.");
        }
    }

    private static JsonElement NormalizeDateTime(JsonElement value)
    {
        if (!StorableText.TryRead(value, out var text) || !TryParseDateTime(text.Trim(), out var parsed))
        {
            return value;
        }

        return JsonSerializer.SerializeToElement(
            parsed.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
    }

    private static void CheckUrl(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references)
    {
        if (ExpectString(property, value, field, errors) is not { } text)
        {
            return;
        }

        if (text.Length > UrlMaxLength)
        {
            errors.Add(field, ErrorCodes.TooLong, $"'{field}' must not exceed {UrlMaxLength} characters.");
        }
        else if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            errors.Add(field, ErrorCodes.InvalidFormat, $"'{field}' must be an absolute http or https URL, e.g. 'https://example.com/docs'.");
        }
    }

    private static void CheckEmail(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references)
    {
        if (ExpectString(property, value, field, errors) is { } text && !Actor.IsValidEmail(text))
        {
            errors.Add(field, ErrorCodes.InvalidFormat, $"'{field}' must be an e-mail address like 'someone@example.com'.");
        }
    }

    private static Guid? ParseId(JsonElement value, string field, FieldErrors errors, string what)
    {
        if (StorableText.TryRead(value, out var text) && Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty)
        {
            return id;
        }

        if (value.ValueKind == JsonValueKind.String && !StorableText.TryRead(value, out _))
        {
            Unstorable(field, errors);
            return null;
        }

        errors.Add(
            field,
            value.ValueKind == JsonValueKind.String ? ErrorCodes.InvalidFormat : ErrorCodes.InvalidType,
            $"'{field}' must be {what} as a UUID string, e.g. '{ExampleId}'.");
        return null;
    }

    private static void CheckRelation(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references)
    {
        var target = property.TargetCollectionId ?? Guid.Empty;
        if (!property.Multiple)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                errors.Add(field, ErrorCodes.InvalidType, $"'{field}' references exactly one record: send a single id, not an array.");
            }
            else if (ParseId(value, field, errors, "a record id") is { } id)
            {
                references.AddRecord(field, target, id);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(field, ErrorCodes.InvalidType, $"'{field}' references several records: send an array of record ids.");
            return;
        }

        if (value.GetArrayLength() > MaxItems)
        {
            errors.Add(field, ErrorCodes.TooLong, $"'{field}' allows at most {MaxItems} references.");
            return;
        }

        var seen = new HashSet<Guid>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var itemField = $"{field}[{index++}]";
            if (ParseId(item, itemField, errors, "a record id") is not { } id)
            {
                continue;
            }

            if (!seen.Add(id))
            {
                errors.Add(itemField, ErrorCodes.Duplicate, $"Record {id} occurs more than once in '{field}'.");
                continue;
            }

            references.AddRecord(itemField, target, id);
        }
    }

    private static JsonObject RelationSchema(PropertyDefinition property)
    {
        var id = new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        var schema = property.Multiple
            ? new JsonObject { ["type"] = "array", ["items"] = id, ["uniqueItems"] = true, ["maxItems"] = MaxItems }
            : id;
        schema["x-agcowork-target-collection"] = property.TargetCollectionId?.ToString("D");
        return schema;
    }

    private static void CheckActor(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references)
    {
        if (ParseId(value, field, errors, "the actor id of a workspace member") is { } id)
        {
            references.AddActor(field, id);
        }
    }

    private static void CheckJson(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references)
    {
        if (value.GetRawText().Length > JsonMaxLength)
        {
            errors.Add(field, ErrorCodes.TooLong, $"'{field}' must not exceed {JsonMaxLength} characters when serialized.");
        }
        else if (!StorableText.IsStorable(value))
        {
            // jsonb rejects \u0000 and unpaired surrogates in strings and member names at any depth.
            Unstorable(field, errors);
        }
    }

    private static void CheckComputed(PropertyDefinition property, JsonElement value, string field, FieldErrors errors, ValueReferences references) =>
        errors.Add(
            field,
            ErrorCodes.ReadOnly,
            $"'{field}' is computed by the system ({PropertyTypes.ToWire(property.Type)}) and cannot be written. Leave it out.");

    // Normalizers leave text that cannot be stored unchanged (reading it may throw); the check reports it as invalid_value.
    private static JsonElement TrimString(JsonElement value) =>
        StorableText.TryRead(value, out var text) && text.Trim() is var trimmed && trimmed.Length != text.Length
            ? JsonSerializer.SerializeToElement(trimmed)
            : value;

    private static JsonElement NormalizeId(JsonElement value) =>
        StorableText.TryRead(value, out var text) && Guid.TryParse(text.Trim(), out var id)
            ? JsonSerializer.SerializeToElement(id.ToString("D"))
            : value;

    /// <summary>Normalizes every item and drops later duplicates (by raw JSON); non-arrays stay as they are.</summary>
    private static JsonElement NormalizeArray(JsonElement value, Func<JsonElement, JsonElement> normalizeItem)
    {
        // Re-serializing would turn an unpaired surrogate into U+FFFD and hide it from the check.
        if (value.ValueKind != JsonValueKind.Array || !StorableText.IsStorable(value))
        {
            return value;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<JsonElement>();
        foreach (var item in value.EnumerateArray())
        {
            var normalized = normalizeItem(item);
            if (seen.Add(normalized.GetRawText()))
            {
                items.Add(normalized);
            }
        }

        return JsonSerializer.SerializeToElement(items);
    }

    [GeneratedRegex("(Z|[+-][0-9]{2}:[0-9]{2})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetSuffix();
}
