using Agcowork.Domain.Comments;

namespace Agcowork.Domain.Tests.Comments;

public sealed class CommentMarkdownTests
{
    private static readonly Guid A = Guid.Parse("0190f3a2-7c4e-7b1a-9d2e-3f4a5b6c7d8e");
    private static readonly Guid B = Guid.Parse("0190f3a2-7c4e-7b1a-9d2e-000000000002");

    private static IReadOnlyList<Guid> Ids(string markdown) => CommentMarkdown.FindMentions(markdown).ActorIds;

    [Fact]
    public void FindMentions_SeveralLinks_InOrder_DuplicatesOnce()
    {
        var markdown = $"[@A](actor:{A}) und [@B](actor:{B}), nochmal [@Anders genannt](actor:{A}).";

        Assert.Equal([A, B], Ids(markdown));
    }

    [Fact]
    public void FindMentions_UuidAndSchemeInAnyCase_AngleBrackets_FreeLinkText()
    {
        Assert.Equal([A], Ids($"[@a](actor:{A.ToString().ToUpperInvariant()})"));
        Assert.Equal([A], Ids($"[jemand](ACTOR:{A})"));
        Assert.Equal([A], Ids($"[@a](<actor:{A}>)"));
        Assert.Equal([A], Ids($"[@a]( actor:{A} )"));
        Assert.Equal(CommentMarkdown.MentionLink("Tech-Lead", A), $"[@Tech-Lead](actor:{A})");
        Assert.Equal([A], Ids(CommentMarkdown.MentionLink("Tech-Lead", A)));
    }

    [Fact]
    public void FindMentions_InCodeSpan_NotCounted()
    {
        Assert.Empty(Ids($"Syntax: `[@a](actor:{A})`"));
        Assert.Empty(Ids($"Doppelt: `` x `[@a](actor:{A})` y ``"));
        Assert.Equal([B], Ids($"`[@a](actor:{A})` aber [@b](actor:{B})"));
        // An unclosed backtick is literal text, the link after it counts.
        Assert.Equal([A], Ids($"ein ` allein [@a](actor:{A})"));
        // A code span may span lines of one paragraph.
        Assert.Empty(Ids($"`code\n[@a](actor:{A})`"));
    }

    [Fact]
    public void FindMentions_InFencedOrIndentedCodeBlock_NotCounted()
    {
        Assert.Empty(Ids($"```\n[@a](actor:{A})\n```"));
        Assert.Empty(Ids($"~~~markdown\n[@a](actor:{A})\n~~~"));
        Assert.Empty(Ids($"````\n```\n[@a](actor:{A})\n```\n````"));
        Assert.Empty(Ids($"Text\n\n    [@a](actor:{A})\n"));
        Assert.Equal([B], Ids($"```\n[@a](actor:{A})\n```\n\n[@b](actor:{B})"));
        // Unclosed fence: code until the end.
        Assert.Empty(Ids($"```\n[@a](actor:{A})"));
    }

    [Fact]
    public void FindMentions_IndentedLines_InParagraphOrList_Counted()
    {
        // An indented line cannot interrupt a paragraph.
        Assert.Equal([A], Ids($"Absatz\n    [@a](actor:{A})"));
        Assert.Equal([A], Ids($"- Punkt\n\n    [@a](actor:{A}) gehört zum Punkt"));
        Assert.Equal([A, B], Ids($"1. [@a](actor:{A})\n   - [@b](actor:{B})"));
    }

    [Fact]
    public void FindMentions_BrokenOrOtherLinks_Ignored_InvalidActorTargetsReported()
    {
        var scan = CommentMarkdown.FindMentions($"[@a] (actor:{A}) [@b](actor:{A} [c](https://x.y/actor:{A}) \\[@d](actor:{B}) [@e](actor:nicht-uuid) [@f](actor:)");

        Assert.Empty(scan.ActorIds);
        Assert.Equal(["actor:nicht-uuid", "actor:"], scan.InvalidTargets);
    }

    [Fact]
    public void FindMentions_CodeSpanBindsBeforeLink()
    {
        // The code span starts in the link text and ends after the destination: no link.
        Assert.Empty(Ids($"[a`](actor:{A})`"));
        Assert.Empty(Ids($"[a](actor:`{A})`"));
        // A code span completely inside the link text is fine.
        Assert.Equal([A], Ids($"[`@a`](actor:{A})"));
        // An escaped backtick opens nothing.
        Assert.Equal([A], Ids($"\\`[@a](actor:{A})`"));
    }

