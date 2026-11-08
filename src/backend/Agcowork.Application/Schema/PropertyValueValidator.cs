using System.Text.Json;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Collections.Schema;

namespace Agcowork.Application.Schema;

/// <summary>How complete the checked properties are.</summary>
public enum PropertyValidationMode
{
    /// <summary>All properties of a record (create): every required property must be present.</summary>
    Full = 1,

    /// <summary>A subset (record patch, template preset): absent properties are fine, but a required one cannot be set to null.</summary>
    Partial = 2,
}

/// <summary>
/// Validates property values against a collection schema, the single place for this rule set (architecture §5,
/// "Properties-Validierung"): REST, MCP and UI share it. Run <see cref="PropertyValueNormalizer.Normalize"/> first, so
/// harmless differences (whitespace, offsets, id casing) do not count as errors.
/// </summary>
/// <remarks>
/// Errors are field errors an agent can act on: <c>unknown_property</c> lists the writable keys,
/// <c>not_in_options</c> the option values, <c>invalid_type</c>/<c>invalid_format</c> say what is expected.
/// <c>null</c> clears an optional property. Relation and actor references are checked against the database
/// through <see cref="IRelationTargetChecker"/> and <see cref="IActorMembershipChecker"/>.
/// </remarks>
public sealed class PropertyValueValidator(IRelationTargetChecker relationTargets, IActorMembershipChecker actorMembers)
{
    /// <param name="schema">Property definitions of the collection.</param>
    /// <param name="properties">Values by key.</param>
    /// <param name="mode">Whether required properties must be present.</param>
    /// <param name="fieldPrefix">Prefix of the error fields, e.g. <c>propertiesPreset</c> → <c>propertiesPreset.status</c>; null for bare keys.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<IReadOnlyList<FieldError>> ValidateAsync(
        IReadOnlyList<PropertyDefinition> schema,
        IReadOnlyDictionary<string, JsonElement> properties,
        PropertyValidationMode mode,
        string? fieldPrefix,
        CancellationToken cancellationToken)
    {
        var errors = new FieldErrors();
        var references = Validate(schema, properties, mode, fieldPrefix, errors);

        await CheckRecordsAsync(references, errors, cancellationToken).ConfigureAwait(false);
        await CheckActorsAsync(references, errors, cancellationToken).ConfigureAwait(false);
        return errors.Items;
    }

    /// <summary>The checks that need no database: keys, presence, types, formats, options. Returns the references to look up.</summary>
    public static ValueReferences Validate(
        IReadOnlyList<PropertyDefinition> schema,
        IReadOnlyDictionary<string, JsonElement> properties,
        PropertyValidationMode mode,
        string? fieldPrefix,
        FieldErrors errors)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(errors);

        var references = new ValueReferences();
        var byKey = schema.ToDictionary(p => p.Key, StringComparer.Ordinal);
        IReadOnlyList<string> writable = [.. schema.Where(p => !p.IsComputed).Select(p => p.Key)];

        // Ordinal order of keys keeps error lists stable for callers and tests.
        foreach (var (key, value) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var field = Field(fieldPrefix, key);
            if (!byKey.TryGetValue(key, out var property))
            {
                errors.Add(field, ErrorCodes.UnknownProperty, $"The collection has no property '{key}'. Use one of the allowed keys.", writable);
                continue;
            }

            // Computed values are never written, not even cleared with null.
            if (property.IsComputed)
            {
                PropertyTypeRegistry.Get(property.Type).Check(property, value, field, errors, references);
                continue;
            }

            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (property.Required)
                {
                    errors.Add(field, ErrorCodes.Required, $"'{key}' is required and cannot be empty.");
                }

                continue;
            }

            // A required property needs real content: "" (after trimming) and [] count as missing.
            if (property.Required && !property.IsComputed && IsEmpty(value))
            {
                errors.Add(field, ErrorCodes.Required, $"'{key}' is required and cannot be empty: {PropertyTypeRegistry.Get(property.Type).Summary}.");
                continue;
            }

            PropertyTypeRegistry.Get(property.Type).Check(property, value, field, errors, references);
        }

        if (mode == PropertyValidationMode.Full)
        {
            foreach (var property in schema.Where(p => p.Required && !properties.ContainsKey(p.Key)))
            {
                errors.Add(Field(fieldPrefix, property.Key), ErrorCodes.Required, $"'{property.Key}' is required: {PropertyTypeRegistry.Get(property.Type).Summary}.");
            }
        }

        return references;
    }

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        // Text that cannot be read is not empty: the type check reports it (invalid_value).
        JsonValueKind.String => StorableText.TryRead(value, out var text) && string.IsNullOrWhiteSpace(text),
        JsonValueKind.Array => value.GetArrayLength() == 0,
        _ => false,
    };

    private static string Field(string? prefix, string key) => string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";

    private async Task CheckRecordsAsync(ValueReferences references, FieldErrors errors, CancellationToken cancellationToken)
    {
        foreach (var group in references.Records.GroupBy(r => r.TargetCollectionId))
        {
            var existing = await relationTargets
                .FindExistingAsync(group.Key, [.. group.Select(r => r.RecordId).Distinct()], cancellationToken)
                .ConfigureAwait(false);
            foreach (var (field, _, recordId) in group.Where(r => !existing.Contains(r.RecordId)))
            {
                errors.Add(field, ErrorCodes.NotFound, $"Record {recordId} does not exist in the target collection {group.Key} of this relation.");
            }
        }
    }

    private async Task CheckActorsAsync(ValueReferences references, FieldErrors errors, CancellationToken cancellationToken)
    {
        if (references.Actors.Count == 0)
        {
            return;
        }

        var members = await actorMembers
            .FindMembersAsync([.. references.Actors.Select(a => a.ActorId).Distinct()], cancellationToken)
            .ConfigureAwait(false);
        foreach (var (field, actorId) in references.Actors)
        {
            if (!members.TryGetValue(actorId, out var isActive))
            {
                errors.Add(field, ErrorCodes.NotFound, $"Actor {actorId} is not a member of this workspace. See GET /members for the members.");
            }
            else if (!isActive)
            {
                errors.Add(field, ErrorCodes.ActorInactive, $"Actor {actorId} is deactivated and cannot be assigned. Choose an active member.");
            }
        }
    }
}
