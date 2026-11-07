using System.Text.Json;
using Agcowork.Domain.Records;

namespace Agcowork.Domain.Tests.Records;

public sealed class RecordDiffTests
{
    private static RecordSnapshot Snapshot(string title, string properties, string body) =>
        new(title, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(properties)!, body);

    [Fact]
    public void Between_ListsOldAndNewPerChangedKey_AndBodyDelta()
    {
        var before = Snapshot("A", """{ "status": "todo", "tags": ["x"], "same": { "a": 1, "b": 2 }, "gone": 3 }""", "Hello world");
        var after = Snapshot("B", """{ "status": "done", "tags": ["x"], "same": { "b": 2, "a": 1 }, "owner": "o" }""", "Hello brave world");

        var diff = RecordDiff.Between(before, after);

        Assert.Equal("""{"old":"A","new":"B"}""", diff["title"]!.ToJsonString());
        var properties = diff["properties"]!.AsObject();
        Assert.Equal(["gone", "owner", "status"], properties.Select(p => p.Key));
        Assert.Equal("""{"old":3,"new":null}""", properties["gone"]!.ToJsonString());
        Assert.Equal("""{"old":null,"new":"o"}""", properties["owner"]!.ToJsonString());
        Assert.Equal("""{"old":"todo","new":"done"}""", properties["status"]!.ToJsonString());
        Assert.True(diff["bodyChanged"]!.GetValue<bool>());
        Assert.Equal("""{"added":6,"removed":0}""", diff["bodyDelta"]!.ToJsonString());
    }

    [Fact]
    public void Between_NothingChanged_EmptyObject()
    {
        var snapshot = Snapshot("A", """{ "n": 1 }""", "B");

        Assert.Empty(RecordDiff.Between(snapshot, snapshot with { }));
        Assert.Empty(RecordDiff.ChangedKeys(snapshot, snapshot));
    }

    [Fact]
    public void ChangedKeys_TitlePropertiesBody()
    {
        var keys = RecordDiff.ChangedKeys(Snapshot("A", """{ "b": 1, "a": 1 }""", "x"), Snapshot("B", """{ "b": 2, "a": 1, "c": true }""", "y"));

        Assert.Equal(["title", "b", "c", "body"], keys);
    }

    [Theory]
    [InlineData("abc", "abcdef", 3, 0)]
    [InlineData("abcdef", "abc", 0, 3)]
    [InlineData("abXdef", "abYYdef", 2, 1)]
    [InlineData("", "new", 3, 0)]
    [InlineData("aaa", "aaaa", 1, 0)]
    public void BodyDelta_CountsOutsideCommonPrefixAndSuffix(string before, string after, int added, int removed) =>
        Assert.Equal((added, removed), RecordDiff.BodyDelta(before, after));

    [Fact]
    public void Created_OldValuesNull()
    {
        var diff = RecordDiff.Created(Snapshot("A", """{ "s": "x" }""", "Body"));

        Assert.Equal("""{"old":null,"new":"A"}""", diff["title"]!.ToJsonString());
        Assert.Equal("""{"old":null,"new":"x"}""", diff["properties"]!["s"]!.ToJsonString());
        Assert.Equal("""{"added":4,"removed":0}""", diff["bodyDelta"]!.ToJsonString());
    }
}
