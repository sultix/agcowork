using System.Text.Json;
using Agcowork.Domain.Collections;

namespace Agcowork.Application.Collections.Templates;

/// <summary>A record template of a collection.</summary>
/// <param name="Id">Template id; pass it as <c>templateId</c> when creating a record.</param>
/// <param name="CollectionId">Collection the template belongs to.</param>
/// <param name="Name">Display name, unique within the collection.</param>
/// <param name="Description">When to use the template.</param>
/// <param name="PropertiesPreset">Property values (by key) a new record starts with.</param>
/// <param name="BodyMarkdown">Markdown body a new record starts with.</param>
/// <param name="IsDefault">Used when a record is created without naming a template.</param>
/// <param name="CreatedBy">Actor that created the template.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
public sealed record TemplateDto(
    Guid Id,
    Guid CollectionId,
    string Name,
    string? Description,
    IReadOnlyDictionary<string, JsonElement> PropertiesPreset,
    string BodyMarkdown,
    bool IsDefault,
    Guid CreatedBy,
    DateTimeOffset CreatedAt)
{
    public static TemplateDto From(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new TemplateDto(
            template.Id,
            template.CollectionId,
            template.Name,
            template.Description,
            new Dictionary<string, JsonElement>(template.PropertiesPreset, StringComparer.Ordinal),
            template.BodyMarkdown,
            template.IsDefault,
            template.CreatedBy,
            template.CreatedAt);
    }
}
