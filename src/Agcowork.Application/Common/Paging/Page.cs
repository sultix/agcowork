using System.Buffers.Text;
using System.Text;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;

namespace Agcowork.Application.Common.Paging;

/// <summary>One page of a list. Pass <paramref name="NextCursor"/> as <c>cursor</c> to get the next page.</summary>
/// <param name="Items">The entries of this page.</param>
/// <param name="NextCursor">Opaque cursor of the next page; null on the last page.</param>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>Limits and opaque cursors of paginated lists (keyset pagination, never offsets).</summary>
public static class Paging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>The effective limit; adds <c>out_of_range</c> for values outside 1..<see cref="MaxLimit"/>.</summary>
    public static int Limit(FieldErrors errors, int? limit)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (limit is { } value && (value < 1 || value > MaxLimit))
        {
            errors.Add("limit", ErrorCodes.OutOfRange, $"'limit' must be between 1 and {MaxLimit}.");
            return DefaultLimit;
        }

        return limit ?? DefaultLimit;
    }

    /// <summary>Opaque form of a cursor position (base64url), so clients do not build cursors themselves.</summary>
    public static string Encode(string position) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(position));

    /// <summary>The position of a cursor; adds <c>invalid_format</c> when it was not produced by <see cref="Encode"/> or <paramref name="parse"/> rejects it.</summary>
    public static T? Decode<T>(FieldErrors errors, string? cursor, Func<string, T?> parse)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(parse);
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        T? position = null;
        try
        {
            position = parse(Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)));
        }
        catch (FormatException)
        {
        }

        if (position is null)
        {
            errors.Add("cursor", ErrorCodes.InvalidFormat, "'cursor' is not valid. Pass 'nextCursor' of the previous page unchanged, or omit it for the first page.");
        }

        return position;
    }
}
