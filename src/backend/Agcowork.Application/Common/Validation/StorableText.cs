using System.Text;
using System.Text.Json;

namespace Agcowork.Application.Common.Validation;

/// <summary>
/// Text that PostgreSQL can store and query: no NUL character (<c>text</c> and <c>jsonb</c> reject it) and no unpaired UTF-16
/// surrogate (it cannot be encoded as UTF-8). Both would fail the statement with a 500, so every input that reaches the
/// database as text is checked here and rejected as a field error <c>invalid_value</c> with <see cref="Hint"/>
/// (<see cref="FieldErrors.Storable"/>). The one place for this rule; <c>QueryText</c> of the filter DSL delegates to it.
/// </summary>
public static class StorableText
{
    /// <summary>The end of every error message about such text, after the field name.</summary>
    public const string Hint =
        "must not contain NUL characters (\\u0000) or unpaired surrogates (\\ud800-\\udfff): they cannot be stored. "
        + "Remove them, or send characters outside the BMP (e.g. emoji) as a complete surrogate pair.";

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

    /// <summary>
    /// The value of a JSON string element when it can be read and stored; false otherwise, also for non-strings (never
    /// throws: <see cref="JsonElement.GetString"/> throws for an escaped unpaired surrogate such as <c>"\ud800"</c>).
    /// </summary>
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

    /// <summary>Whether every string in <paramref name="element"/> (values and member names, at any depth) can be read and stored.</summary>
    public static bool IsStorable(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return TryRead(element, out _);
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!IsStorable(item))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    if (!IsStorableName(member) || !IsStorable(member.Value))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }

    private static bool IsStorableName(JsonProperty member)
    {
        try
        {
            return IsStorable(member.Name);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
