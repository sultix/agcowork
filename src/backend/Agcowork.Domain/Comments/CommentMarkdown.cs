using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Agcowork.Domain.Comments;

/// <summary>What <see cref="CommentMarkdown.FindMentions"/> found in a comment.</summary>
/// <param name="ActorIds">Mentioned actors, each once, in order of first appearance.</param>
/// <param name="InvalidTargets">Link targets with the <c>actor:</c> scheme that are no uuid (e.g. <c>actor:alice</c>), each once.</param>
public sealed record MentionScan(IReadOnlyList<Guid> ActorIds, IReadOnlyList<string> InvalidTargets);

/// <summary>
/// Markdown rules of comments: mentions and plain-text excerpts.
/// </summary>
/// <remarks>
/// A mention is a Markdown link whose destination is <c>actor:&lt;uuid&gt;</c>, canonically <c>[@Display name](actor:&lt;uuid&gt;)</c>
/// (architecture §2). Only the destination counts; the link text is free and not checked. Rules (a subset of CommonMark; renderers
/// that show mentions, e.g. the web app, follow the same ones):
/// <list type="bullet">
/// <item>The scheme is case-insensitive, the uuid accepted in either case (format <c>D</c>), the destination may be wrapped in
/// <c>&lt;…&gt;</c> and surrounded by whitespace. A link with the <c>actor:</c> scheme and no uuid is reported as invalid.</item>
/// <item>The link text may span lines of one paragraph, but contains no brackets; link titles are not recognized.</item>
/// <item>Code never mentions: fenced code blocks (<c>```</c>, <c>~~~</c>, closed by a fence of the same character and at least the
/// same length, or open until the end), indented code blocks (four spaces or a tab where no paragraph is open: at the start, after a
/// blank line, a heading, a fence or an HTML comment; never inside lists, where indented lines are list content) and code spans
/// (a backtick run closed by a run of the same length).</item>
/// <item>HTML comments never mention: <c>&lt;!-- … --&gt;</c> inline, and as a block from a line starting with <c>&lt;!--</c> to
/// the line containing <c>--&gt;</c> (blank lines included).</item>
/// <item>Code spans and HTML comments bind before links: a link whose brackets or destination are cut by a code span or comment
/// (<c>[a`](actor:…)`</c>) is no link; a code span completely inside the link text (<c>[`@x`](actor:…)</c>) is fine.</item>
/// <item>A backslash escapes ASCII punctuation: <c>\[</c> starts no link, <c>\`</c> no code span.</item>
/// </list>
/// </remarks>
public static partial class CommentMarkdown
{
    public const string ActorScheme = "actor:";

    /// <summary>The canonical mention link, e.g. <c>[@Tech-Lead](actor:0190…)</c>.</summary>
    public static string MentionLink(string displayName, Guid actorId) =>
        string.Create(CultureInfo.InvariantCulture, $"[@{displayName}]({ActorScheme}{actorId:D})");

    /// <summary>The actors mentioned in <paramref name="markdown"/> (outside code), and malformed <c>actor:</c> targets.</summary>
    public static MentionScan FindMentions(string? markdown)
    {
        var ids = new List<Guid>();
        var invalid = new List<string>();
        if (string.IsNullOrEmpty(markdown))
        {
            return new MentionScan(ids, invalid);
        }

        foreach (var segment in InlineSegments(markdown))
        {
            ScanInline(segment, ids, invalid);
        }

        return new MentionScan(ids, invalid);
    }

