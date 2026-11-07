using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Views;

namespace Agcowork.Application.Query;

/// <summary>Operators of filter conditions; wire names in <see cref="ViewFilterOperators"/>.</summary>
public enum FilterOperator
{
    Eq,
    Neq,
    In,
    Nin,
    Contains,
    StartsWith,
    Gt,
    Gte,
    Lt,
    Lte,
    IsEmpty,
    IsNotEmpty,
}

/// <summary>How a field is compared; decides the operators (<see cref="OperatorMatrix"/>), the value type and the SQL translation.</summary>
public enum FilterValueType
{
    /// <summary>text, markdown, url, email and the title. Values: strings.</summary>
    Text,

    /// <summary>number. Values: decimals.</summary>
    Number,

    /// <summary>checkbox. Values: booleans.</summary>
    Checkbox,

    /// <summary>select and status. Values: option values (strings).</summary>
    Option,

    /// <summary>multi_select. Values: option values (strings).</summary>
    MultiOption,

    /// <summary>date. Values: <see cref="DateOnly"/>.</summary>
    Date,

    /// <summary>datetime and the computed created_at / updated_at. Values: <see cref="DateTimeOffset"/> (UTC).</summary>
    DateTime,

    /// <summary>relation. Values: record ids (<see cref="Guid"/>).</summary>
    Relation,

    /// <summary>actor and the computed created_by / updated_by. Values: actor ids (<see cref="Guid"/>).</summary>
    Actor,

    /// <summary>json: neither filterable nor sortable.</summary>
    Json,
}

/// <summary>Where the value of a field lives.</summary>
public enum QueryFieldSource
{
    /// <summary>A stored property: <c>properties-&gt;'key'</c>.</summary>
    Property,

    /// <summary>The title column.</summary>
    Title,

    /// <summary>The created_at column.</summary>
    CreatedAt,

    /// <summary>The updated_at column.</summary>
    UpdatedAt,

    /// <summary>The created_by column.</summary>
    CreatedBy,

    /// <summary>The updated_by column.</summary>
    UpdatedBy,
}

/// <summary>A field a filter or sort refers to: a property of the schema or a pseudo-field.</summary>
/// <param name="Key">Key as sent (schema key or pseudo-field name).</param>
/// <param name="Source">Where the value lives. Computed properties map to their column, never to <c>properties-&gt;&gt;'key'</c>.</param>
/// <param name="ValueType">How values are compared.</param>
/// <param name="Property">The schema property; null for pseudo-fields.</param>
public sealed record QueryField(string Key, QueryFieldSource Source, FilterValueType ValueType, PropertyDefinition? Property)
{
    /// <summary>Whether the field can be missing (stored properties); columns always have a value.</summary>
    public bool IsNullable => Source == QueryFieldSource.Property;
}

/// <summary>A node of the parsed filter tree.</summary>
public abstract record FilterNode;

/// <summary>All (<see cref="IsAnd"/>) or at least one of the children match.</summary>
public sealed record FilterGroup(bool IsAnd, IReadOnlyList<FilterNode> Children) : FilterNode;

/// <summary>The child does not match.</summary>
public sealed record FilterNot(FilterNode Child) : FilterNode;

/// <summary>
/// One condition. <paramref name="Values"/> are typed by <see cref="QueryField.ValueType"/> (string, decimal, bool,
/// DateOnly, DateTimeOffset, Guid): empty for is_empty / is_not_empty, one value for single-value operators, the list for in / nin.
/// </summary>
public sealed record FilterCondition(QueryField Field, FilterOperator Operator, IReadOnlyList<object> Values) : FilterNode
{
    public object Value => Values[0];
}

/// <summary>One sort criterion of a parsed query.</summary>
public sealed record QuerySort(QueryField Field, bool Descending)
{
    /// <summary>Stable text form, part of the cursor (a cursor only fits the sort order it came from).</summary>
    public string Signature => $"{Field.Key}:{(Descending ? ViewSort.Desc : ViewSort.Asc)}";
}

/// <summary>
/// The binding operator matrix of the filter DSL (architecture §3): which operators apply to which value type, and which
/// fields can be sorted. The parser is the only place that applies it.
/// </summary>
public static class OperatorMatrix
{
    private static readonly (FilterOperator Operator, string Wire)[] Names =
    [
        (FilterOperator.Eq, ViewFilterOperators.Eq),
        (FilterOperator.Neq, ViewFilterOperators.Neq),
        (FilterOperator.In, ViewFilterOperators.In),
        (FilterOperator.Nin, ViewFilterOperators.Nin),
        (FilterOperator.Contains, ViewFilterOperators.Contains),
        (FilterOperator.StartsWith, ViewFilterOperators.StartsWith),
        (FilterOperator.Gt, ViewFilterOperators.Gt),
        (FilterOperator.Gte, ViewFilterOperators.Gte),
        (FilterOperator.Lt, ViewFilterOperators.Lt),
        (FilterOperator.Lte, ViewFilterOperators.Lte),
        (FilterOperator.IsEmpty, ViewFilterOperators.IsEmpty),
        (FilterOperator.IsNotEmpty, ViewFilterOperators.IsNotEmpty),
    ];

