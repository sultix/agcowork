using Agcowork.Application.Common;

namespace Agcowork.Application.Tests.Common;

public sealed class QuotedStringTests
{
    [Fact]
    public void Of_QuotesBackslashesAndControlCharacters_AreEscaped()
    {
        Assert.Equal("\"a \\\"b\\\" \\\\ \\n\\r\\t\\b\\f \\u0001\\u001f\"", QuotedString.Of("a \"b\" \\ \n\r\t\b\f \u0001\u001f"));
    }

    [Fact]
    public void Of_UmlautsEmojiAndMarkdown_StayReadable()
    {
        Assert.Equal("\"Ärger 🙂 **fett** [x](y) > # - `c` <b>\"", QuotedString.Of("Ärger 🙂 **fett** [x](y) > # - `c` <b>"));
    }

    [Fact]
    public void Of_LineSeparators_AreEscaped()
    {
        Assert.Equal("\"a\\u2028b\\u2029c\"", QuotedString.Of("a\u2028b\u2029c"));
    }

    [Fact]
    public void Of_UnpairedSurrogates_AreEscaped_PairsKept()
    {
        Assert.Equal("\"\\ud83d|\\ude42|🙂\"", QuotedString.Of("\ud83d|\ude42|🙂"));
        Assert.Equal("\"x\\ud83d\"", QuotedString.Of("x\ud83d"));
    }

    [Fact]
    public void Of_Empty_IsTwoQuotes() => Assert.Equal("\"\"", QuotedString.Of(string.Empty));
}
