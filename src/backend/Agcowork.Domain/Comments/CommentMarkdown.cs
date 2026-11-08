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
/// <item>Block quotes are containers (AP-035): a line starting with <c>&gt;</c> (after at most three spaces) opens a quote; the
/// marker and one optional space (or one column of a tab) are removed and the rest is scanned by all rules above, so
/// <c>&gt; ```</c> opens a fenced code block and <c>&gt;     [@a](actor:…)</c> is indented code inside the quote, while
/// <c>&gt; [@a](actor:…)</c> mentions. Quotes nest (<c>&gt;&gt;</c>, <c>&gt; &gt;</c>). A quote goes on with lines that carry the
/// marker, and with lines without marker that lazily continue an open paragraph inside it (non-blank and starting no fence, list
/// item, heading or HTML comment); any other line (e.g. a blank one) ends it, and a fence or HTML comment still open inside the
/// quote ends with it. A link never spans the quote boundary. Quotes inside list items (<c>- &gt; …</c>) are read as list text.
/// Quotes nest at most <see cref="MaxQuoteDepth"/> deep: further markers on a line are text of the innermost quote (so
/// <c>&gt;</c> × 33 followed by five spaces and a link is a paragraph whose link mentions, while <c>&gt;</c> × 32 with the same rest
/// is indented code). Lines are scanned in one pass per line, without recursion.</item>
/// <item>Openers without closer cost one search: once no <c>--&gt;</c> follows an inline <c>&lt;!--</c>, later ones are not
/// searched again.</item>
/// <item>Code spans and HTML comments bind before links: a link whose brackets or destination are cut by a code span or comment
/// (<c>[a`](actor:…)`</c>) is no link; a code span completely inside the link text (<c>[`@x`](actor:…)</c>) is fine.</item>
/// <item>A backslash escapes ASCII punctuation: <c>\[</c> starts no link, <c>\`</c> no code span.</item>
/// </list>
/// </remarks>
public static partial class CommentMarkdown
{
    public const string ActorScheme = "actor:";

    /// <summary>Block quotes nest at most this deep; further markers are text of the innermost quote.</summary>
    public const int MaxQuoteDepth = 32;

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

        var lines = WithoutHtmlComments(markdown.Replace("\r\n", "\n", StringComparison.Ordinal)).Split('\n')
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

    /// <summary>
    /// <paramref name="markdown"/> without <c>&lt;!-- … --&gt;</c>; an unclosed <c>&lt;!--</c> stays. One pass (a lazy regex would
    /// search to the end again for every unclosed opener).
    /// </summary>
    private static string WithoutHtmlComments(string markdown)
    {
        var start = markdown.IndexOf("<!--", StringComparison.Ordinal);
        if (start < 0)
        {
            return markdown;
        }

        var text = new StringBuilder(markdown.Length);
        var from = 0;
        while (start >= 0)
        {
            var end = markdown.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            text.Append(markdown, from, start - from);
            from = end + 3;
            start = markdown.IndexOf("<!--", from, StringComparison.Ordinal);
        }

        return text.Append(markdown, from, markdown.Length - from).ToString();
    }

    /// <summary>
    /// The inline content of the document outside code blocks and HTML comment blocks, one entry per block (paragraph, heading,
    /// list item), block quotes included (their content is scanned by the same rules, see <see cref="BlockScanner"/>).
    /// </summary>
    private static List<string> InlineSegments(string markdown)
    {
        var scanner = new BlockScanner();
        foreach (var raw in markdown.Split('\n'))
        {
            scanner.Feed(raw.TrimEnd('\r'));
        }

        return scanner.Finish();
    }

