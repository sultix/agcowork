using System.Globalization;
using System.Text;

namespace Agcowork.Application.Common;

/// <summary>
/// The one way to put user and agent text (titles, names) into a one-line, structured text for agents: as a double-quoted
/// string literal in JSON syntax. Only what can break the line or the literal is escaped: <c>"</c>, <c>\</c>, characters below
/// U+0020 (line breaks, tabs, ...), the line separators U+2028 and U+2029 and unpaired surrogates (as <c>\uXXXX</c>).
/// Umlauts, emoji and other text stay readable. Markdown characters need no escaping: inside the quotes on one line they cannot
/// start a heading, list item, quote or link structure of the surrounding text.
/// </summary>
public static class QuotedString
{
    /// <summary><paramref name="value"/> as a quoted one-line literal.</summary>
    public static string Of(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = new StringBuilder(value.Length + 2).Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"':
                    text.Append("\\\"");
                    break;
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                case '\b':
                    text.Append("\\b");
                    break;
                case '\f':
                    text.Append("\\f");
                    break;
                case < ' ' or '\u2028' or '\u2029':
                    Unicode(text, c);
                    break;
                case var high when char.IsHighSurrogate(high) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]):
                    text.Append(high).Append(value[++i]);
                    break;
                case var lone when char.IsSurrogate(lone):
                    Unicode(text, lone);
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }

        return text.Append('"').ToString();
    }

    private static void Unicode(StringBuilder text, char c) => text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
}
