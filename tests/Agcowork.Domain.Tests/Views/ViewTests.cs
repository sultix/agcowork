using System.Text.Json;
using Agcowork.Domain.Views;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Domain.Tests.Views;

public sealed class ViewTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    private static View Create(ViewType type = ViewType.Table, string? groupBy = null, bool isSystem = false, ViewFilterNode? filter = null, IEnumerable<ViewSort>? sorts = null, IEnumerable<string>? visible = null, string? cover = null) =>
        View.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "  Tabelle ", type, filter, sorts, groupBy, visible, cover, false, isSystem, 0, Guid.CreateVersion7(), Clock);

    private static ViewFilterNode Cond(string prop, string op = "eq", object? value = null) => new() { Prop = prop, Op = op, Value = JsonSerializer.SerializeToElement(value ?? "x") };

    [Fact]
    public void ViewTypes_Allowlist_ExcludesTimelineAndCalendar()
    {
        Assert.Equal(["table", "board", "gallery", "list", "document", "raw"], ViewTypes.AllWireNames);
        Assert.True(ViewTypes.TryParse("board", out var board));
        Assert.Equal(ViewType.Board, board);
        Assert.False(ViewTypes.TryParse("timeline", out _));
        Assert.False(ViewTypes.TryParse("calendar", out _));
        Assert.False(ViewTypes.TryParse("Table", out _));
    }

    [Fact]
    public void Create_TrimsNameAndStoresDefaults()
    {
        var view = Create();

        Assert.Equal("Tabelle", view.Name);
        Assert.Empty(view.Sorts);
        Assert.Empty(view.VisibleProperties);
        Assert.Null(view.Filter);
        Assert.Equal(Clock.GetUtcNow(), view.CreatedAt);
        Assert.False(view.NeedsAttention);
    }

    [Fact]
    public void Create_InvalidNameOrIds_Throws()
    {
        Assert.Throws<ArgumentException>(() => View.Create(Guid.Empty, Guid.CreateVersion7(), "A", ViewType.Table, null, null, null, null, null, false, false, 0, Guid.CreateVersion7(), Clock));
        Assert.Throws<ArgumentException>(() => View.Create(Guid.CreateVersion7(), Guid.Empty, "A", ViewType.Table, null, null, null, null, null, false, false, 0, Guid.CreateVersion7(), Clock));
        Assert.Throws<ArgumentException>(() => View.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), " ", ViewType.Table, null, null, null, null, null, false, false, 0, Guid.CreateVersion7(), Clock));
        Assert.Throws<ArgumentException>(() => View.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), new string('a', View.NameMaxLength + 1), ViewType.Table, null, null, null, null, null, false, false, 0, Guid.CreateVersion7(), Clock));
    }

    [Fact]
    public void Create_UnknownType_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create((ViewType)99));

    [Fact]
    public void Create_BoardWithoutGroupBy_Throws()
    {
        Assert.Throws<ArgumentException>(() => Create(ViewType.Board));
        Assert.Throws<ArgumentException>(() => Create(ViewType.Board, groupBy: "  "));
        Assert.Equal("status", Create(ViewType.Board, groupBy: "status").GroupByProperty);
    }

    [Fact]
    public void Configure_EmptyFilter_IsStoredAsNoFilter()
    {
        var view = Create(filter: new ViewFilterNode());

        Assert.Null(view.Filter);
    }

    [Fact]
    public void IsDeletable_SystemViewIsNot()
    {
        Assert.False(Create(isSystem: true).IsDeletable);
        Assert.True(Create().IsDeletable);
    }

    [Fact]
    public void RemoveReferences_CleansVisibleSortsCoverAndFilter()
    {
        var filter = new ViewFilterNode
        {
            And = [Cond("status"), Cond("owner"), new ViewFilterNode { Or = [Cond("owner"), Cond("due")] }],
        };
        var view = Create(filter: filter, sorts: [new ViewSort("owner", "asc"), new ViewSort("due", "desc")], visible: ["title", "owner", "due"], cover: "owner");

        var changed = view.RemoveReferences(key => key == "owner");

        Assert.True(changed);
        Assert.Equal(["title", "due"], view.VisibleProperties);
        Assert.Equal([new ViewSort("due", "desc")], view.Sorts);
        Assert.Null(view.CardCoverProperty);
        Assert.Equal(["status"], view.Filter!.And!.Where(n => n.Prop is not null).Select(n => n.Prop));
        Assert.Equal("due", Assert.Single(Assert.Single(view.Filter.And!, n => n.Or is not null).Or!).Prop);
    }

    [Fact]
    public void RemoveReferences_AllConditionsGone_FilterIsNull()
    {
        var view = Create(filter: new ViewFilterNode { And = [Cond("owner"), new ViewFilterNode { Or = [Cond("owner")] }] });

        view.RemoveReferences(key => key == "owner");

        Assert.Null(view.Filter);
    }

    [Fact]
    public void RemoveReferences_NegatedConditionGone_NotNodeGoes_KeptNotIsRewritten()
    {
        var view = Create(filter: new ViewFilterNode
        {
            And = [new ViewFilterNode { Not = Cond("owner") }, new ViewFilterNode { Not = new ViewFilterNode { Or = [Cond("owner"), Cond("due")] } }, Cond("status")],
        });

        Assert.True(view.RemoveReferences(key => key == "owner"));

        Assert.Equal(2, view.Filter!.And!.Count);
        Assert.Equal("due", Assert.Single(view.Filter.And[0].Not!.Or!).Prop);
        Assert.Equal("status", view.Filter.And[1].Prop);
    }

    [Fact]
    public void Configure_OnlyNot_IsNotEmpty() =>
        Assert.False(new ViewFilterNode { Not = Cond("status") }.IsEmpty);

    [Fact]
    public void RemoveReferences_GroupByGone_BoardKeepsTypeAndNeedsAttention()
    {
        var view = Create(ViewType.Board, groupBy: "status");

        Assert.True(view.RemoveReferences(key => key == "status"));

        Assert.Equal(ViewType.Board, view.Type);
        Assert.Null(view.GroupByProperty);
        Assert.True(view.NeedsAttention);
    }

    [Fact]
    public void RemoveReferences_GroupByNoLongerGroupable_IsCleared()
    {
        var view = Create(ViewType.Board, groupBy: "tags");

        Assert.True(view.RemoveReferences(_ => false, key => key == "tags"));

        Assert.True(view.NeedsAttention);
    }

    [Fact]
    public void RemoveReferences_NothingReferenced_ReturnsFalseAndKeepsFilterInstance()
    {
        var filter = new ViewFilterNode { And = [Cond("status")] };
        var view = Create(filter: filter, visible: ["title"]);

        Assert.False(view.RemoveReferences(key => key == "owner"));
        Assert.Same(filter, view.Filter);
    }
}