    private static readonly Dictionary<FilterValueType, FilterOperator[]> Table = new()
    {
        [FilterValueType.Text] = [FilterOperator.Eq, FilterOperator.Neq, FilterOperator.Contains, FilterOperator.StartsWith, FilterOperator.In, FilterOperator.Nin, FilterOperator.IsEmpty, FilterOperator.IsNotEmpty],
        [FilterValueType.Number] = [FilterOperator.Eq, FilterOperator.Neq, FilterOperator.Gt, FilterOperator.Gte, FilterOperator.Lt, FilterOperator.Lte, FilterOperator.In, FilterOperator.Nin, FilterOperator.IsEmpty, FilterOperator.IsNotEmpty],
        [FilterValueType.Checkbox] = [FilterOperator.Eq, FilterOperator.Neq, FilterOperator.IsEmpty],
        [FilterValueType.Option] = [FilterOperator.Eq, FilterOperator.Neq, FilterOperator.In, FilterOperator.Nin, FilterOperator.IsEmpty, FilterOperator.IsNotEmpty],
        [FilterValueType.MultiOption] = [FilterOperator.Contains, FilterOperator.In, FilterOperator.Nin, FilterOperator.IsEmpty, FilterOperator.IsNotEmpty],
        [FilterValueType.Date] = [FilterOperator.Eq, FilterOperator.Neq, FilterOperator.Gt, FilterOperator.Gte, FilterOperator.Lt, FilterOperator.Lte, FilterOperator.IsEmpty, FilterOperator.IsNotEmpty],
        [FilterValueType.DateTime] = [FilterOperator.Eq, FilterOperator.Neq, FilterOperator.Gt, FilterOperator.Gte, FilterOperator.Lt, FilterOperator.Lte, FilterOperator.IsEmpty, FilterOperator.IsNotEmpty],
        [FilterValueType.Relation] = [FilterOperator.Contains, FilterOperator.In, FilterOperator.Nin, FilterOperator.IsEmpty, FilterOperator.IsNotEmpty],
        [FilterValueType.Actor] = [FilterOperator.Eq, FilterOperator.Neq, FilterOperator.In, FilterOperator.Nin, FilterOperator.IsEmpty, FilterOperator.IsNotEmpty],
        [FilterValueType.Json] = [],
    };

    /// <summary>Wire names of all operators.</summary>
    public static IReadOnlyList<string> AllWireNames => ViewFilterOperators.All;

    public static string ToWire(FilterOperator op) => Names.First(n => n.Operator == op).Wire;

    public static bool TryParse(string? wire, out FilterOperator op)
    {
        foreach (var (candidate, name) in Names)
        {
            if (string.Equals(name, wire, StringComparison.Ordinal))
            {
                op = candidate;
                return true;
            }
        }

        op = default;
        return false;
    }

    public static IReadOnlyList<FilterOperator> OperatorsFor(FilterValueType type) => Table[type];

    /// <summary>Wire names of the operators of <paramref name="type"/>, in matrix order (the <c>allowed</c> of <c>operator_not_applicable</c>).</summary>
    public static IReadOnlyList<string> WireNamesFor(FilterValueType type) => [.. Table[type].Select(ToWire)];

    public static bool Applies(FilterValueType type, FilterOperator op) => Table[type].Contains(op);

    /// <summary>Multi-valued and free-form fields have no order.</summary>
    public static bool IsSortable(FilterValueType type) => type is not (FilterValueType.MultiOption or FilterValueType.Relation or FilterValueType.Json);

    /// <summary>Operators that take a list value.</summary>
    public static bool TakesList(FilterOperator op) => op is FilterOperator.In or FilterOperator.Nin;

    /// <summary>Operators that take no value.</summary>
    public static bool TakesNoValue(FilterOperator op) => op is FilterOperator.IsEmpty or FilterOperator.IsNotEmpty;

    /// <summary>The value type of a property type.</summary>
    public static FilterValueType ValueTypeOf(PropertyType type) => type switch
    {
        PropertyType.Text or PropertyType.Markdown or PropertyType.Url or PropertyType.Email => FilterValueType.Text,
        PropertyType.Number => FilterValueType.Number,
        PropertyType.Checkbox => FilterValueType.Checkbox,
        PropertyType.Select or PropertyType.Status => FilterValueType.Option,
        PropertyType.MultiSelect => FilterValueType.MultiOption,
        PropertyType.Date => FilterValueType.Date,
        PropertyType.DateTime or PropertyType.CreatedAt or PropertyType.UpdatedAt => FilterValueType.DateTime,
        PropertyType.Relation => FilterValueType.Relation,
        PropertyType.Actor or PropertyType.CreatedBy or PropertyType.UpdatedBy => FilterValueType.Actor,
        _ => FilterValueType.Json,
    };
}
