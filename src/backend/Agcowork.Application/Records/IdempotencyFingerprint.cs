using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Agcowork.Application.Records;

/// <summary>
/// The hash an <c>Idempotency-Key</c> remembers of its request (AP-035): SHA-256 over the kind of request, its target and its
/// content, as lowercase hex. A request with the same key and the same hash is a retry and gets the first result; the same key
/// with another hash is 409 <c>idempotency_key_reused</c> ("same key, different request").
/// </summary>
/// <remarks>
/// What counts (decided in AP-035, documented for agents in the descriptions of <c>record_create</c> and <c>comment_add</c>):
/// <list type="bullet">
/// <item><c>record_create</c>: collectionId, spaceId, title, properties, body, templateId, parentRecordId, skipTemplate (when true).
/// <c>comment_add</c>: recordId, parentCommentId, markdown.</item>
/// <item>Omitted and null are the same; for ids that is the only form anyway. <c>properties: null</c> equals <c>{}</c>; a
/// property sent as <c>null</c> is a value (it removes a template preset) and differs from an omitted one.</item>
/// <item>Texts (title, body, markdown, string values and keys in properties) count by their characters exactly as sent: no
/// trimming, no line-ending or Unicode normalization. JSON escapes do not count, so <c>"Café"</c> and <c>"Caf\u00e9"</c> are the
/// same (clients and adapters escape differently); a string with an unpaired surrogate escape, which has no characters, counts
/// by its JSON text.</item>
/// <item>Properties count as JSON values: the order of keys (also in nested objects) and the formatting between tokens do not
/// count. Numbers count by their decimal value (<c>1</c>, <c>1.0</c> and <c>1e0</c> are the same); numbers outside the decimal
/// range count by their JSON text; beyond 28 significant digits a number counts rounded (two such requests replay instead of
/// conflicting).</item>
/// <item>The <c>title</c> parameter and <c>properties.title</c> are separate fields, as sent.</item>
/// </list>
/// The hash is computed before validation from the request as sent, so it does not depend on schema, template or defaults.
/// Input is framed (type tag, length prefix), so no two different requests produce the same byte stream. Texts are hashed as
/// UTF-16 code units in little-endian byte order on every platform, so even unpaired surrogates (rejected later by validation)
/// hash without error, and stored hashes do not depend on the server's byte order.
/// </remarks>
public static class IdempotencyFingerprint
{
    private const string RecordCreateKind = "record_create";
    private const string CommentAddKind = "comment_add";

    /// <summary>Hash of a <c>record_create</c> request.</summary>
    public static string Of(CreateRecordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = new Hasher(RecordCreateKind);
        hash.Id(request.CollectionId);
        hash.Id(request.SpaceId);
        hash.Text(request.Title);
        hash.Properties(request.Properties);
        hash.Text(request.Body);
        hash.Id(request.TemplateId);
        hash.Id(request.ParentRecordId);
        if (request.SkipTemplate)
        {
            // Only written when set, so hashes of keys stored before AP-042 (always false) stay valid.
            hash.Text("skipTemplate");
        }

        return hash.Finish();
    }

    /// <summary>Hash of a <c>comment_add</c> request.</summary>
    public static string Of(Comments.AddCommentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = new Hasher(CommentAddKind);
        hash.Id(request.RecordId);
        hash.Id(request.ParentCommentId);
        hash.Text(request.Markdown);
        return hash.Finish();
    }

    /// <summary>Writes framed values into SHA-256: every value starts with a tag byte, texts and lists with their length.</summary>
    private sealed class Hasher
    {
        private const byte NullTag = 0;
        private const byte IdTag = 1;
        private const byte TextTag = 2;
        private const byte ObjectTag = 3;
        private const byte ArrayTag = 4;
        private const byte LiteralTag = 5;
        private const byte StringTag = 6;
        private const byte RawStringTag = 7;
        private const byte NumberTag = 8;
        private const byte RawNumberTag = 9;

        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public Hasher(string kind) => Text(kind);

