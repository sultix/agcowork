using System.Text.Json;
using Agcowork.Application.Comments;
using Agcowork.Application.Records;

namespace Agcowork.Application.Tests.Records;

public sealed class IdempotencyFingerprintTests
{
    private static readonly Guid Collection = Guid.Parse("0190f3a2-7c4e-7b1a-9d2e-000000000001");
    private static readonly Guid Record = Guid.Parse("0190f3a2-7c4e-7b1a-9d2e-000000000002");

    private static Dictionary<string, JsonElement> Props(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Of_Record_SameRequest_SameHash_LowercaseHex()
    {
        var request = new CreateRecordRequest(Collection, Title: "T", Properties: Props("""{ "a": 1 }"""), Body: "B");

        var hash = IdempotencyFingerprint.Of(request);

        Assert.Equal(hash, IdempotencyFingerprint.Of(request with { }));
        Assert.Matches("^[0-9a-f]{64}$", hash);
        // The key itself is not part of the request.
        Assert.Equal(hash, IdempotencyFingerprint.Of(request with { IdempotencyKey = "k" }));
    }

    [Fact]
    public void Of_Record_KeyOrderAndFormatting_DoNotCount_NestedObjectsIncluded()
    {
        var first = new CreateRecordRequest(Collection, Properties: Props("""{ "a": 1, "j": { "x": [1, { "p": true, "q": null }], "y": "s" } }"""));
        var second = new CreateRecordRequest(Collection, Properties: Props("""{"j":{"y":"s","x":[1,{"q":null,"p":true}]},"a":1}"""));

        Assert.Equal(IdempotencyFingerprint.Of(first), IdempotencyFingerprint.Of(second));
    }

    [Fact]
    public void Of_Record_NullPropertiesEqualEmpty_NullValueDiffersFromOmitted()
    {
        Assert.Equal(IdempotencyFingerprint.Of(new CreateRecordRequest(Collection)), IdempotencyFingerprint.Of(new CreateRecordRequest(Collection, Properties: Props("{}"))));
        Assert.NotEqual(IdempotencyFingerprint.Of(new CreateRecordRequest(Collection)), IdempotencyFingerprint.Of(new CreateRecordRequest(Collection, Properties: Props("""{ "a": null }"""))));
    }

    [Fact]
    public void Of_Record_EveryFieldCounts()
    {
        var baseline = new CreateRecordRequest(Collection, Record, "T", Props("""{ "a": [1, 2] }"""), "B", Record, Record);
        CreateRecordRequest[] others =
        [
            baseline with { CollectionId = null },
            baseline with { SpaceId = null },
            baseline with { Title = "T " },
            baseline with { Title = null },
            baseline with { Properties = Props("""{ "a": [2, 1] }""") },
            baseline with { Properties = Props("""{ "a": [1, 2.5] }""") },
            baseline with { Properties = Props("""{ "a": [1, "2"] }""") },
            baseline with { Properties = Props("""{ "b": [1, 2] }""") },
            baseline with { Body = "B\r\n" },
            baseline with { Body = string.Empty },
            baseline with { Body = null },
            baseline with { TemplateId = null },
            baseline with { ParentRecordId = Collection },
        ];

        var hashes = others.Select(IdempotencyFingerprint.Of).Append(IdempotencyFingerprint.Of(baseline)).ToList();

        Assert.Equal(hashes.Count, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Of_Record_StringEscapesAndNumberSpellings_DoNotCount()
    {
        var plain = new CreateRecordRequest(Collection, Properties: Props("""{ "name": "Café", "n": 1, "x": [10, -0.5], "o": { "ü": "é" } }"""));
        var escaped = new CreateRecordRequest(Collection, Properties: Props("""{ "name": "Caf\u00e9", "n": 1.0, "x": [1e1, -5E-1], "o": { "\u00fc": "\u00E9" } }"""));
        var zero = new CreateRecordRequest(Collection, Properties: Props("""{ "n": 0 }"""));
        var negativeZero = new CreateRecordRequest(Collection, Properties: Props("""{ "n": -0.0 }"""));

        Assert.Equal(IdempotencyFingerprint.Of(plain), IdempotencyFingerprint.Of(escaped));
        Assert.Equal(IdempotencyFingerprint.Of(zero), IdempotencyFingerprint.Of(negativeZero));
    }

    [Fact]
    public void Of_Record_HugeNumbersByText_StringsAndNumbersDiffer()
    {
        string Hash(string json) => IdempotencyFingerprint.Of(new CreateRecordRequest(Collection, Properties: Props(json)));

        Assert.NotEqual(Hash("""{ "n": 1e400 }"""), Hash("""{ "n": 1e401 }"""));
        Assert.Equal(Hash("""{ "n": 1e400 }"""), Hash("""{ "n": 1e400 }"""));
        Assert.NotEqual(Hash("""{ "n": 1 }"""), Hash("""{ "n": "1" }"""));
        Assert.NotEqual(Hash("""{ "n": true }"""), Hash("""{ "n": "true" }"""));
        Assert.NotEqual(Hash("""{ "n": null }"""), Hash("""{ "n": "null" }"""));
    }

    [Fact]
    public void Of_KnownRequest_StableHash()
    {
        // Pinned: stored hashes must not change with the platform (byte order) or a refactoring. A change here breaks retries
        // of requests whose keys were stored before (they would get 409 instead of their first result for up to 24 h).
        var request = new CreateRecordRequest(Collection, Title: "Café", Properties: Props("""{ "b": [1.0, "x"], "a": { "z": null, "y": true } }"""), Body: "Ä\r\n");

        Assert.Equal("609f4510349653d6529c62ac13247e7b19eef6967250a2df4b656359f2fa3adb", IdempotencyFingerprint.Of(request));
        Assert.Equal("071b1cc493ac1346997197b6ecbf62ad36631a35eb7c51e98fc0d7b11f04de4e", IdempotencyFingerprint.Of(new AddCommentRequest(Record, "Grüße 🚀", Collection)));
    }

    [Fact]
    public void Of_Record_TitleParameterAndTitleProperty_AreDifferentFields()
    {
        Assert.NotEqual(
            IdempotencyFingerprint.Of(new CreateRecordRequest(Collection, Title: "T")),
            IdempotencyFingerprint.Of(new CreateRecordRequest(Collection, Properties: Props("""{ "title": "T" }"""))));
    }

    [Fact]
    public void Of_FramedInput_NoAmbiguityBetweenAdjacentTexts()
    {
        Assert.NotEqual(
            IdempotencyFingerprint.Of(new CreateRecordRequest(Collection, Title: "ab", Body: "c")),
            IdempotencyFingerprint.Of(new CreateRecordRequest(Collection, Title: "a", Body: "bc")));
    }

    [Fact]
    public void Of_UnpairedSurrogates_HashWithoutError()
    {
        var hash = IdempotencyFingerprint.Of(new CreateRecordRequest(Collection, Title: "a\uD800", Properties: Props("""{ "a": "\uDC00", "o": { "\uD800": 1 } }""")));

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.NotEqual(hash, IdempotencyFingerprint.Of(new CreateRecordRequest(Collection, Title: "a\uDC00", Properties: Props("""{ "a": "\uDC00", "o": { "\uD800": 1 } }"""))));
    }

    [Fact]
    public void Of_Comment_EveryFieldCounts_KindsDiffer()
    {
        var comment = new AddCommentRequest(Record, "Text", Collection);
        string[] hashes =
        [
            IdempotencyFingerprint.Of(comment),
            IdempotencyFingerprint.Of(comment with { RecordId = Collection }),
            IdempotencyFingerprint.Of(comment with { ParentCommentId = null }),
            IdempotencyFingerprint.Of(comment with { Markdown = "Text " }),
            IdempotencyFingerprint.Of(new CreateRecordRequest()),
            IdempotencyFingerprint.Of(new AddCommentRequest(Guid.Empty, null)),
        ];

        Assert.Equal(hashes.Length, hashes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(hashes[0], IdempotencyFingerprint.Of(comment with { IdempotencyKey = "k" }));
    }
}
