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
