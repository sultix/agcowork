using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Agcowork.Application.Query;
using Agcowork.Domain.Records;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Infrastructure.Query;

/// <summary>
/// Translates parsed queries (<see cref="RecordQuery"/>) into LINQ over <see cref="Record"/>s, which EF Core turns into SQL:
/// <list type="bullet">
/// <item>eq/in on text, select/status, checkbox and actor, contains/in on multi_select and relation: JSONB containment
/// <c>properties @&gt; '{"key": value}'</c>, which the GIN index <c>(workspace_id, properties jsonb_path_ops)</c> serves;</item>
/// <item>comparisons on number, date and datetime: <c>jsonb_extract_path_text(properties, key)</c> cast to numeric / date /
/// timestamptz (guarded by <c>jsonb_typeof</c>);</item>
/// <item>contains/starts_with on text: <c>ILIKE</c> with escaped wildcards (case-insensitive);</item>
/// <item>title and computed fields (created_at, updated_at, created_by, updated_by, also under a schema key): their columns.</item>
/// </list>
/// Every value and key is a parameter (members of captured objects), never SQL text. Every condition is NULL-free (missing
/// values compare as false), so <c>not</c> is the exact complement. Composes on the given query, so the tenant filter stays active.
/// </summary>
internal sealed class SqlQueryTranslator : IRecordQueryTranslator
{
    private static readonly ParameterExpression Row = Expression.Parameter(typeof(Record), "r");
    private static readonly Expression PropertiesColumn = Expression.Convert(Expression.Property(Row, nameof(Record.Properties)), typeof(object));
    private static readonly Expression True = Expression.Constant(true);
    private static readonly Expression False = Expression.Constant(false);
    private const string LikeEscape = "\\";

    private static readonly MethodInfo JsonContainsMethod = typeof(NpgsqlJsonDbFunctionsExtensions)
        .GetMethod(nameof(NpgsqlJsonDbFunctionsExtensions.JsonContains), [typeof(DbFunctions), typeof(object), typeof(object)])!;

    private static readonly MethodInfo ILikeMethod = typeof(NpgsqlDbFunctionsExtensions)
        .GetMethod(nameof(NpgsqlDbFunctionsExtensions.ILike), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    private static readonly MethodInfo StringCompare = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;

    public IQueryable<Record> Filter(IQueryable<Record> records, FilterNode filter)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(filter);
        return records.Where(Expression.Lambda<Func<Record, bool>>(Node(filter), Row));
    }

