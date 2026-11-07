using System.Text.Json.Serialization;

namespace Agcowork.Domain.Collections.Schema;

/// <summary>
/// Type of a property (architecture §2, "Property-Typen"). Wire and storage names are snake_case,
/// e.g. <c>multi_select</c>; <c>datetime</c> is written as one word. Values are stable: add, never renumber.
/// </summary>
public enum PropertyType
{
    /// <summary>Single-line or short plain text.</summary>
    Text = 1,

    /// <summary>Longer text in Markdown.</summary>
    Markdown = 2,

    /// <summary>A number, optionally with unit and decimal places.</summary>
    Number = 3,

    /// <summary>true or false.</summary>
    Checkbox = 4,

    /// <summary>Exactly one of the options.</summary>
    Select = 5,

    /// <summary>Any subset of the options.</summary>
    MultiSelect = 6,

    /// <summary>One of the options; every option belongs to a group (todo, in_progress, done).</summary>
    Status = 7,

    /// <summary>Calendar date, ISO 8601 <c>YYYY-MM-DD</c>.</summary>
    Date = 8,

    /// <summary>Point in time, ISO 8601 with offset.</summary>
    [JsonStringEnumMemberName("datetime")]
    DateTime = 9,

    /// <summary>Absolute http(s) URL.</summary>
    Url = 10,

    /// <summary>E-mail address.</summary>
    Email = 11,

    /// <summary>Reference(s) to records of a target collection.</summary>
    Relation = 12,

    /// <summary>A member of the workspace (human or agent).</summary>
    Actor = 13,

    /// <summary>Free JSON value, e.g. agent payloads.</summary>
    Json = 14,

    /// <summary>Computed: when the record was created. Read-only.</summary>
    CreatedAt = 15,

    /// <summary>Computed: when the record was last changed. Read-only.</summary>
    UpdatedAt = 16,

    /// <summary>Computed: who created the record. Read-only.</summary>
    CreatedBy = 17,

    /// <summary>Computed: who changed the record last. Read-only.</summary>
    UpdatedBy = 18,
}

/// <summary>Facts about <see cref="PropertyType"/>s.</summary>
public static class PropertyTypes
{
    private static readonly (PropertyType Type, string Wire)[] Names =
    [
        (PropertyType.Text, "text"),
        (PropertyType.Markdown, "markdown"),
        (PropertyType.Number, "number"),
        (PropertyType.Checkbox, "checkbox"),
        (PropertyType.Select, "select"),
        (PropertyType.MultiSelect, "multi_select"),
        (PropertyType.Status, "status"),
        (PropertyType.Date, "date"),
        (PropertyType.DateTime, "datetime"),
        (PropertyType.Url, "url"),
        (PropertyType.Email, "email"),
        (PropertyType.Relation, "relation"),
        (PropertyType.Actor, "actor"),
        (PropertyType.Json, "json"),
        (PropertyType.CreatedAt, "created_at"),
        (PropertyType.UpdatedAt, "updated_at"),
        (PropertyType.CreatedBy, "created_by"),
        (PropertyType.UpdatedBy, "updated_by"),
    ];

    /// <summary>Wire names of all types, in declaration order.</summary>
    public static IReadOnlyList<string> AllWireNames { get; } = [.. Names.Select(n => n.Wire)];

    public static IReadOnlyList<PropertyType> All { get; } = [.. Names.Select(n => n.Type)];

    public static string ToWire(PropertyType type)
    {
        foreach (var (candidate, wire) in Names)
        {
            if (candidate == type)
            {
                return wire;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown property type.");
    }

    /// <summary>Parses a wire name (ordinal, exact).</summary>
    public static bool TryParse(string? wire, out PropertyType type)
    {
        foreach (var (candidate, name) in Names)
        {
            if (string.Equals(name, wire, StringComparison.Ordinal))
            {
                type = candidate;
                return true;
            }
        }

        type = default;
        return false;
    }

    /// <summary>Computed types are filled by the system and cannot be written.</summary>
    public static bool IsComputed(PropertyType type) =>
        type is PropertyType.CreatedAt or PropertyType.UpdatedAt or PropertyType.CreatedBy or PropertyType.UpdatedBy;

    /// <summary>Types whose values come from <see cref="PropertyDefinition.Options"/>.</summary>
    public static bool HasOptions(PropertyType type) =>
        type is PropertyType.Select or PropertyType.MultiSelect or PropertyType.Status;

    /// <summary>
    /// Type changes of an existing property that keep stored values valid: text ↔ markdown and
    /// select → multi_select. Every other change needs a new property.
    /// </summary>
    public static bool IsAllowedChange(PropertyType from, PropertyType to) =>
        from == to
        || (from, to) is (PropertyType.Text, PropertyType.Markdown)
            or (PropertyType.Markdown, PropertyType.Text)
            or (PropertyType.Select, PropertyType.MultiSelect);

    /// <summary>Wire names of the types an existing property of type <paramref name="from"/> may change to (including itself).</summary>
    public static IReadOnlyList<string> AllowedChangesFrom(PropertyType from) =>
        [.. All.Where(to => IsAllowedChange(from, to)).Select(ToWire)];
}
