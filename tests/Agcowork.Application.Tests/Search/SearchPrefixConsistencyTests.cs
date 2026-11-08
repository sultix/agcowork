using System.Globalization;
using System.Text.RegularExpressions;
using Agcowork.Application.Search;
using Microsoft.EntityFrameworkCore;
using DomainRecord = Agcowork.Domain.Records.Record;

namespace Agcowork.Application.Tests.Search;

/// <summary>
/// The prefix lengths written into the database functions of the migration <c>AddRecordSearch</c> match the constants of
/// <see cref="RecordSearch"/> (AP-035): the migration repeats the numbers as literals, a change on one side only would
/// silently index or snip a different prefix than documented.
/// </summary>
public sealed partial class SearchPrefixConsistencyTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private Task<string> TextAsync(FormattableString sql) => Db.Database.SqlQuery<string>(sql).SingleAsync(Ct);

    private static int Number(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        Assert.True(match.Success, $"'{pattern}' not found in: {text}");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task SearchVector_DefaultPrefixes_MatchRecordSearch()
    {
        var arguments = await TextAsync($"SELECT pg_get_function_arguments('agcowork_record_search_vector'::regproc) AS \"Value\"");

        Assert.Equal(RecordSearch.PropertiesPrefixLength, Number(PropertiesDefault(), arguments));
        Assert.Equal(RecordSearch.BodyPrefixLength, Number(BodyDefault(), arguments));
    }

    [Fact]
    public async Task SearchVector_TitleFallback_MatchesTitleLimit()
    {
        var definition = await TextAsync($"SELECT pg_get_functiondef('agcowork_record_search_vector'::regproc) AS \"Value\"");

        Assert.Equal(DomainRecord.TitleMaxLength, Number(TitleFallback(), definition));
    }

    [Fact]
    public async Task SnippetDocument_Prefixes_MatchRecordSearch()
    {
        var definition = await TextAsync($"SELECT pg_get_functiondef('agcowork_record_search_document'::regproc) AS \"Value\"");

        Assert.Equal(RecordSearch.PropertiesPrefixLength, Number(DocumentPropertiesPrefix(), definition));
        Assert.Equal(RecordSearch.BodyPrefixLength, Number(DocumentBodyPrefix(), definition));
    }

    [GeneratedRegex(@"p_properties_length integer DEFAULT (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PropertiesDefault();

    [GeneratedRegex(@"p_body_length integer DEFAULT (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex BodyDefault();

    [GeneratedRegex(@"left\(coalesce\(p_title, ''\), (\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex TitleFallback();

    // left(agcowork_search_properties_text(p_properties, (SELECT … )), 25000)
    [GeneratedRegex(@"left\(agcowork_search_properties_text\([\s\S]*?\)\), (\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentPropertiesPrefix();

    [GeneratedRegex(@"left\(p_body, (\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentBodyPrefix();
}