    public IQueryable<Record> Order(IQueryable<Record> records, IReadOnlyList<QuerySort> sorts)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sorts);
        IOrderedQueryable<Record>? ordered = null;
        foreach (var sort in sorts)
        {
            var key = Key(sort.Field);
            if (sort.Field.IsNullable)
            {
                // Empty values last in both directions: CASE WHEN key IS NULL THEN 1 ELSE 0 END first.
                ordered = OrderBy(ordered ?? records, Expression.Condition(IsNull(key), Expression.Constant(1), Expression.Constant(0)), descending: false, ordered is not null);
            }

            ordered = OrderBy(ordered ?? records, key, sort.Descending, ordered is not null);
        }

        ordered = OrderBy(ordered ?? records, Expression.Property(Row, nameof(Record.CreatedAt)), descending: false, ordered is not null);
        return OrderBy(ordered, Expression.Property(Row, nameof(Record.Id)), descending: false, thenBy: true);
    }

    public IQueryable<Record> After(IQueryable<Record> records, IReadOnlyList<QuerySort> sorts, QueryCursor position)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sorts);
        ArgumentNullException.ThrowIfNull(position);

        // Keyset: (k1 after c1) or (k1 = c1 and k2 after c2) or … or (all equal and (created_at, id) after the cursor's).
        Expression? after = null;
        Expression equalSoFar = True;
        for (var i = 0; i < sorts.Count; i++)
        {
            var key = Key(sorts[i].Field);
            var value = position.SortValues[i];
            Expression? keyAfter;
            Expression keyEqual;
            if (value is null)
            {
                // The cursor is in the trailing group of empty values: nothing after it on this key.
                keyAfter = null;
                keyEqual = IsNull(key);
            }
            else
            {
                var beyond = Compare(key, sorts[i].Descending ? ExpressionType.LessThan : ExpressionType.GreaterThan, value);
                keyAfter = sorts[i].Field.IsNullable ? Expression.OrElse(IsNull(key), beyond) : beyond;
                keyEqual = Compare(key, ExpressionType.Equal, value);
            }

            if (keyAfter is not null)
            {
                after = Or(after, And(equalSoFar, keyAfter));
            }

            equalSoFar = And(equalSoFar, keyEqual);
        }

        var createdAt = Expression.Property(Row, nameof(Record.CreatedAt));
        var id = Expression.Property(Row, nameof(Record.Id));
        var tieAfter = Expression.OrElse(
            Compare(createdAt, ExpressionType.GreaterThan, position.CreatedAt),
            Expression.AndAlso(Compare(createdAt, ExpressionType.Equal, position.CreatedAt), Compare(id, ExpressionType.GreaterThan, position.Id)));
        after = Or(after, And(equalSoFar, tieAfter));
        return records.Where(Expression.Lambda<Func<Record, bool>>(after, Row));
    }

    public IQueryable<RecordRow> Project(IQueryable<Record> records, IReadOnlyList<QuerySort> sorts, bool includeBody)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(sorts);
        Expression Column(string name) => Expression.Property(Row, name);
        var bindings = new List<MemberBinding>
        {
            Bind(nameof(RecordRow.Id), Column(nameof(Record.Id))),
            Bind(nameof(RecordRow.SpaceId), Column(nameof(Record.SpaceId))),
            Bind(nameof(RecordRow.CollectionId), Column(nameof(Record.CollectionId))),
            Bind(nameof(RecordRow.ParentRecordId), Column(nameof(Record.ParentRecordId))),
            Bind(nameof(RecordRow.Title), Column(nameof(Record.Title))),
            Bind(nameof(RecordRow.Properties), Column(nameof(Record.Properties))),
            Bind(nameof(RecordRow.RevisionNo), Column(nameof(Record.RevisionNo))),
            Bind(nameof(RecordRow.CreatedBy), Column(nameof(Record.CreatedBy))),
            Bind(nameof(RecordRow.CreatedAt), Column(nameof(Record.CreatedAt))),
            Bind(nameof(RecordRow.UpdatedBy), Column(nameof(Record.UpdatedBy))),
            Bind(nameof(RecordRow.UpdatedAt), Column(nameof(Record.UpdatedAt))),
            Bind(nameof(RecordRow.ArchivedAt), Column(nameof(Record.ArchivedAt))),
            Bind(nameof(RecordRow.SortValues), Expression.NewArrayInit(typeof(object), sorts.Select(s => Expression.Convert(Key(s.Field), typeof(object))))),
        };

        // Bodies are large: only read the column when asked for.
        if (includeBody)
        {
            bindings.Add(Bind(nameof(RecordRow.Body), Column(nameof(Record.Body))));
        }

        var projection = Expression.Lambda<Func<Record, RecordRow>>(Expression.MemberInit(Expression.New(typeof(RecordRow)), bindings), Row);
        return records.Select(projection);
    }

    private static MemberAssignment Bind(string member, Expression value) =>
        Expression.Bind(typeof(RecordRow).GetProperty(member)!, value);

    private static Expression Node(FilterNode node) => node switch
    {
        FilterGroup { IsAnd: true } group => group.Children.Select(Node).Aggregate(Expression.AndAlso),
        FilterGroup group => group.Children.Select(Node).Aggregate(Expression.OrElse),
        FilterNot not => Expression.Not(Node(not.Child)),
        FilterCondition condition => Condition(condition),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown filter node."),
    };

    private static Expression Condition(FilterCondition condition)
    {
        var field = condition.Field;
        return condition.Operator switch
        {
            FilterOperator.Eq => Equal(field, condition.Value),
            FilterOperator.Neq => Expression.Not(Equal(field, condition.Value)),
            FilterOperator.In => AnyOf(field, condition.Values),
            FilterOperator.Nin => Expression.Not(AnyOf(field, condition.Values)),
            FilterOperator.Contains when field.ValueType is FilterValueType.MultiOption or FilterValueType.Relation => Holds(field, condition.Value),
            FilterOperator.Contains => Like(field, $"%{EscapeLike((string)condition.Value)}%"),
            FilterOperator.StartsWith => Like(field, $"{EscapeLike((string)condition.Value)}%"),
            FilterOperator.Gt => Comparison(field, ExpressionType.GreaterThan, condition.Value),
            FilterOperator.Gte => Comparison(field, ExpressionType.GreaterThanOrEqual, condition.Value),
            FilterOperator.Lt => Comparison(field, ExpressionType.LessThan, condition.Value),
            FilterOperator.Lte => Comparison(field, ExpressionType.LessThanOrEqual, condition.Value),
            FilterOperator.IsEmpty => IsEmpty(field),
            FilterOperator.IsNotEmpty => Expression.Not(IsEmpty(field)),
            _ => throw new ArgumentOutOfRangeException(nameof(condition), condition.Operator, "Unknown operator."),
        };
    }

    /// <summary>Exact match. Stored JSON scalars use containment (GIN); typed values (number, dates) and columns compare directly.</summary>
    private static Expression Equal(QueryField field, object value)
    {
        if (field.Source != QueryFieldSource.Property)
        {
            return Compare(Key(field), ExpressionType.Equal, value);
        }

        return field.ValueType switch
        {
            FilterValueType.Number or FilterValueType.Date or FilterValueType.DateTime => Comparison(field, ExpressionType.Equal, value),
            _ => JsonContains(field.Key, value),
        };
    }

    /// <summary>in: any of the values (for multi_select and relation: holds any of them).</summary>
    private static Expression AnyOf(QueryField field, IReadOnlyList<object> values) =>
        values
            .Select(v => field.ValueType is FilterValueType.MultiOption or FilterValueType.Relation ? Holds(field, v) : Equal(field, v))
            .Aggregate(Expression.OrElse);

    /// <summary>
    /// The property holds the value: one containment test on the stored shape (multi_select: array; single relation: scalar id).
    /// Multiple relations store arrays but may still hold a scalar id from before <c>multiple</c> was switched on, so both forms match.
    /// </summary>
    private static Expression Holds(QueryField field, object value)
    {
        if (field.ValueType == FilterValueType.Relation && field.Property is { Multiple: false })
        {
            return JsonContains(field.Key, value);
        }

        var array = JsonContains(field.Key, new[] { value });
        return field.ValueType == FilterValueType.Relation ? Expression.OrElse(array, JsonContains(field.Key, value)) : array;
    }

    private static MethodCallExpression JsonContains(string key, object value)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { [key] = Wire(value) });
        var document = Expression.Call(typeof(JsonbFunctions).GetMethod(nameof(JsonbFunctions.Json))!, Parameter(json, typeof(string)));
        return Expression.Call(JsonContainsMethod, Expression.Constant(EF.Functions), PropertiesColumn, Expression.Convert(document, typeof(object)));
    }

    /// <summary>Stored form of a value: ids as lowercase D strings (normalizer), the rest as is.</summary>
    private static object Wire(object value) => value switch
    {
        Guid id => id.ToString("D"),
        object[] items => items.Select(Wire).ToArray(),
        _ => value,
    };

    /// <summary>ILIKE with <c>\</c> as escape character; missing values read as '' (never NULL, so <c>not</c> stays exact).</summary>
    private static MethodCallExpression Like(QueryField field, string pattern)
    {
        var text = field.IsNullable ? Expression.Coalesce(Key(field), Expression.Constant(string.Empty)) : Key(field);
        return Expression.Call(ILikeMethod, Expression.Constant(EF.Functions), text, Parameter(pattern, typeof(string)), Expression.Constant(LikeEscape));
    }

    /// <summary>Order comparison on the typed value; false for records without a value.</summary>
    private static BinaryExpression Comparison(QueryField field, ExpressionType comparison, object value)
    {
        var key = Key(field);
        var compared = Compare(key, comparison, value);
        return field.IsNullable ? Expression.AndAlso(IsNotNull(key), compared) : compared;
    }

    /// <summary>Key missing or null; for text also "", for multi-valued properties also [].</summary>
    private static Expression IsEmpty(QueryField field)
    {
        if (!field.IsNullable)
        {
            return field.Source == QueryFieldSource.Title ? Compare(Key(field), ExpressionType.Equal, string.Empty) : False;
        }

        var text = Text(field.Key);
        return field.ValueType switch
        {
            FilterValueType.Text => Expression.OrElse(IsNull(text), Compare(text, ExpressionType.Equal, string.Empty)),
            FilterValueType.MultiOption or FilterValueType.Relation => Expression.OrElse(IsNull(text), Compare(text, ExpressionType.Equal, "[]")),
            _ => IsNull(text),
        };
    }

    /// <summary>The typed sort/compare key of a field (CLR type: <see cref="QueryCursor.KeyType"/>, nullable for stored properties).</summary>
    private static Expression Key(QueryField field) => field.Source switch
    {
        QueryFieldSource.Title => Expression.Property(Row, nameof(Record.Title)),
        QueryFieldSource.CreatedAt => Expression.Property(Row, nameof(Record.CreatedAt)),
        QueryFieldSource.UpdatedAt => Expression.Property(Row, nameof(Record.UpdatedAt)),
        QueryFieldSource.CreatedBy => Expression.Property(Row, nameof(Record.CreatedBy)),
        QueryFieldSource.UpdatedBy => Expression.Property(Row, nameof(Record.UpdatedBy)),
        _ => field.ValueType switch
        {
            FilterValueType.Number => Jsonb(nameof(JsonbFunctions.Number), field.Key),
            FilterValueType.Date => Jsonb(nameof(JsonbFunctions.Date), field.Key),
            FilterValueType.DateTime => Jsonb(nameof(JsonbFunctions.Timestamp), field.Key),
            _ => Text(field.Key),
        },
    };

    private static MethodCallExpression Text(string key) => Jsonb(nameof(JsonbFunctions.Text), key);

    private static MethodCallExpression Jsonb(string function, string key) =>
        Expression.Call(typeof(JsonbFunctions).GetMethod(function)!, PropertiesColumn, Parameter(key, typeof(string)));

    /// <summary><paramref name="left"/> op <paramref name="value"/>; strings via <c>string.Compare</c> (SQL: plain comparison, database collation).</summary>
    private static BinaryExpression Compare(Expression left, ExpressionType comparison, object value)
    {
        if (left.Type == typeof(string))
        {
            var right = Parameter(value, typeof(string));
            return comparison == ExpressionType.Equal
                ? Expression.Equal(left, right)
                : Expression.MakeBinary(comparison, Expression.Call(StringCompare, left, right), Expression.Constant(0));
        }

        return Expression.MakeBinary(comparison, left, Parameter(value, left.Type));
    }

    private static BinaryExpression IsNull(Expression key) => Expression.Equal(key, Expression.Constant(null, Nullable(key.Type)));

    private static BinaryExpression IsNotNull(Expression key) => Expression.NotEqual(key, Expression.Constant(null, Nullable(key.Type)));

    private static Type Nullable(Type type) => type.IsValueType && System.Nullable.GetUnderlyingType(type) is null ? typeof(Nullable<>).MakeGenericType(type) : type;

    private static Expression And(Expression left, Expression right) =>
        ReferenceEquals(left, True) ? right : Expression.AndAlso(left, right);

    private static Expression Or(Expression? left, Expression right) => left is null ? right : Expression.OrElse(left, right);

    /// <summary>A value EF Core sends as a SQL parameter: a member of a captured object, not a constant in the SQL text.</summary>
    private static MemberExpression Parameter(object? value, Type type)
    {
        var box = Activator.CreateInstance(typeof(Box<>).MakeGenericType(type), value)!;
        return Expression.Property(Expression.Constant(box), nameof(Box<object>.Value));
    }

    private static IOrderedQueryable<Record> OrderBy(IQueryable<Record> source, Expression key, bool descending, bool thenBy)
    {
        var name = (thenBy, descending) switch
        {
            (false, false) => nameof(Queryable.OrderBy),
            (false, true) => nameof(Queryable.OrderByDescending),
            (true, false) => nameof(Queryable.ThenBy),
            (true, true) => nameof(Queryable.ThenByDescending),
        };
        var method = typeof(Queryable).GetMethods()
            .Single(m => m.Name == name && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(Record), key.Type);
        return (IOrderedQueryable<Record>)method.Invoke(null, [source, Expression.Lambda(key, Row)])!;
    }

    /// <summary><c>%</c>, <c>_</c> and the escape character itself match literally.</summary>
    private static string EscapeLike(string value) =>
        value.Replace(LikeEscape, LikeEscape + LikeEscape, StringComparison.Ordinal)
            .Replace("%", LikeEscape + "%", StringComparison.Ordinal)
            .Replace("_", LikeEscape + "_", StringComparison.Ordinal);

    private sealed class Box<T>(T value)
    {
        public T Value { get; } = value;
    }
}