    /// <summary>
    /// Where the content of a line starts inside its innermost container, without copying the line: <paramref name="Start"/> is the
    /// index of the first character after the container's markers and the leading spaces and tabs, <paramref name="Indent"/> the
    /// columns of that whitespace (a tab counts to the next multiple of four of the document line), <paramref name="Column"/> the
    /// column of <paramref name="Start"/> in the document line.
    /// </summary>
    private readonly record struct LineView(int Indent, int Start, int Column)
    {
        /// <summary>The line as it stands in the document (outside any quote).</summary>
        public static LineView Of(string line)
        {
            var (start, column) = SkipWhitespace(line, 0, 0);
            return new LineView(column, start, column);
        }

        public bool IsBlank(string line) => line.AsSpan(Start).IsWhiteSpace();

        /// <summary>A block quote marker: <c>&gt;</c> after at most three columns of indentation.</summary>
        public bool HasQuoteMarker(string line) => Indent <= 3 && Start < line.Length && line[Start] == '>';

        /// <summary>
        /// The content after the quote marker: one optional space follows the marker (a tab after it counts to the next tab stop,
        /// one column of it being the optional space); the remaining whitespace is the indentation inside the quote.
        /// </summary>
        public LineView AfterQuoteMarker(string line)
        {
            var afterMarker = Column + 1;
            var (start, column) = SkipWhitespace(line, Start + 1, afterMarker);
            var whitespace = column - afterMarker;
            return new LineView(whitespace - (whitespace > 0 ? 1 : 0), start, column);
        }

        public bool StartsWith(string line, string text) => string.CompareOrdinal(line, Start, text, 0, text.Length) == 0;

        private static (int Start, int Column) SkipWhitespace(string line, int index, int column)
        {
            while (index < line.Length && line[index] is ' ' or '\t')
            {
                column += line[index] == '\t' ? 4 - (column % 4) : 1;
                index++;
            }

            return (index, column);
        }
    }

    /// <summary>
    /// Whether the line opens a fenced code block: a fence of three or more backticks or tildes, indented at most three columns
    /// (or any amount inside a list); a backtick fence whose info string contains a backtick is no fence.
    /// </summary>
    private static bool IsOpeningFence(string line, LineView view, bool inList, out Match fence)
    {
        fence = OpeningFence().Match(line, view.Start);
        return fence.Success && (view.Indent <= 3 || inList)
            && !(fence.Groups[1].Value[0] == '`' && fence.Groups[2].Value.Contains('`', StringComparison.Ordinal));
    }

    private static bool IsHtmlCommentStart(string line, LineView view) => view.Indent <= 3 && view.StartsWith(line, "<!--");

    private static bool IsListItem(string line, LineView view) => view.Indent <= 3 && ListItem().IsMatch(line, view.Start);

    private static bool IsHeading(string line, LineView view) => view.Indent <= 3 && Heading().IsMatch(line, view.Start);

    /// <summary>Lines that start a block of their own and therefore never continue a quoted paragraph lazily.</summary>
    private static bool StartsBlock(string line, LineView view) =>
        IsOpeningFence(line, view, inList: false, out _) || IsHtmlCommentStart(line, view) || IsListItem(line, view) || IsHeading(line, view);

    /// <summary>
    /// Splits the lines of the document into inline segments. Block quotes (CommonMark) are containers: a line starting with a
    /// quote marker opens a nested container that gets the content after the marker, so fences, indented code and HTML comments
    /// inside the quote follow the same rules. The quote goes on while lines carry the marker (<c>&gt;&gt;</c> or <c>&gt; &gt;</c>
    /// nests deeper) and, without marker, while they lazily continue an open paragraph inside it (non-blank, starting no fence,
    /// list item, heading or HTML comment). Any other line ends the quote; an open fence or HTML comment inside it ends with it.
    /// </summary>
    /// <remarks>
    /// Iterative over a stack of containers (the document, then one per open quote), at most <see cref="MaxQuoteDepth"/> quotes
    /// deep: a marker beyond that depth is text of the innermost quote. Lines are read by offset (<see cref="LineView"/>), never
    /// copied per level, so a line of thousands of markers costs one pass.
    /// </remarks>
    private sealed class BlockScanner
    {
        private readonly List<string> _segments = [];
        private readonly List<Container> _containers = [new()];

        private Container Innermost => _containers[^1];