    [Fact]
    public void FindMentions_IndentedLineAfterHeadingOrFence_IsCode()
    {
        Assert.Empty(Ids($"# Titel\n    [@a](actor:{A})"));
        Assert.Empty(Ids($"```\nx\n```\n    [@a](actor:{A})"));
        Assert.Equal([B], Ids($"# Titel [@b](actor:{B})\n    [@a](actor:{A})"));
    }

    [Fact]
    public void FindMentions_LinkTextAcrossLines_Counted()
    {
        Assert.Equal([A], Ids($"Hallo [@Tech\nLead](actor:{A})"));
        // A blank line ends the paragraph and with it the link.
        Assert.Empty(Ids($"Hallo [@Tech\n\nLead](actor:{A})"));
    }

    [Fact]
    public void FindMentions_InHtmlComment_NotCounted()
    {
        Assert.Empty(Ids($"Text <!-- [@a](actor:{A}) --> Ende"));
        Assert.Empty(Ids($"<!--\n[@a](actor:{A})\n\n[@a](actor:{A})\n-->"));
        Assert.Equal([B], Ids($"<!-- weg -->\n\n[@b](actor:{B})"));
        Assert.Equal([B], Ids($"<!--\n[@a](actor:{A})\n-->\n[@b](actor:{B})"));
        // Unclosed inline comment start is literal.
        Assert.Equal([A], Ids($"a <!-- b [@a](actor:{A})"));
        Assert.Equal("a b", CommentMarkdown.PlainText("a <!-- geheim --> b", 200));
    }

    [Fact]
    public void FindMentions_InBlockQuote_Counted()
    {
        Assert.Equal([A], Ids($"> [@a](actor:{A})"));
        Assert.Equal([A], Ids($">[@a](actor:{A})"));
        Assert.Equal([A], Ids($"   > Zitat\n> [@a](actor:{A})"));
        Assert.Equal([A, B], Ids($"> [@a](actor:{A})\n\nDanach [@b](actor:{B})"));
        // Nested quotes.
        Assert.Equal([A], Ids($">> [@a](actor:{A})"));
        Assert.Equal([A], Ids($"> > [@a](actor:{A})"));
        // Link text across lines of the quoted paragraph, and lazy continuation without marker.
        Assert.Equal([A], Ids($"> [@a\n> b](actor:{A})"));
        Assert.Equal([A], Ids($"> Zitat\n[@a](actor:{A}) gehört noch dazu"));
        // After a quote and a blank line an indented line is code; the next quote is a new one.
        Assert.Equal([A], Ids($"> Zitat\n\n    [@b](actor:{B})\n> [@a](actor:{A})"));
    }

    [Fact]
    public void FindMentions_CodeInBlockQuote_NotCounted()
    {
        // Fenced code inside the quote.
        Assert.Empty(Ids($"> ```\n> [@a](actor:{A})\n> ```"));
        Assert.Empty(Ids($"> ~~~\n> [@a](actor:{A})"));
        Assert.Empty(Ids($">```\n>[@a](actor:{A})\n>```"));
        Assert.Empty(Ids($"> > ```\n> > [@a](actor:{A})"));
        // Indented code inside the quote: marker, optional space, then four columns.
        Assert.Empty(Ids($">     [@a](actor:{A})"));
        Assert.Empty(Ids($"> Zitat\n>\n>     [@a](actor:{A})"));
        Assert.Empty(Ids($">\t\t[@a](actor:{A})"));
        Assert.Empty(Ids($"> >     [@a](actor:{A})"));
        // HTML comment inside the quote.
        Assert.Empty(Ids($"> <!--\n> [@a](actor:{A})\n> -->"));
        // Code spans as everywhere.
        Assert.Empty(Ids($"> `[@a](actor:{A})`"));
    }

    [Fact]
    public void FindMentions_BlockQuoteBoundaries_FollowCommonMark()
    {
        // Four spaces before ">" are indented code, not a quote.
        Assert.Empty(Ids($"    > [@a](actor:{A})"));
        // Inside a quote an indented line continues an open paragraph.
        Assert.Equal([A], Ids($"> Zitat\n>     [@a](actor:{A})"));
        // ">" plus a tab: the tab reaches column 4, one column is the optional space, so two columns remain: no code.
        Assert.Equal([A], Ids($">\t[@a](actor:{A})"));
        // A fence open inside the quote ends with the quote; the line after it is outside again.
        Assert.Equal([A], Ids($"> ```\n> code\n[@a](actor:{A})"));
        // A line without marker does not continue quoted code lazily, and a fence line without marker ends the quote.
        Assert.Equal([A], Ids($">     code\n[@a](actor:{A})"));
        Assert.Empty(Ids($"> Zitat\n```\n[@a](actor:{A})\n```"));
        // A link never spans the quote boundary: a quote interrupts the paragraph before it.
        Assert.Empty(Ids($"[@a\n> b](actor:{A})"));
        // A mention after a quoted fence that was closed inside the quote counts.
        Assert.Equal([B], Ids($"> ```\n> [@a](actor:{A})\n> ```\n> [@b](actor:{B})"));
    }

