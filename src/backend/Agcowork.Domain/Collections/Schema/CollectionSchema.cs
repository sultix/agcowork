namespace Agcowork.Domain.Collections.Schema;

/// <summary>
/// Rules across the property definitions of one collection, and between two versions of a property.
/// </summary>
public static class CollectionSchema
{
    /// <summary>Key of the title property every collection has (text, required).</summary>
    public const string TitleKey = "title";

    public const int MaxProperties = 200;

    /// <summary>
    /// Keys that would collide with system fields of a record (e.g. in the Markdown frontmatter of
    /// architecture §4.4). <see cref="TitleKey"/> is not in the list: it is reserved for the title property.
    /// </summary>
    public static IReadOnlyList<string> ReservedKeys { get; } = ["id", "collection", "space", "revision", "body"];

    /// <summary>
    /// All broken rules of <paramref name="properties"/>: each definition's own rules (path <c>[i].…</c>),
    /// unique keys and the size limit. Does not require the title to be present (see <see cref="WithTitle"/>).
    /// </summary>
    public static IReadOnlyList<SchemaViolation> Validate(IReadOnlyList<PropertyDefinition> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var violations = new List<SchemaViolation>();
        if (properties.Count > MaxProperties)
        {
            violations.Add(new(string.Empty, SchemaViolationCodes.TooLong, $"A collection has at most {MaxProperties} properties."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < properties.Count; i++)
        {
            var property = properties[i];
            if (property is null)
            {
                violations.Add(new($"[{i}]", SchemaViolationCodes.Required, "Property must not be null."));
                continue;
            }

            violations.AddRange(property.Validate().Select(v => v.Prefixed($"[{i}]")));
            if (!string.IsNullOrEmpty(property.Key) && !seen.Add(property.Key))
            {
                violations.Add(new($"[{i}].key", SchemaViolationCodes.Duplicate, $"Key '{property.Key}' occurs more than once. Keys must be unique in a collection."));
            }
        }

        return violations;
    }

    /// <summary>
    /// Broken rules when an existing property <paramref name="before"/> becomes <paramref name="after"/>
    /// (paths relative to the property). Keys never change; the type only along
    /// <see cref="PropertyTypes.IsAllowedChange"/>; a relation keeps its target and can only widen to multiple.
    /// </summary>
    public static IReadOnlyList<SchemaViolation> ValidateChange(PropertyDefinition before, PropertyDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var violations = new List<SchemaViolation>();

        if (!string.Equals(before.Key, after.Key, StringComparison.Ordinal))
        {
            violations.Add(new("key", SchemaViolationCodes.KeyImmutable, "Keys cannot be renamed. Add a new property and remove the old one instead."));
        }

        if (!PropertyTypes.IsAllowedChange(before.Type, after.Type))
        {
            violations.Add(new(
                "type",
                SchemaViolationCodes.TypeChangeNotAllowed,
                $"A {PropertyTypes.ToWire(before.Type)} property cannot become {PropertyTypes.ToWire(after.Type)}: stored values would not fit. "
                + "Allowed changes: text ↔ markdown, select → multi_select. Otherwise add a new property and remove the old one.",
                PropertyTypes.AllowedChangesFrom(before.Type)));
        }

        if (before.Type == PropertyType.Relation && after.Type == PropertyType.Relation)
        {
            if (before.TargetCollectionId != after.TargetCollectionId)
            {
                violations.Add(new("targetCollectionId", SchemaViolationCodes.NotAllowed, "The target collection of a relation cannot change. Add a new relation property instead."));
            }

            if (before.Multiple && !after.Multiple)
            {
                violations.Add(new("multiple", SchemaViolationCodes.NotAllowed, "A relation with several references cannot become single: stored values would not fit."));
            }
        }

        return violations;
    }

    /// <summary><paramref name="properties"/> with the default title property in front when it has none.</summary>
    public static IReadOnlyList<PropertyDefinition> WithTitle(IReadOnlyList<PropertyDefinition> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.Any(p => p is not null && string.Equals(p.Key, TitleKey, StringComparison.Ordinal))
            ? properties
            : [PropertyDefinition.Title(), .. properties];
    }

    /// <summary>The property with <paramref name="key"/>, or null.</summary>
    public static PropertyDefinition? Find(this IReadOnlyList<PropertyDefinition> properties, string key)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));
    }
}
