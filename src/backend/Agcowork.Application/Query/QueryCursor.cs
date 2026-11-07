using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

namespace Agcowork.Application.Query;

/// <summary>
/// Keyset position of a query page: the sort values of the last record (as the database computed them), then its
/// creation time and id (the tie-breaker). Values are typed per sort field (<see cref="QueryCursor.KeyType"/>).
/// </summary>
/// <param name="SortValues">One value per sort criterion; null when the record has no value.</param>
/// <param name="CreatedAt">Creation time of the last record.</param>
/// <param name="Id">Id of the last record.</param>
public sealed record QueryCursor(IReadOnlyList<object?> SortValues, DateTimeOffset CreatedAt, Guid Id)
{
    private const int FormatVersion = 1;

    /// <summary>
    /// CLR type of the sort key of a field, shared by the parser (decoding cursors) and the SQL translator (computing keys):
    /// string for text-like values, decimal for numbers, DateOnly for dates, DateTimeOffset for date-times, Guid for the actor columns.
    /// </summary>
    public static Type KeyType(QueryField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Source switch
        {
            QueryFieldSource.Title => typeof(string),
            QueryFieldSource.CreatedAt or QueryFieldSource.UpdatedAt => typeof(DateTimeOffset),
            QueryFieldSource.CreatedBy or QueryFieldSource.UpdatedBy => typeof(Guid),
            _ => field.ValueType switch
            {
                FilterValueType.Number => typeof(decimal),
                FilterValueType.Date => typeof(DateOnly),
                FilterValueType.DateTime => typeof(DateTimeOffset),
                _ => typeof(string),
            },
        };
    }

    /// <summary>Opaque form (base64url of a small JSON document), bound to the sort order it was produced for.</summary>
    public string Encode(IReadOnlyList<QuerySort> sorts)
    {
        ArgumentNullException.ThrowIfNull(sorts);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("v", FormatVersion);
            json.WriteString("s", string.Join(',', sorts.Select(s => s.Signature)));
            json.WriteStartArray("k");
            foreach (var value in SortValues)
            {
                switch (value)
                {
                    case null:
                        json.WriteNullValue();
                        break;
                    case decimal number:
                        json.WriteNumberValue(number);
                        break;
                    case DateOnly date:
                        json.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        break;
                    case DateTimeOffset time:
                        json.WriteStringValue(time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                        break;
                    case Guid id:
                        json.WriteStringValue(id.ToString("D"));
                        break;
                    default:
                        json.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            json.WriteEndArray();
            json.WriteString("c", CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("i", Id.ToString("D"));
            json.WriteEndObject();
        }

        return Base64Url.EncodeToString(stream.ToArray());
    }

    /// <summary>The position of <paramref name="cursor"/>, or null when it is not a cursor of this sort order (never throws).</summary>
    public static QueryCursor? TryDecode(string cursor, IReadOnlyList<QuerySort> sorts)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(sorts);
        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(cursor));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != FormatVersion
                || !root.TryGetProperty("s", out var signature) || signature.GetString() != string.Join(',', sorts.Select(s => s.Signature))
                || !root.TryGetProperty("k", out var keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() != sorts.Count
                || !root.TryGetProperty("c", out var created) || !TryTime(created, out var createdAt)
                || !root.TryGetProperty("i", out var idElement) || idElement.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(idElement.GetString(), "D", out var id))
            {
                return null;
            }

            var values = new List<object?>(sorts.Count);
            var index = 0;
            foreach (var key in keys.EnumerateArray())
            {
                var field = sorts[index++].Field;
                if (key.ValueKind == JsonValueKind.Null)
                {
                    if (!field.IsNullable)
                    {
                        return null;
                    }

                    values.Add(null);
                    continue;
                }

                if (TryValue(key, KeyType(field)) is not { } value)
                {
                    return null;
                }

                values.Add(value);
            }

            return new QueryCursor(values, createdAt, id);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static object? TryValue(JsonElement key, Type type)
    {
        if (type == typeof(decimal))
        {
            return key.ValueKind == JsonValueKind.Number && key.TryGetDecimal(out var number) ? number : null;
        }

        // Values go to the database as parameters: NUL and unpaired surrogates are no valid cursor content.
        if (!QueryText.TryRead(key, out var text))
        {
            return null;
        }

        if (type == typeof(string))
        {
            return text;
        }

        if (type == typeof(DateOnly))
        {
            return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        }

        if (type == typeof(DateTimeOffset))
        {
            return TryTime(key, out var time) ? time : null;
        }

        return Guid.TryParseExact(text, "D", out var id) ? id : null;
    }

    private static bool TryTime(JsonElement element, out DateTimeOffset value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParseExact(element.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        value = parsed.ToUniversalTime();
        return true;
    }
}
