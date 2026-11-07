using System.Text.Json;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Views;

namespace Agcowork.Application.Views;

/// <summary>One sort criterion as sent by a caller.</summary>
/// <param name="Prop">Key of a schema property.</param>
/// <param name="Dir"><c>asc</c> or <c>desc</c>.</param>
public sealed record ViewSortInput(string? Prop, string? Dir);

/// <summary>The presentation parts of a view as sent by a caller; null parts are not checked.</summary>
public sealed record ViewDefinitionInput(
    string? Type,
    ViewFilterNode? Filter,
    IReadOnlyList<ViewSortInput>? Sorts,
    string? GroupBy,
    IReadOnlyList<string>? VisibleProperties,
    string? CardCoverProperty);

/// <summary>
/// Checks a view definition against the collection schema: the filter structurally (shape, operators, property keys; its
/// translation to SQL is a different concern), sorts, grouping, visible and cover properties. Errors name the field
/// (<c>filter.and[1].op</c>, <c>sorts[0].prop</c>) and list the <c>allowed</c> values.
/// </summary>
public static class ViewDefinitionValidator
{
    public const int MaxFilterDepth = 8;
    public const int MaxConditions = 100;
    public const int MaxSorts = 10;

    /// <summary>Checks every non-null part; returns the parsed type when <c>Type</c> is set and valid.</summary>
    public static ViewType? Validate(FieldErrors errors, IReadOnlyList<PropertyDefinition> schema, ViewDefinitionInput input)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(input);

        ViewType? type = null;
        if (input.Type is not null)
        {
            if (ViewTypes.TryParse(input.Type, out var parsed))
            {
                type = parsed;
            }
            else
            {
                errors.Add("type", ErrorCodes.NotInOptions, $"'{input.Type}' is not a supported view type.", ViewTypes.AllWireNames);
            }
        }

        var keys = new KeyCheck(errors, [.. schema.Select(p => p.Key)]);
        if (input.Filter is { IsEmpty: false } filter)
        {
            var conditions = 0;
            ValidateNode(keys, "filter", filter, 1, ref conditions);
        }

        ValidateSorts(keys, input.Sorts);
        if (!string.IsNullOrWhiteSpace(input.GroupBy) && input.GroupBy.Trim() is var groupBy && keys.Exists("groupBy", groupBy))
        {
            var groupable = schema.Where(p => p.Type is PropertyType.Select or PropertyType.Status).Select(p => p.Key).ToList();
            if (!groupable.Contains(groupBy, StringComparer.Ordinal))
            {
                errors.Add("groupBy", ErrorCodes.InvalidType, $"'{groupBy}' cannot group a view: only select and status properties can.", groupable);
            }
        }

