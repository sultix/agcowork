using Agcowork.Domain.Workspaces;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Domain.Tests.Workspaces;

public sealed class WorkspaceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Creator = Guid.CreateVersion7();

    private static Workspace Create(string slug = "acme", string name = "Acme", string? description = null, FakeTimeProvider? clock = null) =>
        Workspace.Create(slug, name, description, Creator, clock ?? new FakeTimeProvider(Start));

    [Fact]
    public void Create_ValidInput_SetsFieldsFromTimeProvider()
    {
        var workspace = Create(name: "  Acme Corp  ", description: "  Product work  ");

        Assert.Equal("acme", workspace.Slug);
        Assert.Equal("Acme Corp", workspace.Name);
        Assert.Equal("Product work", workspace.Description);
        Assert.Equal(Creator, workspace.CreatedBy);
        Assert.Equal(Start, workspace.CreatedAt);
        Assert.Equal(TimeSpan.Zero, workspace.CreatedAt.Offset);
        Assert.Equal(Workspace.EmptySettings, workspace.Settings);
        Assert.Null(workspace.ArchivedAt);
        Assert.False(workspace.IsArchived);
    }

    [Fact]
    public void Create_ValidInput_IdIsUuidV7()
    {
        Assert.Equal(7, Create().Id.Version);
    }

    [Fact]
    public void Create_AdvancingClock_IdsAreMonotonicInCreationOrder()
    {
        var clock = new FakeTimeProvider(Start);
        var ids = new List<Guid>();

        for (var i = 0; i < 50; i++)
        {
            ids.Add(Create($"ws-{i}", clock: clock).Id);
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
        Assert.ThrowsAny<ArgumentException>(() => Create(slug));
        Assert.False(Workspace.IsValidSlug(slug));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("acme")]
    [InlineData("project-alpha-2")]
    [InlineData("0")]
    public void IsValidSlug_ValidSlug_True(string slug)
    {
        Assert.True(Workspace.IsValidSlug(slug));
    }

    [Fact]
    public void IsValidSlug_MaxLength_TrueAndOneMoreFalse()
    {
        Assert.True(Workspace.IsValidSlug(new string('a', Workspace.SlugMaxLength)));
        Assert.False(Workspace.IsValidSlug(new string('a', Workspace.SlugMaxLength + 1)));
        Assert.False(Workspace.IsValidSlug(null));
    }

    [Fact]
    public void Create_SlugTooLong_Throws()
    {
        Assert.Throws<ArgumentException>(() => Create(new string('a', Workspace.SlugMaxLength + 1)));
    }

    [Fact]
    public void Create_NameTooLong_Throws()
    {
        Assert.Throws<ArgumentException>(() => Create(name: new string('n', Workspace.NameMaxLength + 1)));
    }

    [Fact]
    public void Create_DescriptionTooLong_Throws()
    {
        Assert.Throws<ArgumentException>(() => Create(description: new string('d', Workspace.DescriptionMaxLength + 1)));
    }

    [Fact]
    public void Create_EmptyCreator_Throws()
    {
        Assert.Throws<ArgumentException>(() => Workspace.Create("acme", "Acme", null, Guid.Empty, new FakeTimeProvider(Start)));
    }

    [Fact]
    public void RenameAndDescribe_Valid_TrimsAndClearsEmptyDescription()
    {
        var workspace = Create(description: "old");

        workspace.Rename("  New name ");
        workspace.Describe("   ");

        Assert.Equal("New name", workspace.Name);
        Assert.Null(workspace.Description);
    }

    [Fact]
    public void Archive_Active_SetsArchivedAtFromClock()
    {
        var clock = new FakeTimeProvider(Start);
        var workspace = Create(clock: clock);
        clock.Advance(TimeSpan.FromHours(1));

        workspace.Archive(clock);

        Assert.True(workspace.IsArchived);
        Assert.Equal(Start.AddHours(1), workspace.ArchivedAt);
    }

    [Fact]
    public void Archived_AnyChange_Throws()
    {
        var clock = new FakeTimeProvider(Start);
        var workspace = Create(clock: clock);
        workspace.Archive(clock);

        Assert.Throws<InvalidOperationException>(() => workspace.Rename("Other"));
        Assert.Throws<InvalidOperationException>(() => workspace.Describe("Other"));
        Assert.Throws<InvalidOperationException>(() => workspace.Archive(clock));
    }
}
