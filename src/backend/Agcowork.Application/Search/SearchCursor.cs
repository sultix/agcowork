using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agcowork.Application.Search;

/// <summary>
/// Keyset position of a search page: rank and id of the last hit (order: rank descending, then id). Bound to the parsed
/// query, so a cursor of another search is rejected instead of skipping hits. The rank travels as its exact float bits.
/// </summary>
internal sealed record SearchCursor(float Rank, Guid Id)
{
    private const int FormatVersion = 1;

    public string Encode(string query)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("v", FormatVersion);
            json.WriteString("q", Signature(query));
            json.WriteNumber("r", BitConverter.SingleToInt32Bits(Rank));
            json.WriteString("i", Id.ToString("D"));
            json.WriteEndObject();
        }

        return Base64Url.EncodeToString(stream.ToArray());
    }

    /// <summary>The position, or null when <paramref name="cursor"/> is no cursor of a search for <paramref name="query"/> (never throws).</summary>
    public static SearchCursor? TryDecode(string cursor, string query)
    {
        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(cursor));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != FormatVersion
                || !root.TryGetProperty("q", out var signature) || signature.ValueKind != JsonValueKind.String || signature.GetString() != Signature(query)
                || !root.TryGetProperty("r", out var rank) || rank.ValueKind != JsonValueKind.Number || !rank.TryGetInt32(out var bits)
                || !root.TryGetProperty("i", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParseExact(id.GetString(), "D", out var guid))
            {
                return null;
            }

            var value = BitConverter.Int32BitsToSingle(bits);
            return float.IsFinite(value) ? new SearchCursor(value, guid) : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static string Signature(string query) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(query)).AsSpan(0, 12));
}
