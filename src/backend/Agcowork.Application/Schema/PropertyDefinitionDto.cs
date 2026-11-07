using System.Text.Json;
using Agcowork.Domain.Collections.Schema;

namespace Agcowork.Application.Schema;

/// <summary>One property of a collection schema.</summary>
/// <param name="Key">snake_case identifier; the key in a record's <c>properties</c>. Immutable.</param>
/// <param name="Name">Display name.</param>
/// <param name="Type">Value type, e.g. <c>text</c>, <c>status</c>, <c>relation</c>.</param>
/// <param name="Description">What belongs in the property. Written for agents.</param>
/// <param name="Required">A value must be present on every record.</param>
/// <param name="ReadOnly">Computed by the system (created_at, updated_at, created_by, updated_by); never send it.</param>
/// <param name="Options">Choices of select, multi_select and status; null for other types.</param>
/// <param name="TargetCollectionId">Relation only: collection of the referenced records.</param>
/// <param name="Multiple">Relation only: several references allowed.</param>
/// <param name="Default">Value new records get when they do not set the property.</param>
/// <param name="Unit">Number only: unit, e.g. <c>h</c>.</param>
/// <param name="Precision">Number only: maximum decimal places.</param>
public sealed record PropertyDefinitionDto(
    string Key,
    string Name,
    PropertyType Type,
    string? Description,
    bool Required,
    bool ReadOnly,
    IReadOnlyList<PropertyOptionDto>? Options,
    Guid? TargetCollectionId,
    bool Multiple,
    JsonElement? Default,
    string? Unit,
    int? Precision)
{
    public static PropertyDefinitionDto From(PropertyDefinition property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new PropertyDefinitionDto(
            property.Key,
            property.Name,
            property.Type,
            property.Description,
            property.Required,
            property.IsComputed,
            property.Options?.Select(o => new PropertyOptionDto(o.Value, o.Label, o.Color, o.Group)).ToList(),
            property.TargetCollectionId,
            property.Multiple,
            property.Default,
            property.Unit,
            property.Precision);
    }

    public static IReadOnlyList<PropertyDefinitionDto> From(IEnumerable<PropertyDefinition> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return [.. schema.Select(From)];
    }
}

/// <summary>A choice of a select, multi_select or status property.</summary>
/// <param name="Value">Stored value (case-sensitive). Send this in record properties.</param>
/// <param name="Label">Display text.</param>
/// <param name="Color">UI color, if set.</param>
/// <param name="Group">Status only: <c>todo</c>, <c>in_progress</c> or <c>done</c>.</param>
public sealed record PropertyOptionDto(string Value, string Label, string? Color, StatusGroup? Group);
