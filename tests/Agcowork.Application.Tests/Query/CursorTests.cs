using System.Buffers.Text;
using System.Text;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Query;
using Agcowork.Domain.Collections.Schema;

namespace Agcowork.Application.Tests.Query;

public sealed class CursorTests
{
    private static readonly PropertyDefinition Number = new() { Key = "estimate", Name = "E", Type = PropertyType.Number };

    private static readonly IReadOnlyList<QuerySort> Sorts =
    [
        new(new QueryField("notes", QueryFieldSource.Property, FilterValueType.Text, null), false),
        new(new QueryField("estimate", QueryFieldSource.Property, FilterValueType.Number, Number), true),
        new(new QueryField("due", QueryFieldSource.Property, FilterValueType.Date, null), false),
        new(new QueryField("at", QueryFieldSource.Property, FilterValueType.DateTime, null), false),
        new(new QueryField("created_by", QueryFieldSource.CreatedBy, FilterValueType.Actor, null), false),
        new(new QueryField("title", QueryFieldSource.Title, FilterValueType.Text, null), true),
    ];

    private static readonly QueryCursor Sample = new(
        ["Notiz \"1\" ünicode", 12.50m, new DateOnly(2026, 11, 7), new DateTimeOffset(2026, 11, 7, 13, 30, 0, 123, TimeSpan.Zero).AddTicks(4560), Guid.CreateVersion7(), "ADR 7"],
        new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero).AddTicks(10),
        Guid.CreateVersion7());

    [Fact]
    public void EncodeDecode_RoundTripsEveryKeyType()
    {
        var decoded = QueryCursor.TryDecode(Sample.Encode(Sorts), Sorts);

        Assert.NotNull(decoded);
        Assert.Equal(Sample.SortValues, decoded.SortValues);
        Assert.Equal((Sample.CreatedAt, Sample.Id), (decoded.CreatedAt, decoded.Id));
        Assert.IsType<decimal>(decoded.SortValues[1]);
        Assert.IsType<DateOnly>(decoded.SortValues[2]);
        Assert.IsType<Guid>(decoded.SortValues[4]);
    }

    [Fact]
    public void Encode_IsOpaqueBase64Url()
    {
        var cursor = Sample.Encode(Sorts);

        Assert.DoesNotContain("Notiz", cursor, StringComparison.Ordinal);
        Assert.Matches("^[A-Za-z0-9_-]+$", cursor);
    }

    [Fact]
    public void Decode_NullsOnlyForNullableKeys()
    {
        var withNulls = Sample with { SortValues = [null, null, null, null, Sample.SortValues[4], "x"] };
        var nullTitle = Sample with { SortValues = [.. Sample.SortValues.Take(5), null] };

        Assert.Equal(withNulls.SortValues, QueryCursor.TryDecode(withNulls.Encode(Sorts), Sorts)!.SortValues);
        Assert.Null(QueryCursor.TryDecode(nullTitle.Encode(Sorts), Sorts));
    }

    [Fact]
    public void Decode_OtherSortOrder_Null()
    {
        var cursor = Sample.Encode(Sorts);

        Assert.Null(QueryCursor.TryDecode(cursor, [.. Sorts.Take(5)]));
        Assert.Null(QueryCursor.TryDecode(cursor, [.. Sorts.Take(5), Sorts[5] with { Descending = false }]));
        Assert.Null(QueryCursor.TryDecode(cursor, []));
    }

    public static TheoryData<string> Tampered => new()
    {
        """{"v":2,"s":"","k":[],"c":"2026-11-07T12:00:00.0000000+00:00","i":"0190f5e2-7a4c-7000-8000-000000000001"}""",
        """{"v":1,"s":"","k":[1],"c":"2026-11-07T12:00:00.0000000+00:00","i":"0190f5e2-7a4c-7000-8000-000000000001"}""",
        """{"v":1,"s":"","k":[],"c":"gestern","i":"0190f5e2-7a4c-7000-8000-000000000001"}""",
        """{"v":1,"s":"","k":[],"c":"2026-11-07T12:00:00.0000000+00:00","i":42}""",
        """{"v":"1","s":"","k":[],"c":"2026-11-07T12:00:00.0000000+00:00","i":"0190f5e2-7a4c-7000-8000-000000000001"}""",
        """{"v":1e999}""",
        """[1,2,3]""",
        """null""",
        """{"v":1""",
    };

    [Theory]
    [MemberData(nameof(Tampered))]
    public void Decode_Tampered_Null(string json) =>
        Assert.Null(QueryCursor.TryDecode(Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json)), []));

    [Fact]
    public void Decode_WrongKeyTypes_Null()
    {
        var sorts = Sorts.Take(3).ToList();
        foreach (var keys in new[] { """[1, 2, "2026-11-07"]""", """["a", "2", "2026-11-07"]""", """["a", 2, "07.11.2026"]""", """["a", 2, 20261107]""" })
        {
            var json = $$"""{"v":1,"s":"notes:asc,estimate:desc,due:asc","k":{{keys}},"c":"2026-11-07T12:00:00.0000000+00:00","i":"0190f5e2-7a4c-7000-8000-000000000001"}""";
            Assert.Null(QueryCursor.TryDecode(Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json)), sorts));
        }
    }

    [Theory]
    [InlineData("\\u0000")]
    [InlineData("a\\u0000")]
    [InlineData("\\ud800")]
    [InlineData("\\udfff")]
    public void Decode_UnstorableTextKey_Null(string escaped)
    {
        var sorts = Sorts.Take(1).ToList();
        var json = $$"""{"v":1,"s":"notes:asc","k":["{{escaped}}"],"c":"2026-11-07T12:00:00.0000000+00:00","i":"0190f5e2-7a4c-7000-8000-000000000001"}""";
        var errors = new FieldErrors();

        Assert.Null(QueryCursor.TryDecode(Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json)), sorts));
        Assert.Null(QueryParser.ParseCursor(errors, Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json)), sorts));
        Assert.Equal("invalid_cursor", Assert.Single(errors.Items).Code);
    }

    [Fact]
    public void Decode_RandomInput_NeverThrows()
    {
        var random = new Random(9);
        for (var i = 0; i < 500; i++)
        {
            var bytes = new byte[random.Next(0, 80)];
            random.NextBytes(bytes);
            var text = i % 2 == 0 ? Base64Url.EncodeToString(bytes) : Encoding.Latin1.GetString(bytes);

            _ = QueryCursor.TryDecode(text, Sorts);
        }
    }

    [Fact]
    public void ParseCursor_Invalid_InvalidCursorOnCursorField()
    {
        var errors = new FieldErrors();

        Assert.Null(QueryParser.ParseCursor(errors, "%%%", Sorts));
        Assert.Null(QueryParser.ParseCursor(errors, null, Sorts));

        var error = Assert.Single(errors.Items);
        Assert.Equal(("cursor", "invalid_cursor"), (error.Field, error.Code));
    }
}
