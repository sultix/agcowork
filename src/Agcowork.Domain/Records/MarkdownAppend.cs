using System.Text.RegularExpressions;

namespace Agcowork.Domain.Records;

/// <summary>
/// Appending Markdown to a body without knowing it (architecture §4.2, <c>record_append</c>), e.g. log entries.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>Without a section the text goes to the end of the body.</item>
/// <item>With a section the text goes to the end of the first ATX heading (<c>#</c> … <c>######</c>) whose text equals the
/// section, compared trimmed and case-insensitively; leading <c>#</c> in the section are ignored (<c>"## Log"</c> = <c>"Log"</c>).
/// The section ends before the next heading of the same or a higher level. Headings inside fenced code blocks do not count.</item>
/// <item>A section that does not exist yet is created as a level-2 heading at the end.</item>
/// <item>Blocks are separated by one blank line; the appended text loses leading and trailing blank lines.</item>
/// </list>
/// </remarks>
public static partial class MarkdownAppend
{
    public static string Append(string? body, string markdown, string? section)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        body ??= string.Empty;
        var text = markdown.TrimStart('\r', '\n').TrimEnd();

        var title = NormalizeSection(section);
        if (title is null)
        {
            return Join(body, body.Length, text);
        }

        if (FindSectionEnd(body, title) is { } end)
        {
            return Join(body, end, text);
        }

        return Join(body, body.Length, $"## {title}\n\n{text}");
    }

    /// <summary>The section title without leading <c>#</c> and whitespace; null when empty.</summary>
    public static string? NormalizeSection(string? section)
    {
        var title = section?.Trim().TrimStart('#').Trim();
        return string.IsNullOrEmpty(title) ? null : title;
    }

    private static string Join(string body, int position, string text)
    {
        var before = body[..position].TrimEnd();
        var after = body[position..];
        var result = before.Length > 0 ? $"{before}\n\n{text}\n" : $"{text}\n";
        return after.Length > 0 ? $"{result}\n{after}" : result;
    }

    /// <summary>Offset where the section's content ends (start of the next heading of the same or higher level, or the end), or null.</summary>
    private static int? FindSectionEnd(string body, string title)
    {
        int? level = null;
        char? fence = null;
        var offset = 0;
        while (true)
        {
            var newline = body.IndexOf('\n', offset);
            var lineEnd = newline < 0 ? body.Length : newline;
            var line = body[offset..lineEnd].TrimEnd('\r');

            var fenceMatch = Fence().Match(line);
            if (fenceMatch.Success)
            {
                var marker = fenceMatch.Groups[1].Value[0];
                fence = fence is null ? marker : fence == marker ? null : fence;
            }
            else if (fence is null && Heading().Match(line) is { Success: true } heading)
            {
                var headingLevel = heading.Groups[1].Value.Length;
                if (level is { } found && headingLevel <= found)
                {
                    return offset;
                }

                if (level is null && string.Equals(heading.Groups[2].Value.Trim(), title, StringComparison.OrdinalIgnoreCase))
                {
                    level = headingLevel;
                }
            }

            if (newline < 0)
            {
                break;
            }

            offset = newline + 1;
        }

        return level is null ? null : body.Length;
    }

    [GeneratedRegex(@"^ {0,3}(```|~~~)", RegexOptions.CultureInvariant)]
    private static partial Regex Fence();

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();
}
