using System.Text.Json;
using Agcowork.Domain.Common;

namespace Agcowork.Domain.Collections;

/// <summary>
/// Record template of a collection: preset property values and a Markdown body (e.g. an ADR outline).
/// At most one template per collection is the default. The preset is validated against the schema by the
/// application layer.
/// </summary>
public sealed class Template : ITenantEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int BodyMaxLength = 200_000;

    private Template(
        Guid id,
        Guid workspaceId,
        Guid collectionId,
        string name,
        string? description,
        IReadOnlyDictionary<string, JsonElement> propertiesPreset,
        string bodyMarkdown,
        bool isDefault,
        Guid createdBy,
        DateTimeOffset createdAt)
    {
        Id = id;
        WorkspaceId = workspaceId;
        CollectionId = collectionId;
        Name = name;
        Description = description;
        PropertiesPreset = propertiesPreset;
        BodyMarkdown = bodyMarkdown;
        IsDefault = isDefault;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
    }

    // For EF Core materialization.
    private Template()
    {
        Name = string.Empty;
        PropertiesPreset = new Dictionary<string, JsonElement>();
        BodyMarkdown = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid CollectionId { get; private set; }

    public string Name { get; private set; }

    /// <summary>When to use this template. Written for agents.</summary>
    public string? Description { get; private set; }

    /// <summary>Property values (by key) a new record starts with.</summary>
    public IReadOnlyDictionary<string, JsonElement> PropertiesPreset { get; private set; }

    /// <summary>Markdown body a new record starts with.</summary>
    public string BodyMarkdown { get; private set; }

    /// <summary>Used for new records of the collection when no template is named.</summary>
    public bool IsDefault { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Template Create(
        Guid workspaceId,
        Guid collectionId,
        string name,
        string? description,
        IReadOnlyDictionary<string, JsonElement>? propertiesPreset,
        string? bodyMarkdown,
        bool isDefault,
        Guid createdBy,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must be set.", nameof(workspaceId));
        }

        if (collectionId == Guid.Empty)
        {
            throw new ArgumentException("Collection id must be set.", nameof(collectionId));
        }

        if (createdBy == Guid.Empty)
        {
            throw new ArgumentException("Creator must be set.", nameof(createdBy));
        }

        var createdAt = timeProvider.GetUtcNow();
        return new Template(
            Guid.CreateVersion7(createdAt),
            workspaceId,
            collectionId,
            GuardText.Required(name, NameMaxLength, nameof(name)),
            GuardText.Optional(description, DescriptionMaxLength, nameof(description)),
            GuardPreset(propertiesPreset),
            GuardBody(bodyMarkdown),
            isDefault,
            createdBy,
            createdAt);
    }

    public void Rename(string name) => Name = GuardText.Required(name, NameMaxLength, nameof(name));

    /// <summary>Sets the description; null, empty or whitespace clears it.</summary>
    public void Describe(string? description) => Description = GuardText.Optional(description, DescriptionMaxLength, nameof(description));

    public void ChangePreset(IReadOnlyDictionary<string, JsonElement>? propertiesPreset) => PropertiesPreset = GuardPreset(propertiesPreset);

    public void ChangeBody(string? bodyMarkdown) => BodyMarkdown = GuardBody(bodyMarkdown);

    public void MarkDefault(bool isDefault) => IsDefault = isDefault;

    private static Dictionary<string, JsonElement> GuardPreset(IReadOnlyDictionary<string, JsonElement>? preset) =>
        preset is null ? [] : new Dictionary<string, JsonElement>(preset, StringComparer.Ordinal);

    private static string GuardBody(string? body)
    {
        if (body is { Length: > BodyMaxLength })
        {
            throw new ArgumentException($"Body must not exceed {BodyMaxLength} characters.", nameof(body));
        }

        return body ?? string.Empty;
    }
}
