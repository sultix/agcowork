using System.Text.Json;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Domain.Collections;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections.Templates;

/// <summary>Shared rules of the template use cases.</summary>
internal static class TemplateSupport
{
    public const string PresetField = "propertiesPreset";

    public static UniqueConflict DefaultConflict { get; } = new(
        "ix_templates_default",
        new ConflictError("template_default_conflict", "Another template became the default concurrently. Read the templates again and retry.", "isDefault"));

    public static void Name(FieldErrors errors, string? name)
    {
        if (errors.Required("name", name))
        {
            errors.MaxLength("name", name, Template.NameMaxLength);
        }
    }

    public static void Texts(FieldErrors errors, string? description, string? bodyMarkdown)
    {
        errors.MaxLength("description", description, Template.DescriptionMaxLength);
        if (bodyMarkdown is { Length: > Template.BodyMaxLength })
        {
            errors.Add("bodyMarkdown", ErrorCodes.TooLong, $"'bodyMarkdown' must not exceed {Template.BodyMaxLength} characters.");
        }
    }

    /// <summary>The collection for a template change: 404 when missing, 409 when archived.</summary>
    public static async Task<(Collection? Collection, Error? Error)> LoadWritableCollectionAsync(IAgcoworkDbContext db, Guid collectionId, CancellationToken cancellationToken)
    {
        var collection = await db.Collections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == collectionId, cancellationToken).ConfigureAwait(false);
        if (collection is null)
        {
            return (null, new NotFoundError("collection"));
        }

        return collection.IsArchived ? (null, CollectionConflicts.Archived) : (collection, null);
    }

    /// <summary>
    /// Normalizes the preset and validates it against the schema as a partial record (presets need not
    /// contain required properties). Errors name <c>propertiesPreset.&lt;key&gt;</c>.
    /// </summary>
    public static async Task<(Dictionary<string, JsonElement> Preset, IReadOnlyList<FieldError> Errors)> ValidatePresetAsync(
        PropertyValueValidator validator,
        Collection collection,
        IReadOnlyDictionary<string, JsonElement> preset,
        CancellationToken cancellationToken)
    {
        var normalized = PropertyValueNormalizer.Normalize(collection.Schema, preset);
        var errors = await validator.ValidateAsync(collection.Schema, normalized, PropertyValidationMode.Partial, PresetField, cancellationToken).ConfigureAwait(false);
        // Null entries clear nothing in a preset; drop them so stored presets only carry values.
        foreach (var key in normalized.Where(p => p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined).Select(p => p.Key).ToList())
        {
            normalized.Remove(key);
        }

        return (normalized, errors);
    }

    /// <summary>Clears the default flag of the collection's other templates. Run inside the transaction that sets the new default.</summary>
    public static Task ClearOtherDefaultsAsync(IAgcoworkDbContext db, Guid collectionId, Guid templateId, CancellationToken cancellationToken) =>
        // Tenant filter applies; no IgnoreQueryFilters and no WorkspaceId change (rules of IAgcoworkDbContext).
        db.Templates
            .Where(t => t.CollectionId == collectionId && t.IsDefault && t.Id != templateId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.IsDefault, false), cancellationToken);
}
