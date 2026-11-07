using System.Text;
using System.Text.Json;

namespace Agcowork.Application.Query;

/// <summary>
/// Strings that may reach the database as query parameters. PostgreSQL text cannot hold NUL, and unpaired UTF-16
/// surrogates cannot be encoded as UTF-8: both would fail the query (500), so they are rejected as input instead.
/// </summary>
public static class QueryText
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Whether <paramref name="text"/> has no NUL character and no unpaired surrogate.</summary>
    public static bool IsStorable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            _ = Strict.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>The string value of a JSON string element when it can be read and stored; false otherwise (never throws).</summary>
    public static bool TryRead(JsonElement element, out string text)
    {
        text = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            text = element.GetString()!;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return IsStorable(text);
    }
}
