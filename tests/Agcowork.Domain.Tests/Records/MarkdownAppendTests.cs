using Agcowork.Domain.Records;

namespace Agcowork.Domain.Tests.Records;

public sealed class MarkdownAppendTests
{
    [Fact]
    public void Append_NoSection_AtEndWithBlankLine()
    {
        Assert.Equal("# T\n\ntext\n\n- new\n", MarkdownAppend.Append("# T\n\ntext\n\n\n", "\n- new\n\n", null));
        Assert.Equal("- new\n", MarkdownAppend.Append(string.Empty, "- new", null));
    }

    [Fact]
    public void Append_Section_AtEndOfSectionBeforeNextHeadingOfSameOrHigherLevel()
    {
        const string body = "## Kontext\nA\n\n## Log\n- one\n\n### Detail\nd\n\n## Entscheidung\nE\n";

        var result = MarkdownAppend.Append(body, "- two", "log");

        Assert.Equal("## Kontext\nA\n\n## Log\n- one\n\n### Detail\nd\n\n- two\n\n## Entscheidung\nE\n", result);
    }

    [Fact]
    public void Append_SectionWithHashesAndCase_MatchesHeading()
    {
        var result = MarkdownAppend.Append("# Notes\n\n## LOG ##\n- one\n", "- two", "## Log");

        Assert.Equal("# Notes\n\n## LOG ##\n- one\n\n- two\n", result);
    }

    [Fact]
    public void Append_HeadingInsideCodeFence_Ignored()
    {
        const string body = "```\n## Log\n```\n\n## Log\n- one\n";

        var result = MarkdownAppend.Append(body, "- two", "Log");

        Assert.Equal("```\n## Log\n```\n\n## Log\n- one\n\n- two\n", result);
    }

    [Fact]
    public void Append_MissingSection_CreatedAtEnd()
    {
        var result = MarkdownAppend.Append("## Kontext\nA\n", "- first", "Log");

        Assert.Equal("## Kontext\nA\n\n## Log\n\n- first\n", result);
    }

    [Fact]
    public void Append_EmptyMarkdown_Throws() =>
        Assert.Throws<ArgumentException>(() => MarkdownAppend.Append("x", "  ", null));
}