        if (input.VisibleProperties is { } visible)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < visible.Count; i++)
            {
                if (keys.Exists($"visibleProperties[{i}]", visible[i]) && !seen.Add(visible[i]))
                {
                    errors.Add($"visibleProperties[{i}]", ErrorCodes.Duplicate, $"'{visible[i]}' is listed twice.");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(input.CardCoverProperty))
        {
            keys.Exists("cardCoverProperty", input.CardCoverProperty.Trim());
        }

        return type;
    }

    /// <summary>A board is grouped by a property; call when type or grouping change.</summary>
    public static void RequireGroupBy(FieldErrors errors, IReadOnlyList<PropertyDefinition> schema, ViewType type, string? effectiveGroupBy)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(schema);
        if (type == ViewType.Board && string.IsNullOrWhiteSpace(effectiveGroupBy))
        {
            errors.Add(
                "groupBy",
                ErrorCodes.Required,
                "A board view needs 'groupBy': the key of the select or status property whose values become the columns.",
                [.. schema.Where(p => p.Type is PropertyType.Select or PropertyType.Status).Select(p => p.Key)]);
        }
    }

    private static void ValidateSorts(KeyCheck keys, IReadOnlyList<ViewSortInput>? sorts)
    {
        if (sorts is null)
        {
            return;
        }

        if (sorts.Count > MaxSorts)
        {
            keys.Errors.Add("sorts", ErrorCodes.TooLong, $"A view has at most {MaxSorts} sort criteria.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sorts.Count; i++)
        {
            var sort = sorts[i];
            if (sort is null)
            {
                keys.Errors.Add($"sorts[{i}]", ErrorCodes.Required, "Sort criterion must be { prop, dir }.");
                continue;
            }

            if (keys.Exists($"sorts[{i}].prop", sort.Prop) && !seen.Add(sort.Prop!))
            {
                keys.Errors.Add($"sorts[{i}].prop", ErrorCodes.Duplicate, $"'{sort.Prop}' is sorted twice.");
            }

            if (string.IsNullOrWhiteSpace(sort.Dir))
            {
                keys.Errors.Add($"sorts[{i}].dir", ErrorCodes.Required, "'dir' is required: asc or desc.", ViewSort.Directions);
            }
            else
            {
                keys.Errors.OneOf($"sorts[{i}].dir", sort.Dir, ViewSort.Directions);
            }
        }
    }

    private static void ValidateNode(KeyCheck keys, string path, ViewFilterNode node, int depth, ref int conditions)
    {
        var errors = keys.Errors;
        if (depth > MaxFilterDepth)
        {
            errors.Add(path, ErrorCodes.TooLong, $"Filters nest at most {MaxFilterDepth} levels deep.");
            return;
        }

        if (node.Unknown is { Count: > 0 })
        {
            foreach (var key in node.Unknown.Keys)
            {
                errors.Add($"{path}.{key}", ErrorCodes.UnknownProperty, $"'{key}' is not part of the filter DSL. A filter node has only: and, or, prop, op, value.", ViewFilterNode.Keys);
            }

            return;
        }

        var isCondition = node.Prop is not null || node.Op is not null || node.Value is not null;
        var shapes = (node.And is not null ? 1 : 0) + (node.Or is not null ? 1 : 0) + (isCondition ? 1 : 0);
        if (shapes == 0)
        {
            errors.Add(path, ErrorCodes.Required, "A filter node is { \"and\": [ … ] }, { \"or\": [ … ] } or a condition { \"prop\", \"op\", \"value\" }.");
            return;
        }

        if (shapes > 1)
        {
            errors.Add(path, ErrorCodes.InvalidFormat, "A filter node is exactly one of 'and', 'or' or a condition (prop, op, value); nest groups instead of mixing them.");
            return;
        }

        if (isCondition)
        {
            ValidateCondition(keys, path, node, ref conditions);
            return;
        }

        var (name, children) = node.And is not null ? ("and", node.And) : ("or", node.Or!);
        if (children.Count == 0)
        {
            errors.Add($"{path}.{name}", ErrorCodes.Required, $"'{name}' needs at least one condition.");
            return;
        }

        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is null)
            {
                errors.Add($"{path}.{name}[{i}]", ErrorCodes.Required, "Filter node must not be null.");
                continue;
            }

            ValidateNode(keys, $"{path}.{name}[{i}]", children[i], depth + 1, ref conditions);
        }
    }

    private static void ValidateCondition(KeyCheck keys, string path, ViewFilterNode node, ref int conditions)
    {
        var errors = keys.Errors;
        if (++conditions == MaxConditions + 1)
        {
            errors.Add(path, ErrorCodes.TooLong, $"A filter has at most {MaxConditions} conditions.");
        }

        keys.Exists($"{path}.prop", node.Prop);

        if (string.IsNullOrWhiteSpace(node.Op))
        {
            errors.Add($"{path}.op", ErrorCodes.Required, "'op' is required.", ViewFilterOperators.All);
            return;
        }

        if (!errors.OneOf($"{path}.op", node.Op, ViewFilterOperators.All))
        {
            return;
        }

        if (ViewFilterOperators.TakesNoValue(node.Op))
        {
            if (node.Value is not null)
            {
                errors.Add($"{path}.value", ErrorCodes.NotAllowed, $"Operator '{node.Op}' takes no 'value'; remove it.");
            }

            return;
        }

        var value = node.Value;
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            errors.Add($"{path}.value", ErrorCodes.Required, $"Operator '{node.Op}' needs a 'value'. To test for missing values use is_empty / is_not_empty.");
        }
        else if (ViewFilterOperators.TakesList(node.Op) && value.Value.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.value", ErrorCodes.InvalidType, $"Operator '{node.Op}' needs a list of values, e.g. [\"todo\", \"done\"].");
        }
        else if (!ViewFilterOperators.TakesList(node.Op) && value.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            errors.Add($"{path}.value", ErrorCodes.InvalidType, $"Operator '{node.Op}' needs a single value (use in / nin for lists).");
        }
    }

    private sealed class KeyCheck(FieldErrors errors, IReadOnlyList<string> keys)
    {
        public FieldErrors Errors { get; } = errors;

        /// <summary>Adds an error unless <paramref name="key"/> is a property key of the schema. Returns true when it is.</summary>
        public bool Exists(string field, string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                Errors.Add(field, ErrorCodes.Required, $"'{field}' is required: a property key of the collection.", keys);
                return false;
            }

            if (!keys.Contains(key, StringComparer.Ordinal))
            {
                Errors.Add(field, ErrorCodes.UnknownProperty, $"The collection has no property '{key}'.", keys);
                return false;
            }

            return true;
        }
    }
}
