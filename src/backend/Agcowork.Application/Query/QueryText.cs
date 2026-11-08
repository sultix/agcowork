using System.Text.Json;
using Agcowork.Application.Common.Validation;

namespace Agcowork.Application.Query;

/// <summary>
/// Strings that may reach the database as query parameters. PostgreSQL text cannot hold NUL, and unpaired UTF-16
/// surrogates cannot be encoded as UTF-8: both would fail the query (500), so they are rejected as input instead.
/// The rule itself is <see cref="StorableText"/>, shared with all writing use cases.
/// </summary>
public static class QueryText
{
    /// <summary>Whether <paramref name="text"/> has no NUL character and no unpaired surrogate.</summary>
    public static bool IsStorable(string text) => StorableText.IsStorable(text);

    /// <summary>The string value of a JSON string element when it can be read and stored; false otherwise (never throws).</summary>
    public static bool TryRead(JsonElement element, out string text) => StorableText.TryRead(element, out text);
}