        public void Feed(string line)
        {
            var view = LineView.Of(line);

            // Down through the open quotes: each one continues while the line carries its marker.
            var level = 0;
            while (level + 1 < _containers.Count)
            {
                if (view.HasQuoteMarker(line))
                {
                    view = view.AfterQuoteMarker(line);
                    level++;
                    continue;
                }

                if (!view.IsBlank(line) && Innermost.HasOpenParagraph && !StartsBlock(line, view))
                {
                    // Lazy continuation: the paragraph of the innermost quote goes on.
                    level = _containers.Count - 1;
                    break;
                }

                CloseAbove(level);
                break;
            }

            while (true)
            {
                var container = _containers[level];
                if (container.FenceChar is { } open)
                {
                    if (ClosingFence().Match(line, view.Start) is { Success: true } close
                        && close.Groups[1].Value[0] == open && close.Groups[1].Value.Length >= container.FenceLength)
                    {
                        container.FenceChar = null;
                    }

                    return;
                }

                if (container.InHtmlComment)
                {
                    container.InHtmlComment = line.IndexOf("-->", view.Start, StringComparison.Ordinal) < 0;
                    return;
                }

                if (IsOpeningFence(line, view, container.InList, out var fence))
                {
                    Flush(container);
                    container.FenceChar = fence.Groups[1].Value[0];
                    container.FenceLength = fence.Groups[1].Value.Length;
                    return;
                }

                // An HTML comment block (CommonMark type 2) may interrupt a paragraph and ends with the line that contains "-->".
                if (IsHtmlCommentStart(line, view))
                {
                    Flush(container);
                    container.InHtmlComment = line.IndexOf("-->", view.Start + 4, StringComparison.Ordinal) < 0;
                    return;
                }

                if (view.IsBlank(line))
                {
                    Flush(container);
                    return;
                }

                // A block quote may interrupt a paragraph; its content is scanned as a container of its own. Beyond the depth
                // limit the marker is text.
                if (view.HasQuoteMarker(line) && _containers.Count <= MaxQuoteDepth)
                {
                    Flush(container);
                    container.InList = container.InList && view.Indent >= 2;
                    _containers.Add(new Container());
                    view = view.AfterQuoteMarker(line);
                    level++;
                    continue;
                }

                // Indented code needs a place where no paragraph is open (an indented line continues a paragraph); inside lists
                // indented lines are list content.
                if (view.Indent >= 4 && container.Block.Length == 0 && !container.InList)
                {
                    return;
                }

                var listItem = IsListItem(line, view);
                var heading = IsHeading(line, view);
                if (listItem || heading)
                {
                    Flush(container);
                }

                container.InList = listItem || (container.InList && view.Indent >= 2);
                container.Block.Append(line.AsSpan(view.Start)).Append('\n');
                if (heading)
                {
                    // A heading is a block of one line: an indented line right after it starts code.
                    Flush(container);
                }

                return;
            }
        }

        /// <summary>Ends the document: open quotes end, open blocks become segments.</summary>
        public List<string> Finish()
        {
            CloseAbove(-1);
            return _segments;
        }

        /// <summary>Closes the containers deeper than <paramref name="level"/>, innermost first.</summary>
        private void CloseAbove(int level)
        {
            for (var i = _containers.Count - 1; i > level; i--)
            {
                Flush(_containers[i]);
                if (i > 0)
                {
                    _containers.RemoveAt(i);
                }
            }
        }

        private void Flush(Container container)
        {
            if (container.Block.Length > 0)
            {
                _segments.Add(container.Block.ToString());
                container.Block.Clear();
            }
        }

        /// <summary>State of one container: the open block and whether a fence, HTML comment or list is open in it.</summary>
        private sealed class Container
        {
            public StringBuilder Block { get; } = new();

            public char? FenceChar { get; set; }

            public int FenceLength { get; set; }

            public bool InHtmlComment { get; set; }

            public bool InList { get; set; }

            /// <summary>Whether a line without quote marker may continue the paragraph of this container.</summary>
            public bool HasOpenParagraph => FenceChar is null && !InHtmlComment && Block.Length > 0;
        }
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
        var commentsUnclosed = false;
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

            if (!commentsUnclosed && string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                var end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end >= 0)
                {
                    ranges.Add((i, end + 3));
                    i = end + 3;
                    continue;
                }

                // No "-->" up to the end: no later "<!--" closes either (searching again for each would be quadratic).
                commentsUnclosed = true;
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

    // [text](actor:target) or [text](<actor:target>), anchored at the match start; the text may span lines of the paragraph.
    [GeneratedRegex(@"\G\[(?<text>[^\[\]]*)\]\(\s*<?actor:(?<target>[^\s()<>]*)>?\s*\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex ActorLink();


    // The block patterns are matched at the start of a line's content (LineView.Start, after markers and indentation).
    [GeneratedRegex(@"\G\s*(`{3,}|~{3,})(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex OpeningFence();

    [GeneratedRegex(@"\G\s*(`{3,}|~{3,})[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ClosingFence();

    [GeneratedRegex(@"\G([-+*]|\d{1,9}[.)])([ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\G#{1,6}([ \t]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*(`{3,}|~{3,}).*$", RegexOptions.CultureInvariant)]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"^\s*((>\s*)+|#{1,6}\s+|[-+*]\s+|\d{1,9}[.)]\s+)*", RegexOptions.CultureInvariant)]
    private static partial Regex BlockMarker();

    // Destinations without parentheses: a destination scan stops at the next "(" instead of running to the end for every opener.
    [GeneratedRegex(@"!\[([^\[\]]*)\]\([^()]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\[\]]*)\]\([^()]*\)", RegexOptions.CultureInvariant)]
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
