using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Agcowork.Domain.Collections.Schema;

/// <summary>
/// One field of a collection schema. A flat record: type-specific members (<see cref="Options"/>,
/// <see cref="TargetCollectionId"/>, <see cref="Unit"/>, …) are null or default where they do not apply,
/// so the JSONB form needs no type discriminator and stays readable and additive.
/// </summary>
/// <remarks>
/// <see cref="Validate"/> lists every broken rule of the definition on its own; rules across the schema
/// (unique keys) are in <see cref="CollectionSchema"/>. Values (<see cref="Default"/>) are checked by the
/// application layer, which knows how each type is validated.
/// </remarks>
public sealed partial record PropertyDefinition
{
    public const int KeyMaxLength = 63;
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int UnitMaxLength = 32;
    public const int MaxPrecision = 10;
    public const int MaxOptions = 500;

    /// <summary>snake_case identifier, unique in the collection, immutable after creation.</summary>
    public required string Key { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    public required PropertyType Type { get; init; }

    /// <summary>What belongs in the field. Written for agents.</summary>
    public string? Description { get; init; }

    /// <summary>A value must be present on every record.</summary>
    public bool Required { get; init; }

    /// <summary>Choices for select, multi_select and status; null for all other types.</summary>
    public IReadOnlyList<PropertyOption>? Options { get; init; }

    /// <summary>Relation only: the collection whose records are referenced (same workspace).</summary>
    public Guid? TargetCollectionId { get; init; }

    /// <summary>Relation only: true allows several references (n:m), false exactly one.</summary>
    public bool Multiple { get; init; }

    /// <summary>Value for new records that do not set the property.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>Number only: unit shown next to the value, e.g. <c>h</c> or <c>EUR</c>.</summary>
    public string? Unit { get; init; }

    /// <summary>Number only: maximum decimal places (0 = integers).</summary>
    public int? Precision { get; init; }

    /// <summary>Computed properties are read-only.</summary>
    [JsonIgnore]
    public bool IsComputed => PropertyTypes.IsComputed(Type);

    /// <summary>The always-present title property (<see cref="CollectionSchema.TitleKey"/>, text, required).</summary>
    public static PropertyDefinition Title(string? name = null, string? description = null) => new()
    {
        Key = CollectionSchema.TitleKey,
        Name = string.IsNullOrWhiteSpace(name) ? "Title" : name.Trim(),
        Type = PropertyType.Text,
        Description = string.IsNullOrWhiteSpace(description) ? "Title of the record: short, specific, unique enough to find it again." : description.Trim(),
        Required = true,
    };

    /// <summary>True for <c>^[a-z][a-z0-9_]{0,62}$</c>: lowercase snake_case, starting with a letter.</summary>
    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>All rules of this definition that are broken, with paths relative to the definition.</summary>
    public IReadOnlyList<SchemaViolation> Validate()
    {
        var violations = new List<SchemaViolation>();
        ValidateKey(violations);
        ValidateTexts(violations);

        if (!Enum.IsDefined(Type))
        {
            violations.Add(new("type", SchemaViolationCodes.NotInOptions, "Unknown property type.", PropertyTypes.AllWireNames));
            return violations;
        }

        if (IsComputed && Required)
        {
            violations.Add(new("required", SchemaViolationCodes.NotAllowed, "Computed properties are always filled by the system and cannot be required."));
        }

        ValidateOptions(violations);
        ValidateRelation(violations);
        ValidateNumber(violations);
        return violations;
    }

    private void ValidateKey(List<SchemaViolation> violations)
    {
        if (string.IsNullOrEmpty(Key))
        {
            violations.Add(new("key", SchemaViolationCodes.Required, "'key' is required: a snake_case identifier such as 'due_date'."));
            return;
        }

        if (Key.Length > KeyMaxLength)
        {
            violations.Add(new("key", SchemaViolationCodes.TooLong, $"'key' must not exceed {KeyMaxLength} characters."));
            return;
        }

        if (!IsValidKey(Key))
        {
            violations.Add(new("key", SchemaViolationCodes.InvalidFormat, "'key' must be snake_case: start with a lowercase letter, then lowercase letters, digits or '_', e.g. 'due_date'."));
            return;
        }

        if (CollectionSchema.ReservedKeys.Contains(Key, StringComparer.Ordinal))
        {
            violations.Add(new(
                "key",
                SchemaViolationCodes.Reserved,
                $"'{Key}' is reserved for a system field of records ({string.Join(", ", CollectionSchema.ReservedKeys)}). Choose another key."));
        }

        if (string.Equals(Key, CollectionSchema.TitleKey, StringComparison.Ordinal))
        {
            if (Type != PropertyType.Text)
            {
                violations.Add(new(
                    "type",
                    SchemaViolationCodes.Reserved,
                    "The key 'title' is reserved for the record title, which is always of type text. Use another key for this property.",
                    [PropertyTypes.ToWire(PropertyType.Text)]));
            }

            if (!Required)
            {
                violations.Add(new("required", SchemaViolationCodes.NotAllowed, "The title property is always required."));
            }
        }
    }

    private void ValidateTexts(List<SchemaViolation> violations)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            violations.Add(new("name", SchemaViolationCodes.Required, "'name' is required: the display name, e.g. 'Due date'."));
        }
        else if (Name.Length > NameMaxLength)
        {
            violations.Add(new("name", SchemaViolationCodes.TooLong, $"'name' must not exceed {NameMaxLength} characters."));
        }