    [Fact]
    public void FindMentions_DeeplyNestedQuotes_DepthLimited_FastWithoutCrash()
    {
        var unspaced = new string('>', 64 * 1024) + $" [@a](actor:{A})";
        var spaced = string.Concat(Enumerable.Repeat("> ", 32_000)) + $"[@a](actor:{A})";
        var manyLines = string.Join('\n', Enumerable.Repeat(new string('>', 2000) + " x", 32)) + $"\n[@a](actor:{A})";
        Assert.Equal([A], Ids($"> [@a](actor:{A})")); // JIT warm-up outside the measurement

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var fromUnspaced = Ids(unspaced);
        var fromSpaced = Ids(spaced);
        var fromManyLines = Ids(manyLines);
        watch.Stop();

        // Beyond the depth limit the markers are text of the innermost quote's paragraph, so the link there counts.
        Assert.Equal([A], fromUnspaced);
        Assert.Equal([A], fromSpaced);
        Assert.Equal([A], fromManyLines);
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void FindMentions_QuoteDepthLimit_MarkersBeyondAreText()
    {
        var limit = CommentMarkdown.MaxQuoteDepth;

        // At the limit the content is still inside the innermost quote: four columns after its marker are code.
        Assert.Empty(Ids(new string('>', limit) + $"     [@a](actor:{A})"));
        Assert.Empty(Ids(new string('>', limit) + $" ```\n{new string('>', limit)} [@a](actor:{A})"));
        // One marker more is text: the line is a paragraph of the innermost quote, the link counts.
        Assert.Equal([A], Ids(new string('>', limit + 1) + $"     [@a](actor:{A})"));
        Assert.Equal([A], Ids(new string('>', limit + 1) + $" ```\n[@a](actor:{A})"));
    }

    [Fact]
    public void FindMentions_ManyUnclosedOpeners_Linear()
    {
        var comments = "Text " + string.Concat(Enumerable.Repeat("<!--", 16 * 1024)) + $" [@a](actor:{A})";
        var backticks = string.Concat(Enumerable.Range(1, 300).Select(n => new string('`', n) + "x")) + $" [@a](actor:{A})";
        var links = string.Concat(Enumerable.Repeat("[x](", 16 * 1024)) + $" [@a](actor:{A})";
        Assert.Equal([A], Ids($"[@a](actor:{A})"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var fromComments = Ids(comments);
        var fromBackticks = Ids(backticks);
        var fromLinks = Ids(links);
        var plain = CommentMarkdown.PlainText(comments + links, 200);
        watch.Stop();

        Assert.Equal([A], fromComments);
        Assert.Equal([A], fromBackticks);
        Assert.Equal([A], fromLinks);
        Assert.NotEmpty(plain);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void FindMentions_EmptyOrNull_Nothing()
    {
        Assert.Empty(Ids(string.Empty));
        Assert.Empty(CommentMarkdown.FindMentions(null).ActorIds);
    }

    [Fact]
    public void PlainText_StripsMarkdown_KeepsMentionNames_Collapses()
    {
        var markdown = $"## Frage\n\n> **Wichtig:** [@Tech-Lead](actor:{A}) bitte `snake_case` prüfen, siehe [Doku](https://x.y) und ![Bild](a.png).\n\n- eins\n- _zwei_";

        Assert.Equal("Frage Wichtig: @Tech-Lead bitte snake_case prüfen, siehe Doku und Bild. eins zwei", CommentMarkdown.PlainText(markdown, 200));
    }

    [Fact]
    public void PlainText_LongText_CutWithEllipsis_NoBrokenSurrogate()
    {
        var text = CommentMarkdown.PlainText(new string('a', 300), 200);
        Assert.Equal(200, text.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);

        var emoji = CommentMarkdown.PlainText(new string('a', 198) + "🚀🚀🚀", 200);
        Assert.False(char.IsHighSurrogate(emoji[^2]));
        Assert.True(emoji.Length <= 200);
    }
}
