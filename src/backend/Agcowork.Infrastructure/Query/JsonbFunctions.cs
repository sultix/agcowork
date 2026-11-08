using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Infrastructure.Query;

/// <summary>
/// Typed reads of one top-level key of a JSONB column, for LINQ queries only (they throw when called in .NET). Translated by
/// <see cref="JsonbMethodCallTranslator"/>; the key is a SQL parameter, never SQL text. Values of another JSON type than
/// expected read as NULL instead of failing the cast.
/// </summary>
internal static class JsonbFunctions
{
    /// <summary>A JSON document sent as a <c>jsonb</c> parameter (for <c>@&gt;</c>, which has no <c>jsonb @&gt; text</c> form).</summary>
    public static string Json(string json) => throw OnlyInQueries();

    /// <summary><c>jsonb_extract_path_text(properties, key)</c>: the value as text (strings unquoted, arrays as JSON).</summary>
    public static string? Text(object properties, string key) => throw OnlyInQueries();

    /// <summary>The value as <c>numeric</c> when it is a JSON number.</summary>
    public static decimal? Number(object properties, string key) => throw OnlyInQueries();

    /// <summary>The value as <c>date</c> when it is a JSON string (values are validated ISO dates).</summary>
    public static DateOnly? Date(object properties, string key) => throw OnlyInQueries();

    /// <summary>The value as <c>timestamptz</c> when it is a JSON string (values are validated ISO date-times).</summary>
    public static DateTimeOffset? Timestamp(object properties, string key) => throw OnlyInQueries();

    private static InvalidOperationException OnlyInQueries() => new("JSONB functions can only be used in LINQ queries against the database.");
}

/// <summary>SQL for <see cref="JsonbFunctions"/>.</summary>
internal sealed class JsonbMethodCallTranslator(ISqlExpressionFactory sql, IRelationalTypeMappingSource types) : IMethodCallTranslator
{
    private readonly RelationalTypeMapping _text = types.FindMapping(typeof(string))!;
    private readonly RelationalTypeMapping _jsonb = types.FindMapping(typeof(string), "jsonb")!;

    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);
        if (method.DeclaringType != typeof(JsonbFunctions))
        {
            return null;
        }

        if (method.Name == nameof(JsonbFunctions.Json))
        {
            return sql.ApplyTypeMapping(arguments[0], _jsonb);
        }

        var properties = arguments[0];
        var key = sql.ApplyTypeMapping(arguments[1], _text);
        var text = sql.Function("jsonb_extract_path_text", [properties, key], nullable: true, argumentsPropagateNullability: [false, false], typeof(string), _text);
        return method.Name switch
        {
            nameof(JsonbFunctions.Text) => text,
            nameof(JsonbFunctions.Number) => Guarded(properties, key, text, "number", typeof(decimal)),
            nameof(JsonbFunctions.Date) => Guarded(properties, key, text, "string", typeof(DateOnly)),
            nameof(JsonbFunctions.Timestamp) => Guarded(properties, key, text, "string", typeof(DateTimeOffset)),
            _ => null,
        };
    }

    /// <summary><c>CASE WHEN jsonb_typeof(jsonb_extract_path(p, k)) = 'jsonType' THEN CAST(text AS type) END</c>.</summary>
    private SqlExpression Guarded(SqlExpression properties, SqlExpression key, SqlExpression text, string jsonType, Type type)
    {
        var value = sql.Function("jsonb_extract_path", [properties, key], nullable: true, argumentsPropagateNullability: [false, false], typeof(string), _jsonb);
        var typeOf = sql.Function("jsonb_typeof", [value], nullable: true, argumentsPropagateNullability: [false], typeof(string), _text);
        var mapping = types.FindMapping(type)!;
        return sql.Case([new CaseWhenClause(sql.Equal(typeOf, sql.Constant(jsonType, _text)), sql.Convert(text, type, mapping))], elseResult: null);
    }
}

internal sealed class JsonbMethodCallTranslatorPlugin(ISqlExpressionFactory sql, IRelationalTypeMappingSource types) : IMethodCallTranslatorPlugin
{
    public IEnumerable<IMethodCallTranslator> Translators { get; } =
        [new JsonbMethodCallTranslator(sql, types), new Search.RecordSearchMethodCallTranslator(sql, types)];
}

/// <summary>
/// Keeps <see cref="JsonbFunctions"/> and <see cref="Search.RecordSearchFunctions"/> calls in the query even when their
/// arguments are all parameters (e.g. <see cref="JsonbFunctions.Json"/>, <see cref="Search.RecordSearchFunctions.Query"/>).
/// </summary>
internal sealed class JsonbEvaluatableExpressionFilterPlugin : IEvaluatableExpressionFilterPlugin
{
    public bool IsEvaluatableExpression(Expression expression) =>
        expression is not MethodCallExpression call
        || (call.Method.DeclaringType != typeof(JsonbFunctions) && call.Method.DeclaringType != typeof(Search.RecordSearchFunctions));
}

/// <summary>Registers the <see cref="JsonbFunctions"/> and <see cref="Search.RecordSearchFunctions"/> translations with the EF Core services of the context.</summary>
internal sealed class JsonbQueryOptionsExtension : IDbContextOptionsExtension
{
    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) =>
        new EntityFrameworkRelationalServicesBuilder(services)
            .TryAdd<IMethodCallTranslatorPlugin, JsonbMethodCallTranslatorPlugin>()
            .TryAdd<IEvaluatableExpressionFilterPlugin, JsonbEvaluatableExpressionFilterPlugin>();

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using JsonbFunctions ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["Agcowork:JsonbFunctions"] = "1";
    }
}
