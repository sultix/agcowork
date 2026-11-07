using System.Text.Json;
using Agcowork.Application.Schema;
using static Agcowork.Application.Tests.Schema.SchemaFixtures;

namespace Agcowork.Application.Tests.Schema;

public sealed class PropertyValueNormalizerTests
{
    private static string Normalized(string json, string key) =>
        PropertyValueNormalizer.Normalize(AllTypes, Props(json))[key].GetRawText();

    [Fact]
    public void Normalize_TrimsTextLikeValues()
    {
        Assert.Equal("\"Title\"", Normalized("""{ "title": "  Title \n" }""", "title"));
        Assert.Equal("\"high\"", Normalized("""{ "priority": " high " }""", "priority"));
        Assert.Equal("\"2026-11-07\"", Normalized("""{ "due": " 2026-11-07 " }""", "due"));
        Assert.Equal("\"a@example.com\"", Normalized("""{ "mail": " a@example.com " }""", "mail"));
        Assert.Equal("\"https://example.com\"", Normalized("""{ "link": " https://example.com " }""", "link"));
    }

    [Fact]
    public void Normalize_KeepsMarkdownWhitespace()
    {
        Assert.Equal("\"    code\\n\"", Normalized("""{ "notes": "    code\n" }""", "notes"));
    }

    [Fact]
    public void Normalize_SelectKeepsCase()
    {
        Assert.Equal("\"High\"", Normalized("""{ "priority": "High" }""", "priority"));
    }

    [Fact]
    public void Normalize_DateTimeToUtc()
    {
        Assert.Equal("\"2026-11-07T13:30:00Z\"", Normalized("""{ "at": "2026-11-07T14:30:00+01:00" }""", "at"));
        Assert.Equal("\"2026-11-07T13:30:00.5Z\"", Normalized("""{ "at": "2026-11-07T13:30:00.500Z" }""", "at"));
    }

    [Fact]
    public void Normalize_InvalidDateTime_Unchanged()
    {
        Assert.Equal("\"tomorrow\"", Normalized("""{ "at": "tomorrow" }""", "at"));
    }

    [Fact]
    public void Normalize_MultiSelect_TrimsAndDropsDuplicates()
    {
        Assert.Equal("""["a","b"]""", Normalized("""{ "tags": [" a", "b", "a "] }""", "tags"));
    }

    [Fact]
    public void Normalize_Ids_LowercaseAndDeduplicated()
    {
        Assert.Equal("\"0190f5e2-7a4c-7000-8000-0000000000b1\"", Normalized("""{ "owner": "0190F5E2-7A4C-7000-8000-0000000000B1" }""", "owner"));
        Assert.Equal(
            """["0190f5e2-7a4c-7000-8000-0000000000b1"]""",
            Normalized("""{ "related": ["0190F5E2-7A4C-7000-8000-0000000000B1", "0190f5e2-7a4c-7000-8000-0000000000b1"] }""", "related"));
    }

    [Fact]
    public void Normalize_UnknownKeysAndWrongShapes_PassThrough()
    {
        var result = PropertyValueNormalizer.Normalize(AllTypes, Props("""{ "nope": " x ", "estimate": " 2 ", "tags": "a" }"""));

        Assert.Equal("\" x \"", result["nope"].GetRawText());
        Assert.Equal("\" 2 \"", result["estimate"].GetRawText());
        Assert.Equal("\"a\"", result["tags"].GetRawText());
    }

    [Fact]
    public void ApplyDefaults_AddsMissingOnly()
    {
        var schema = new[]
        {
            AllTypes[0],
            AllTypes[5] with { Default = JsonSerializer.SerializeToElement("low") },
            AllTypes[4] with { Default = JsonSerializer.SerializeToElement(false) },
        };

        var result = PropertyValueNormalizer.ApplyDefaults(schema, Props("""{ "done": true }"""));

        Assert.Equal("\"low\"", result["priority"].GetRawText());
        Assert.Equal("true", result["done"].GetRawText());
        Assert.False(result.ContainsKey("title"));
    }
}