        public void Id(Guid? id)
        {
            if (id is not { } value)
            {
                Tag(NullTag);
                return;
            }

            Tag(IdTag);
            Span<byte> bytes = stackalloc byte[16];
            value.TryWriteBytes(bytes, bigEndian: true, out _);
            _hash.AppendData(bytes);
        }

        public void Text(string? text)
        {
            if (text is null)
            {
                Tag(NullTag);
                return;
            }

            Tag(TextTag);
            Length(text.Length);
            if (BitConverter.IsLittleEndian)
            {
                _hash.AppendData(MemoryMarshal.AsBytes(text.AsSpan()));
                return;
            }

            // UTF-16LE everywhere: big-endian platforms swap each code unit.
            Span<byte> unit = stackalloc byte[2];
            foreach (var c in text)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(unit, c);
                _hash.AppendData(unit);
            }
        }

        public void Properties(IReadOnlyDictionary<string, JsonElement>? properties)
        {
            var entries = (properties ?? new Dictionary<string, JsonElement>()).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            Tag(ObjectTag);
            Length(entries.Count);
            foreach (var (key, value) in entries)
            {
                Text(key);
                Json(value);
            }
        }

        public string Finish()
        {
            var digest = _hash.GetHashAndReset();
            _hash.Dispose();
            return Convert.ToHexStringLower(digest);
        }

        private void Json(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var members = Members(value);
                    Tag(ObjectTag);
                    Length(members.Count);
                    foreach (var (raw, name, member) in members)
                    {
                        Tag(raw ? RawStringTag : StringTag);
                        Text(name);
                        Json(member);
                    }

                    break;
                case JsonValueKind.Array:
                    Tag(ArrayTag);
                    Length(value.GetArrayLength());
                    foreach (var item in value.EnumerateArray())
                    {
                        Json(item);
                    }

                    break;
                case JsonValueKind.Undefined:
                    Tag(NullTag);
                    break;
                case JsonValueKind.String:
                    if (TryGetString(value, out var text))
                    {
                        Tag(StringTag);
                        Text(text);
                    }
                    else
                    {
                        Tag(RawStringTag);
                        Text(value.GetRawText());
                    }

                    break;
                case JsonValueKind.Number:
                    var number = value.GetRawText();
                    if (decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        Tag(NumberTag);
                        Text(Canonical(parsed));
                    }
                    else
                    {
                        Tag(RawNumberTag);
                        Text(number);
                    }

                    break;
                default:
                    // true, false, null: one spelling each.
                    Tag(LiteralTag);
                    Text(value.GetRawText());
                    break;
            }
        }

        /// <summary>
        /// Members of an object ordered by name; a name that cannot be unescaped to valid text (unpaired surrogate escape) is
        /// taken as the member's raw JSON text and marked as raw.
        /// </summary>
        private static List<(bool Raw, string Name, JsonElement Value)> Members(JsonElement value)
        {
            var members = new List<(bool Raw, string Name, JsonElement Value)>();
            foreach (var member in value.EnumerateObject())
            {
                try
                {
                    members.Add((false, member.Name, member.Value));
                }
                catch (InvalidOperationException)
                {
                    members.Add((true, member.ToString(), member.Value));
                }
            }

            return [.. members.OrderBy(m => m.Raw).ThenBy(m => m.Name, StringComparer.Ordinal)];
        }

        /// <summary>The characters of a JSON string, escapes resolved; false when they are no valid text (unpaired surrogate escape).</summary>
        private static bool TryGetString(JsonElement value, out string text)
        {
            try
            {
                text = value.GetString()!;
                return true;
            }
            catch (InvalidOperationException)
            {
                text = string.Empty;
                return false;
            }
        }

        /// <summary>One spelling per decimal value: no exponent, no trailing zeros, no negative zero.</summary>
        private static string Canonical(decimal value) =>
            value == 0m ? "0" : value.ToString("0.############################", CultureInfo.InvariantCulture);

        private void Tag(byte tag) => _hash.AppendData([tag]);

        private void Length(int length)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, length);
            _hash.AppendData(bytes);
        }
    }
}