    /// <summary>
    /// Plain text of <paramref name="markdown"/> for previews, at most <paramref name="maxLength"/> characters (cut with "…"):
    /// links become their text (mentions keep their <c>@Name</c>), images their alt text, markers of headings, quotes, lists,
    /// emphasis and code are dropped, whitespace collapses to single spaces.
    /// </summary>
    public static string PlainText(string? markdown, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = HtmlComment().Replace(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), string.Empty).Split('\n')
            .Where(line => !FenceLine().IsMatch(line))
            .Select(line => BlockMarker().Replace(line, string.Empty));
        var text = string.Join(' ', lines);
        text = Image().Replace(text, "$1");
        text = Link().Replace(text, "$1");
        text = HtmlTag().Replace(text, string.Empty);
        text = StrongOrStrike().Replace(text, string.Empty);
        text = text.Replace("`", string.Empty, StringComparison.Ordinal).Replace("*", string.Empty, StringComparison.Ordinal);
        text = Underscore().Replace(text, string.Empty);
        text = Whitespace().Replace(text, " ").Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = maxLength - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return string.Concat(text.AsSpan(0, cut).TrimEnd(), "…");
    }

    /// <summary>The inline content of the document outside code blocks and HTML comment blocks, one entry per block (paragraph, heading, list item).</summary>
    private static List<string> InlineSegments(string markdown)
    {
        var segments = new List<string>();
        var block = new StringBuilder();
        void Flush()
        {
            if (block.Length > 0)
            {
                segments.Add(block.ToString());
                block.Clear();
            }
        }

        char? fenceChar = null;
        var fenceLength = 0;
        var inHtmlComment = false;
        var inList = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var indent = Indentation(line);

            if (fenceChar is { } open)
            {
                if (ClosingFence().Match(line) is { Success: true } close && close.Groups[1].Value[0] == open && close.Groups[1].Value.Length >= fenceLength)
                {
                    fenceChar = null;
                }

                continue;
            }

            if (inHtmlComment)
            {
                inHtmlComment = !line.Contains("-->", StringComparison.Ordinal);
                continue;
            }

            if (OpeningFence().Match(line) is { Success: true } fence && (indent <= 3 || inList)
                && !(fence.Groups[1].Value[0] == '`' && fence.Groups[2].Value.Contains('`', StringComparison.Ordinal)))
            {
                Flush();
                fenceChar = fence.Groups[1].Value[0];
                fenceLength = fence.Groups[1].Value.Length;
                continue;
            }

            // An HTML comment block (CommonMark type 2) may interrupt a paragraph and ends with the line that contains "-->".
            if (HtmlCommentStart().IsMatch(line))
            {
                Flush();
                inHtmlComment = !line[(line.IndexOf("<!--", StringComparison.Ordinal) + 4)..].Contains("-->", StringComparison.Ordinal);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            // Indented code needs a place where no paragraph is open (an indented line continues a paragraph); inside lists
            // indented lines are list content.
            if (indent >= 4 && block.Length == 0 && !inList)
            {
                continue;
            }

            var listItem = ListItem().IsMatch(line);
            var heading = Heading().IsMatch(line);
            if (listItem || heading)
            {
                Flush();
            }

            inList = listItem || (inList && indent >= 2);
            block.Append(line).Append('\n');
            if (heading)
            {
                // A heading is a block of one line: an indented line right after it starts code.
                Flush();
            }
        }

        Flush();
        return segments;
    }

    private static void ScanInline(string text, List<Guid> ids, List<string> invalid)
    {
        var protectedRanges = ProtectedRanges(text);
        var next = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (next < protectedRanges.Count && i >= protectedRanges[next].Start)
            {
                i = Math.Max(i, protectedRanges[next].End);
                next++;
                continue;
            }

            if (IsEscape(text, i))
            {
                i += 2;
                continue;
            }

            if (text[i] == '[' && ActorLink().Match(text, i) is { Success: true } link)
            {
                var textEnd = link.Groups["text"].Index + link.Groups["text"].Length;
                var linkEnd = link.Index + link.Length;
                var cut = protectedRanges.Skip(next).TakeWhile(r => r.Start < linkEnd).Any(r => r.End > textEnd);
                if (!cut)
                {
                    Add(link.Groups["target"].Value, ids, invalid);
                    i = linkEnd;
                    continue;
                }
            }

            i++;
        }
    }

    private static void Add(string target, List<Guid> ids, List<string> invalid)
    {
        if (Guid.TryParseExact(target, "D", out var id))
        {
            if (!ids.Contains(id))
            {
                ids.Add(id);
            }

            return;
        }

        var shown = ActorScheme + target;
        if (!invalid.Contains(shown, StringComparer.Ordinal))
        {
            invalid.Add(shown);
        }
    }

    /// <summary>Code spans and inline HTML comments, left to right (they bind before links), as [Start, End) ranges.</summary>
    private static List<(int Start, int End)> ProtectedRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        var i = 0;
        while (i < text.Length)
        {
            if (IsEscape(text, i))
            {
                i += 2;
                continue;
            }

            if (text[i] == '`')
            {
                var run = RunLength(text, i, '`');
                var close = FindRun(text, i + run, run);
                if (close < 0)
                {
                    i += run;
                    continue;
                }

                ranges.Add((i, close + run));
                i = close + run;
                continue;
            }

            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0 && text.IndexOf("-->", i + 4, StringComparison.Ordinal) is var end and >= 0)
            {
                ranges.Add((i, end + 3));
                i = end + 3;
                continue;
            }

            i++;
        }

        return ranges;
    }

    /// <summary>A backslash before ASCII punctuation (CommonMark): the character is literal.</summary>
    private static bool IsEscape(string text, int i) =>
        text[i] == '\\' && i + 1 < text.Length && char.IsAscii(text[i + 1]) && (char.IsPunctuation(text[i + 1]) || char.IsSymbol(text[i + 1]));

    private static int RunLength(string text, int start, char c)
    {
        var end = start;
        while (end < text.Length && text[end] == c)
        {
            end++;
        }

        return end - start;
    }

    /// <summary>Start of the next run of exactly <paramref name="length"/> backticks at or after <paramref name="from"/>; -1 if none.</summary>
    private static int FindRun(string text, int from, int length)
    {
        var i = from;
        while (i < text.Length)
        {
            if (text[i] != '`')
            {
                i++;
                continue;
            }

            var run = RunLength(text, i, '`');
            if (run == length)
            {
                return i;
            }

            i += run;
        }

        return -1;
    }

    /// <summary>Columns of leading whitespace; a tab counts to the next multiple of four.</summary>
    private static int Indentation(string line)
    {
        var columns = 0;
        foreach (var c in line)
        {
            if (c == ' ')
            {
                columns++;
            }
            else if (c == '\t')
            {
                columns += 4 - (columns % 4);
            }
            else
            {
                break;
            }
        }

        return columns;
    }

    // [text](actor:target) or [text](<actor:target>), anchored at the match start; the text may span lines of the paragraph.
    [GeneratedRegex(@"\G\[(?<text>[^\[\]]*)\]\(\s*<?actor:(?<target>[^\s()<>]*)>?\s*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex ActorLink();

    [GeneratedRegex(@"^ {0,3}<!--", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlCommentStart();

    [GeneratedRegex(@"<!--[\s\S]*?-->", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"^\s*(`{3,}|~{3,})(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex OpeningFence();

    [GeneratedRegex(@"^\s*(`{3,}|~{3,})[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ClosingFence();

    [GeneratedRegex(@"^ {0,3}([-+*]|\d{1,9}[.)])([ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^ {0,3}#{1,6}([ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*(`{3,}|~{3,}).*$", RegexOptions.CultureInvariant)]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"^\s*((>\s*)+|#{1,6}\s+|[-+*]\s+|\d{1,9}[.)]\s+)*", RegexOptions.CultureInvariant)]
    private static partial Regex BlockMarker();

    [GeneratedRegex(@"!\[([^\[\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\[\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"</?[A-Za-z][^<>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\*\*|__|~~", RegexOptions.CultureInvariant)]
    private static partial Regex StrongOrStrike();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])_|_(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex Underscore();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
