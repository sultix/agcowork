using System.Globalization;
using System.Text.Json;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Application.Views;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Views;

namespace Agcowork.Application.Query;

/// <summary>
/// The one place that understands the filter DSL (architecture §3): turns the wire form (<see cref="ViewFilterNode"/>,
/// <see cref="QueryDefinition"/>) into a typed, schema-checked <see cref="RecordQuery"/>. REST, MCP and stored views
/// (<see cref="ViewDefinitionValidator"/>) all go through it. Errors name the field (<c>filter.and[1].op</c>) and carry
/// <c>allowed</c> where there is a closed set: <c>unknown_property</c>, <c>unknown_operator</c>,
/// <c>operator_not_applicable</c> (operators of the field's type), <c>not_in_options</c> (option values),
/// <c>invalid_value</c> (message names the expected type), <c>not_sortable</c>, <c>invalid_cursor</c>.
/// </summary>
public static class QueryParser
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int MaxFilterDepth = 8;
    public const int MaxConditions = 100;
    public const int MaxSorts = 10;
    public const int MaxListValues = 100;

    /// <summary>Conditions of a view filter and an additional request filter together (<c>POST /views/{id}/query</c>).</summary>
    public const int MaxCombinedConditions = 150;

    /// <summary>Upper bound of <c>includeCount</c>.</summary>
    public const int MaxCount = 10_000;

    /// <summary>Field name of <see cref="QueryDefinition.Fields"/> that adds the body.</summary>
    public const string BodyField = "body";

    /// <summary>Fields every record has besides its schema properties; a schema property with the same key wins.</summary>
    public static IReadOnlyList<string> PseudoFields { get; } = ["title", "created_at", "updated_at", "created_by", "updated_by"];

    /// <summary>Prefix of actor values in the alternative form <c>actor:&lt;id&gt;</c>.</summary>
    public const string ActorPrefix = "actor:";

    /// <summary>Parses a whole query; null when <paramref name="errors"/> got entries.</summary>
    public static RecordQuery? Parse(FieldErrors errors, IReadOnlyList<PropertyDefinition> schema, QueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(definition);

        var before = errors.Items.Count;
        RejectUnknownMembers(errors, definition.Unknown?.Keys, QueryDefinition.Keys);
        var filter = ParseFilter(errors, "filter", schema, definition.Filter);
        var sorts = ParseSorts(errors, "sorts", schema, definition.Sorts);
        var fields = ParseFields(errors, "fields", schema, definition.Fields);
        var limit = ParseLimit(errors, definition.Limit);
        var after = errors.Items.Count == before ? ParseCursor(errors, definition.Cursor, sorts) : null;
        return errors.Items.Count == before
            ? new RecordQuery(filter, sorts, limit, after, fields, definition.IncludeArchived, definition.IncludeCount)
            : null;
    }

    /// <summary>Adds <c>unknown_property</c> (with the accepted members as <c>allowed</c>) for every member of a request body that is not part of it.</summary>
    public static void RejectUnknownMembers(FieldErrors errors, IEnumerable<string>? unknown, IReadOnlyList<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(allowed);
        foreach (var key in unknown ?? [])
        {
            errors.Add(key, ErrorCodes.UnknownProperty, $"'{key}' is not part of the request. Accepted: {string.Join(", ", allowed)}.", allowed);
        }
    }

    /// <summary>Number of conditions of a parsed filter.</summary>
    public static int CountConditions(FilterNode? node) => node switch
    {
        null => 0,
        FilterCondition => 1,
        FilterNot not => CountConditions(not.Child),
        FilterGroup group => group.Children.Sum(CountConditions),
        _ => 0,
    };

    /// <summary>The field a key refers to: a schema property (computed ones map to their column) or a pseudo-field; null if unknown.</summary>
    public static QueryField? ResolveField(IReadOnlyList<PropertyDefinition> schema, string key)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Find(key) is { } property)
        {
            return property.Type switch
            {
                PropertyType.CreatedAt => new QueryField(key, QueryFieldSource.CreatedAt, FilterValueType.DateTime, property),
                PropertyType.UpdatedAt => new QueryField(key, QueryFieldSource.UpdatedAt, FilterValueType.DateTime, property),
                PropertyType.CreatedBy => new QueryField(key, QueryFieldSource.CreatedBy, FilterValueType.Actor, property),
                PropertyType.UpdatedBy => new QueryField(key, QueryFieldSource.UpdatedBy, FilterValueType.Actor, property),
                _ when key == CollectionSchema.TitleKey => new QueryField(key, QueryFieldSource.Title, FilterValueType.Text, property),
                _ => new QueryField(key, QueryFieldSource.Property, OperatorMatrix.ValueTypeOf(property.Type), property),
            };
        }

        return key switch
        {
            "title" => new QueryField(key, QueryFieldSource.Title, FilterValueType.Text, null),
            "created_at" => new QueryField(key, QueryFieldSource.CreatedAt, FilterValueType.DateTime, null),
            "updated_at" => new QueryField(key, QueryFieldSource.UpdatedAt, FilterValueType.DateTime, null),
            "created_by" => new QueryField(key, QueryFieldSource.CreatedBy, FilterValueType.Actor, null),
            "updated_by" => new QueryField(key, QueryFieldSource.UpdatedBy, FilterValueType.Actor, null),
            _ => null,
        };
    }

    /// <summary>Keys filters and sorts may refer to: the schema keys, then the pseudo-fields the schema does not shadow.</summary>
    public static IReadOnlyList<string> FieldKeys(IReadOnlyList<PropertyDefinition> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var keys = schema.Select(p => p.Key).ToList();
        keys.AddRange(PseudoFields.Where(p => !keys.Contains(p, StringComparer.Ordinal)));
        return keys;
    }

    /// <summary>Parses a filter tree below <paramref name="path"/>; null for no filter (null or <c>{}</c>) and on errors.</summary>
    public static FilterNode? ParseFilter(FieldErrors errors, string path, IReadOnlyList<PropertyDefinition> schema, ViewFilterNode? node)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(schema);
        if (node is null || node.IsEmpty)
        {
            return null;
        }

        var before = errors.Items.Count;
        var context = new FilterContext(errors, schema, FieldKeys(schema));
        var parsed = ParseNode(context, path, node, 1);
        return errors.Items.Count == before ? parsed : null;
    }

    /// <summary>Parses sort criteria; errors on unknown, duplicate or unsortable fields and on missing/unknown directions.</summary>
    public static IReadOnlyList<QuerySort> ParseSorts(FieldErrors errors, string path, IReadOnlyList<PropertyDefinition> schema, IReadOnlyList<ViewSortInput>? sorts)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(schema);
        if (sorts is null)
        {
            return [];
        }

        if (sorts.Count > MaxSorts)
        {
            errors.Add(path, ErrorCodes.TooLong, $"At most {MaxSorts} sort criteria.");
        }

        var keys = FieldKeys(schema);
        var result = new List<QuerySort>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sorts.Count; i++)
        {
            var sort = sorts[i];
            var field = $"{path}[{i}]";
            if (sort is null)
            {
                errors.Add(field, ErrorCodes.Required, "Sort criterion must be { prop, dir }.");
                continue;
            }

            var resolved = Resolve(errors, $"{field}.prop", schema, keys, sort.Prop);
            if (resolved is not null && !seen.Add(resolved.Key))
            {
                errors.Add($"{field}.prop", ErrorCodes.Duplicate, $"'{resolved.Key}' is sorted twice.");
                resolved = null;
            }
            else if (resolved is not null && !OperatorMatrix.IsSortable(resolved.ValueType))
            {
                errors.Add(
                    $"{field}.prop",
                    ErrorCodes.NotSortable,
                    $"'{resolved.Key}' ({TypeName(resolved)}) has no order: multi_select, relation and json properties cannot be sorted.",
                    [.. keys.Where(k => ResolveField(schema, k) is { } f && OperatorMatrix.IsSortable(f.ValueType))]);
                resolved = null;
            }

            bool? descending = null;
            if (string.IsNullOrWhiteSpace(sort.Dir))
            {
                errors.Add($"{field}.dir", ErrorCodes.Required, "'dir' is required: asc or desc.", ViewSort.Directions);
            }
            else if (errors.OneOf($"{field}.dir", sort.Dir, ViewSort.Directions))
            {
                descending = sort.Dir == ViewSort.Desc;
            }

            if (resolved is not null && descending is { } desc)
            {
                result.Add(new QuerySort(resolved, desc));
            }
        }

        return result;
    }

    /// <summary>The requested fields (null = all); every entry is a schema key or <c>body</c>.</summary>
    public static IReadOnlyList<string>? ParseFields(FieldErrors errors, string path, IReadOnlyList<PropertyDefinition> schema, IReadOnlyList<string>? fields)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(schema);
        if (fields is null)
        {
            return null;
        }

        IReadOnlyList<string> allowed = [.. schema.Select(p => p.Key).Append(BodyField)];
        var result = new List<string>();
        for (var i = 0; i < fields.Count; i++)
        {
            var key = fields[i];
            if (string.IsNullOrWhiteSpace(key))
            {
                errors.Add($"{path}[{i}]", ErrorCodes.Required, "A field is a property key of the collection or 'body'.", allowed);
            }
            else if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                errors.Add($"{path}[{i}]", ErrorCodes.UnknownProperty, $"The collection has no property '{key}'.", allowed);
            }
            else if (!result.Contains(key, StringComparer.Ordinal))
            {
                result.Add(key);
            }
        }

        return result;
    }

    /// <summary>The effective page size; <c>out_of_range</c> outside 1..<see cref="MaxLimit"/>.</summary>
    public static int ParseLimit(FieldErrors errors, int? limit)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (limit is { } value && (value < 1 || value > MaxLimit))
        {
            errors.Add("limit", ErrorCodes.OutOfRange, $"'limit' must be between 1 and {MaxLimit}.");
            return DefaultLimit;
        }

        return limit ?? DefaultLimit;
    }

    /// <summary>The position of a cursor; <c>invalid_cursor</c> when it is not a cursor of this sort order.</summary>
    public static QueryCursor? ParseCursor(FieldErrors errors, string? cursor, IReadOnlyList<QuerySort> sorts)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        var position = QueryCursor.TryDecode(cursor, sorts);
        if (position is null)
        {
            errors.Add(
                "cursor",
                ErrorCodes.InvalidCursor,
                "'cursor' is not valid for this query. Pass 'nextCursor' of the previous page unchanged with the same sorts, or omit it for the first page.");
        }

        return position;
    }

    private sealed record FilterContext(FieldErrors Errors, IReadOnlyList<PropertyDefinition> Schema, IReadOnlyList<string> Keys)
    {
        public int Conditions { get; set; }
    }

    private static FilterNode? ParseNode(FilterContext context, string path, ViewFilterNode node, int depth)
    {
        var errors = context.Errors;
        if (depth > MaxFilterDepth)
        {
            errors.Add(path, ErrorCodes.TooLong, $"Filters nest at most {MaxFilterDepth} levels deep.");
            return null;
        }

        if (node.Unknown is { Count: > 0 })
        {
            foreach (var key in node.Unknown.Keys)
            {
                errors.Add($"{path}.{key}", ErrorCodes.UnknownProperty, $"'{key}' is not part of the filter DSL. A filter node has only: and, or, not, prop, op, value.", ViewFilterNode.Keys);
            }

            return null;
        }

        var isCondition = node.Prop is not null || node.Op is not null || node.Value is not null;
        var shapes = (node.And is not null ? 1 : 0) + (node.Or is not null ? 1 : 0) + (node.Not is not null ? 1 : 0) + (isCondition ? 1 : 0);
        if (shapes == 0)
        {
            errors.Add(path, ErrorCodes.Required, "A filter node is { \"and\": [ … ] }, { \"or\": [ … ] }, { \"not\": { … } } or a condition { \"prop\", \"op\", \"value\" }.");
            return null;
        }

        if (shapes > 1)
        {
            errors.Add(path, ErrorCodes.InvalidFormat, "A filter node is exactly one of 'and', 'or', 'not' or a condition (prop, op, value); nest groups instead of mixing them.");
            return null;
        }

        if (isCondition)
        {
            return ParseCondition(context, path, node);
        }

        if (node.Not is { } negated)
        {
            return ParseNode(context, $"{path}.not", negated, depth + 1) is { } child ? new FilterNot(child) : null;
        }

        var (name, children) = node.And is not null ? ("and", node.And) : ("or", node.Or!);
        if (children.Count == 0)
        {
            errors.Add($"{path}.{name}", ErrorCodes.Required, $"'{name}' needs at least one condition.");
            return null;
        }

        var parsed = new List<FilterNode>(children.Count);
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is null)
            {
                errors.Add($"{path}.{name}[{i}]", ErrorCodes.Required, "Filter node must not be null.");
                continue;
            }

            if (ParseNode(context, $"{path}.{name}[{i}]", children[i], depth + 1) is { } child)
            {
                parsed.Add(child);
            }
        }

        return parsed.Count == children.Count ? new FilterGroup(name == "and", parsed) : null;
    }

    private static FilterCondition? ParseCondition(FilterContext context, string path, ViewFilterNode node)
    {
        var errors = context.Errors;
        if (++context.Conditions == MaxConditions + 1)
        {
            errors.Add(path, ErrorCodes.TooLong, $"A filter has at most {MaxConditions} conditions.");
        }

        var field = Resolve(errors, $"{path}.prop", context.Schema, context.Keys, node.Prop);

        if (string.IsNullOrWhiteSpace(node.Op))
        {
            errors.Add($"{path}.op", ErrorCodes.Required, "'op' is required.", field is null ? OperatorMatrix.AllWireNames : OperatorMatrix.WireNamesFor(field.ValueType));
            return null;
        }

        if (!OperatorMatrix.TryParse(node.Op, out var op))
        {
            errors.Add(
                $"{path}.op",
                ErrorCodes.UnknownOperator,
                $"'{node.Op}' is not an operator of the filter DSL.",
                field is null ? OperatorMatrix.AllWireNames : OperatorMatrix.WireNamesFor(field.ValueType));
            return null;
        }

        if (field is not null && !OperatorMatrix.Applies(field.ValueType, op))
        {
            var allowed = OperatorMatrix.WireNamesFor(field.ValueType);
            errors.Add(
                $"{path}.op",
                ErrorCodes.OperatorNotApplicable,
                allowed.Count == 0
                    ? $"'{field.Key}' ({TypeName(field)}) cannot be filtered."
                    : $"'{node.Op}' does not apply to '{field.Key}' ({TypeName(field)}). Use one of: {string.Join(", ", allowed)}.",
                allowed);
            return null;
        }

        var valuePath = $"{path}.value";
        if (OperatorMatrix.TakesNoValue(op))
        {
            if (node.Value is not null)
            {
                errors.Add(valuePath, ErrorCodes.NotAllowed, $"Operator '{node.Op}' takes no 'value'; remove it.");
                return null;
            }

            return field is null ? null : new FilterCondition(field, op, []);
        }

        if (node.Value is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value)
        {
            errors.Add(valuePath, ErrorCodes.Required, $"Operator '{node.Op}' needs a 'value'. To test for missing values use is_empty / is_not_empty.");
            return null;
        }

        if (OperatorMatrix.TakesList(op))
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                errors.Add(valuePath, ErrorCodes.InvalidValue, $"Operator '{node.Op}' needs a list of values, e.g. [\"todo\", \"done\"].");
                return null;
            }

            var count = value.GetArrayLength();
            if (count == 0)
            {
                errors.Add(valuePath, ErrorCodes.Required, $"Operator '{node.Op}' needs at least one value in the list.");
                return null;
            }

            if (count > MaxListValues)
            {
                errors.Add(valuePath, ErrorCodes.TooLong, $"Operator '{node.Op}' takes at most {MaxListValues} values.");
                return null;
            }

            if (field is null)
            {
                return null;
            }

            var values = new List<object>(count);
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (ParseValue(errors, $"{valuePath}[{index++}]", field, op, item) is { } parsed)
                {
                    values.Add(parsed);
                }
            }

            return values.Count == count ? new FilterCondition(field, op, values) : null;
        }

        if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            errors.Add(valuePath, ErrorCodes.InvalidValue, $"Operator '{node.Op}' needs a single value (use in / nin for lists).");
            return null;
        }

        return field is not null && ParseValue(errors, valuePath, field, op, value) is { } single ? new FilterCondition(field, op, [single]) : null;
    }

    private static QueryField? Resolve(FieldErrors errors, string path, IReadOnlyList<PropertyDefinition> schema, IReadOnlyList<string> keys, string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            errors.Add(path, ErrorCodes.Required, $"'{path}' is required: a property key of the collection.", keys);
            return null;
        }

        var field = ResolveField(schema, key);
        if (field is null)
        {
            errors.Add(path, ErrorCodes.UnknownProperty, $"The collection has no property '{key}'.", keys);
        }

        return field;
    }

    /// <summary>One typed value for <paramref name="field"/>, or null after adding an error.</summary>
    private static object? ParseValue(FieldErrors errors, string path, QueryField field, FilterOperator op, JsonElement value)
    {
        var str = string.Empty;
        if (value.ValueKind == JsonValueKind.String && !QueryText.TryRead(value, out str))
        {
            errors.Add(path, ErrorCodes.InvalidValue, $"'{path}' must not contain NUL characters (\\u0000) or unpaired surrogates (\\ud800–\\udfff).");
            return null;
        }

        switch (field.ValueType)
        {
            case FilterValueType.Text:
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = str;
                    if (text.Length <= PropertyTypeRegistry.TextMaxLength)
                    {
                        // Stored text is trimmed by the normalizer (Markdown is not); exact comparisons use the same form.
                        return op is FilterOperator.Contains or FilterOperator.StartsWith || field.Property?.Type == PropertyType.Markdown ? text : text.Trim();
                    }

                    errors.Add(path, ErrorCodes.TooLong, $"'{path}' must not exceed {PropertyTypeRegistry.TextMaxLength} characters.");
                    return null;
                }

                break;
            case FilterValueType.Number:
                if (value.ValueKind == JsonValueKind.Number)
                {
                    if (value.TryGetDecimal(out var number))
                    {
                        return number;
                    }

                    errors.Add(path, ErrorCodes.InvalidValue, $"'{path}' is too large or too precise for a number property.");
                    return null;
                }

                break;
            case FilterValueType.Checkbox:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return value.GetBoolean();
                }

                break;
            case FilterValueType.Option or FilterValueType.MultiOption:
                if (value.ValueKind == JsonValueKind.String)
                {
                    var option = str.Trim();
                    IReadOnlyList<string> options = [.. (field.Property?.Options ?? []).Select(o => o.Value)];
                    if (options.Contains(option, StringComparer.Ordinal))
                    {
                        return option;
                    }

                    errors.Add(path, ErrorCodes.NotInOptions, $"'{option}' is not an option of '{field.Key}' (any more). Use one of the allowed values.", options);
                    return null;
                }

                break;
            case FilterValueType.Date:
                if (value.ValueKind == JsonValueKind.String
                    && DateOnly.TryParseExact(str.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return date;
                }

                break;
            case FilterValueType.DateTime:
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = str.Trim();
                    if (PropertyTypeRegistry.TryParseDateTime(text, out var time))
                    {
                        return time.ToUniversalTime();
                    }

                    // A plain date means its start in UTC, e.g. created_at gte 2026-11-01; for eq/neq that would almost never match.
                    if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    {
                        if (op is FilterOperator.Eq or FilterOperator.Neq)
                        {
                            errors.Add(
                                path,
                                ErrorCodes.InvalidValue,
                                $"'{path}': a plain date is only allowed with gt, gte, lt, lte on '{field.Key}' ({TypeName(field)}). For one day use gte '{text}' and lt the next day, or send a full date-time with offset.");
                            return null;
                        }

                        return new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                    }
                }

                break;
            case FilterValueType.Relation:
                if (value.ValueKind == JsonValueKind.String && Guid.TryParse(str.Trim(), out var recordId))
                {
                    return recordId;
                }

                break;
            case FilterValueType.Actor:
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = str.Trim();
                    if (text.StartsWith(ActorPrefix, StringComparison.Ordinal))
                    {
                        text = text[ActorPrefix.Length..];
                    }

                    if (Guid.TryParse(text, out var actorId))
                    {
                        return actorId;
                    }
                }

                break;
        }

        errors.Add(path, ErrorCodes.InvalidValue, $"'{path}' must be {Expected(field.ValueType)} for '{field.Key}' ({TypeName(field)}).");
        return null;
    }

    private static string Expected(FilterValueType type) => type switch
    {
        FilterValueType.Text => "a string",
        FilterValueType.Number => "a JSON number, e.g. 3 or 2.5",
        FilterValueType.Checkbox => "true or false",
        FilterValueType.Option or FilterValueType.MultiOption => "an option value (string)",
        FilterValueType.Date => "an ISO 8601 date 'YYYY-MM-DD', e.g. '2026-11-07'",
        FilterValueType.DateTime => "an ISO 8601 date-time with offset, e.g. '2026-11-07T14:30:00Z', or a date 'YYYY-MM-DD' (start of the day, UTC)",
        FilterValueType.Relation => $"a record id (UUID string), e.g. '{PropertyTypeRegistry.ExampleId}'",
        FilterValueType.Actor => $"an actor id (UUID string or 'actor:<id>'), e.g. '{PropertyTypeRegistry.ExampleId}'",
        _ => "a value",
    };

    private static string TypeName(QueryField field) =>
        field.Property is { } property ? PropertyTypes.ToWire(property.Type) : field.Key;
}
