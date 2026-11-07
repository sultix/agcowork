using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Query;
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
/// Checks a view definition against the collection schema: filter and sorts through <see cref="QueryParser"/> (the same
/// rules as a query: operator matrix, typed values, option values), grouping, visible and cover properties. Errors name the
/// field (<c>filter.and[1].op</c>, <c>sorts[0].prop</c>) and list the <c>allowed</c> values.
/// </summary>
public static class ViewDefinitionValidator
{
    public const int MaxFilterDepth = QueryParser.MaxFilterDepth;
    public const int MaxConditions = QueryParser.MaxConditions;
    public const int MaxSorts = QueryParser.MaxSorts;

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

        // Filter and sorts are what a query understands: the parser is the one truth (types, operators, values).
        QueryParser.ParseFilter(errors, "filter", schema, input.Filter);
        QueryParser.ParseSorts(errors, "sorts", schema, input.Sorts);
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