        if (Description is { Length: > DescriptionMaxLength })
        {
            violations.Add(new("description", SchemaViolationCodes.TooLong, $"'description' must not exceed {DescriptionMaxLength} characters."));
        }
    }

    private void ValidateOptions(List<SchemaViolation> violations)
    {
        if (!PropertyTypes.HasOptions(Type))
        {
            if (Options is not null)
            {
                violations.Add(new("options", SchemaViolationCodes.NotAllowed, "'options' only apply to select, multi_select and status properties."));
            }

            return;
        }

        var options = Options ?? [];
        if (options.Count > MaxOptions)
        {
            violations.Add(new("options", SchemaViolationCodes.TooLong, $"At most {MaxOptions} options are allowed."));
        }

        if (Required && options.Count == 0)
        {
            violations.Add(new("options", SchemaViolationCodes.Required, "A required select, multi_select or status property needs at least one option, otherwise no record could be valid."));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var path = $"options[{i}]";
            if (option is null)
            {
                violations.Add(new(path, SchemaViolationCodes.Required, "Option must not be null."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(option.Value))
            {
                violations.Add(new($"{path}.value", SchemaViolationCodes.Required, "'value' is required: the stored value of the option, e.g. 'high'."));
            }
            else if (option.Value.Length > PropertyOption.ValueMaxLength)
            {
                violations.Add(new($"{path}.value", SchemaViolationCodes.TooLong, $"'value' must not exceed {PropertyOption.ValueMaxLength} characters."));
            }
            else if (!seen.Add(option.Value))
            {
                violations.Add(new($"{path}.value", SchemaViolationCodes.Duplicate, $"Option value '{option.Value}' occurs more than once. Values must be unique (case-sensitive)."));
            }

            if (string.IsNullOrWhiteSpace(option.Label))
            {
                violations.Add(new($"{path}.label", SchemaViolationCodes.Required, "'label' is required."));
            }
            else if (option.Label.Length > PropertyOption.LabelMaxLength)
            {
                violations.Add(new($"{path}.label", SchemaViolationCodes.TooLong, $"'label' must not exceed {PropertyOption.LabelMaxLength} characters."));
            }

            if (option.Color is not null && !PropertyOption.Colors.Contains(option.Color, StringComparer.Ordinal))
            {
                violations.Add(new($"{path}.color", SchemaViolationCodes.NotInOptions, $"'{option.Color}' is not a known color.", PropertyOption.Colors));
            }

            if (Type == PropertyType.Status)
            {
                if (option.Group is null)
                {
                    violations.Add(new(
                        $"{path}.group",
                        SchemaViolationCodes.Required,
                        "Status options need a 'group': todo, in_progress or done. Boards and filters use it.",
                        PropertyOption.StatusGroups));
                }
                else if (!Enum.IsDefined(option.Group.Value))
                {
                    violations.Add(new($"{path}.group", SchemaViolationCodes.NotInOptions, "Unknown status group.", PropertyOption.StatusGroups));
                }
            }
            else if (option.Group is not null)
            {
                violations.Add(new($"{path}.group", SchemaViolationCodes.NotAllowed, "'group' only applies to status options."));
            }
        }
    }

    private void ValidateRelation(List<SchemaViolation> violations)
    {
        if (Type == PropertyType.Relation)
        {
            if (TargetCollectionId is null || TargetCollectionId == Guid.Empty)
            {
                violations.Add(new("targetCollectionId", SchemaViolationCodes.Required, "Relation properties need 'targetCollectionId': the collection whose records they reference."));
            }

            return;
        }

        if (TargetCollectionId is not null)
        {
            violations.Add(new("targetCollectionId", SchemaViolationCodes.NotAllowed, "'targetCollectionId' only applies to relation properties."));
        }

        if (Multiple)
        {
            violations.Add(new("multiple", SchemaViolationCodes.NotAllowed, "'multiple' only applies to relation properties; use multi_select for several choices."));
        }
    }

    private void ValidateNumber(List<SchemaViolation> violations)
    {
        if (Type != PropertyType.Number)
        {
            if (Unit is not null)
            {
                violations.Add(new("unit", SchemaViolationCodes.NotAllowed, "'unit' only applies to number properties."));
            }

            if (Precision is not null)
            {
                violations.Add(new("precision", SchemaViolationCodes.NotAllowed, "'precision' only applies to number properties."));
            }

            return;
        }

        if (Unit is { Length: > UnitMaxLength })
        {
            violations.Add(new("unit", SchemaViolationCodes.TooLong, $"'unit' must not exceed {UnitMaxLength} characters."));
        }

        if (Precision is < 0 or > MaxPrecision)
        {
            violations.Add(new("precision", SchemaViolationCodes.OutOfRange, $"'precision' (decimal places) must be between 0 and {MaxPrecision}."));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
