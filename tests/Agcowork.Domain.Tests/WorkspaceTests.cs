using Agcowork.Domain.Workspaces;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Domain.Tests;

public sealed class WorkspaceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ValidInput_SetsFieldsFromTimeProvider()
    {
        var clock = new FakeTimeProvider(Start);

        var workspace = Workspace.Create("acme", "  Acme Corp  ", clock);

        Assert.Equal("acme", workspace.Slug);
        Assert.Equal("Acme Corp", workspace.Name);
        Assert.Equal(Start, workspace.CreatedAt);
        Assert.Equal(TimeSpan.Zero, workspace.CreatedAt.Offset);
    }

    [Fact]
    public void Create_ValidInput_IdIsUuidV7()
    {
        var workspace = Workspace.Create("acme", "Acme", new FakeTimeProvider(Start));

        Assert.Equal(7, workspace.Id.Version);
    }

    [Fact]
    public void Create_AdvancingClock_IdsAreMonotonicInCreationOrder()
    {
        var clock = new FakeTimeProvider(Start);
        var ids = new List<Guid>();

        for (var i = 0; i < 50; i++)
        {
            ids.Add(Workspace.Create($"ws-{i}", "Workspace", clock).Id);
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        // UUIDv7 carries a big-endian millisecond timestamp, so the canonical string form sorts by creation time.
        var sorted = ids.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToList();
        Assert.Equal(ids, sorted);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Acme")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac--me")]
    [InlineData("ac me")]
    [InlineData("äcme")]
    [InlineData("acme\n")]
    [InlineData("acme\r\n")]
    public void Create_InvalidSlug_Throws(string slug)
    {
        Assert.ThrowsAny<ArgumentException>(() => Workspace.Create(slug, "Acme", new FakeTimeProvider(Start)));
    }

    [Fact]
    public void Create_SlugTooLong_Throws()
    {
        var slug = new string('a', Workspace.SlugMaxLength + 1);

        Assert.Throws<ArgumentException>(() => Workspace.Create(slug, "Acme", new FakeTimeProvider(Start)));
    }

    [Fact]
    public void Create_NameTooLong_Throws()
    {
        var name = new string('n', Workspace.NameMaxLength + 1);

        Assert.Throws<ArgumentException>(() => Workspace.Create("acme", name, new FakeTimeProvider(Start)));
    }
}
