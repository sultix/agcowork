using System.Reflection;
using Agcowork.Application.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using NpgsqlTypes;

namespace Agcowork.Infrastructure.Search;

/// <summary>
/// The full-text parts of the records table for LINQ queries only (they throw when called in .NET). Translated by
/// <see cref="RecordSearchMethodCallTranslator"/>. <c>search_vector</c> is deliberately not part of the EF model: it is
/// maintained by a trigger (migration <c>AddRecordSearch</c>), and a mapped column would be read with every record.
/// </summary>
internal static class RecordSearchFunctions
{
    /// <summary>Text search configuration of index and queries: no stemming, no stop words (content is mixed German/English).</summary>
    public const string Configuration = "simple";

    /// <summary>
    /// <c>ts_headline</c> options: at most two fragments of up to 30 words, with the markers around matches. The markers contain
    /// no comma, space or quote and need no quoting; the fragment delimiter contains spaces and is quoted.
    /// </summary>
    public static readonly string HeadlineOptions =
        $"StartSel={RecordSearch.HighlightStart}, StopSel={RecordSearch.HighlightEnd}, MaxWords=30, MinWords=12, ShortWord=2, MaxFragments=2, FragmentDelimiter=\" … \"";

    /// <summary>
    /// <c>search_vector</c> of the row whose <c>id</c> column is <paramref name="recordId"/> (same table alias). Pass the
    /// <c>Id</c> of the queried record directly.
    /// </summary>
    public static NpgsqlTsVector Vector(Guid recordId) => throw OnlyInQueries();

    /// <summary><c>CAST(text AS tsquery)</c>: the canonical tsquery text of <see cref="RecordSearchIndex.ParseAsync"/>, as a parameter.</summary>
    public static NpgsqlTsQuery Query(string tsquery) => throw OnlyInQueries();

    /// <summary>
    /// <c>float4(value)</c>: keeps a rank in <c>real</c>. PostgreSQL resolves <c>real + integer</c> to <c>double precision</c>,
    /// which would make the rank of the keyset cursor (a float) differ from the rank the database compares and sorts by.
    /// </summary>
    public static float Real(float value) => throw OnlyInQueries();

    /// <summary>
    /// The text the snippet is cut from: the first <see cref="RecordSearch.SnippetSourceLength"/> characters of indexed property
    /// text and indexed body prefix (<c>agcowork_record_search_document</c>), with the marker characters replaced, so markers in a
    /// snippet only come from the server.
    /// </summary>
    public static string Document(Guid workspaceId, Guid? collectionId, object properties, string body) => throw OnlyInQueries();

    private static InvalidOperationException OnlyInQueries() => new("Record search functions can only be used in LINQ queries against the database.");
}

/// <summary>SQL for <see cref="RecordSearchFunctions"/>.</summary>
internal sealed class RecordSearchMethodCallTranslator(ISqlExpressionFactory sql, IRelationalTypeMappingSource types) : IMethodCallTranslator
{
    private readonly RelationalTypeMapping _text = types.FindMapping(typeof(string))!;
    private readonly RelationalTypeMapping _vector = types.FindMapping(typeof(NpgsqlTsVector))!;
    private readonly RelationalTypeMapping _query = types.FindMapping(typeof(NpgsqlTsQuery))!;

    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<DbLoggerCategory.Query> logger)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);
        if (method.DeclaringType != typeof(RecordSearchFunctions))
        {
            return null;
        }

        switch (method.Name)
        {
            case nameof(RecordSearchFunctions.Vector):
                // The column of the same table as the id column: the query filter, joins and pushdown keep their aliases.
                return arguments[0] is ColumnExpression id
                    ? new ColumnExpression("search_vector", id.TableAlias, typeof(NpgsqlTsVector), _vector, nullable: false)
                    : throw new InvalidOperationException("RecordSearchFunctions.Vector needs the Id column of the queried record.");
            case nameof(RecordSearchFunctions.Query):
                return sql.Convert(sql.ApplyTypeMapping(arguments[0], _text), typeof(NpgsqlTsQuery), _query);
            case nameof(RecordSearchFunctions.Real):
                return sql.Function("float4", [arguments[0]], nullable: true, argumentsPropagateNullability: [true], typeof(float), types.FindMapping(typeof(float)));
            case nameof(RecordSearchFunctions.Document):
                var document = sql.Function(
                    "agcowork_record_search_document",
                    [arguments[0], arguments[1], arguments[2], arguments[3]],
                    nullable: true,
                    argumentsPropagateNullability: [false, false, false, false],
                    typeof(string),
                    _text);
                var source = sql.Function(
                    "left",
                    [document, sql.Constant(RecordSearch.SnippetSourceLength, types.FindMapping(typeof(int)))],
                    nullable: true,
                    argumentsPropagateNullability: [true, false],
                    typeof(string),
                    _text);
                return Replace(Replace(source, RecordSearch.HighlightStart, "["), RecordSearch.HighlightEnd, "]");
            default:
                return null;
        }
    }

    private SqlExpression Replace(SqlExpression text, string from, string to) =>
        sql.Function(
            "replace",
            [text, sql.Constant(from, _text), sql.Constant(to, _text)],
            nullable: true,
            argumentsPropagateNullability: [true, false, false],
            typeof(string),
            _text);
}
