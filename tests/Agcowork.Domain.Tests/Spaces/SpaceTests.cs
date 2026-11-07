using Agcowork.Domain.Spaces;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Domain.Tests.Spaces;

public sealed class SpaceTests
{
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    private static Space Create() => Space.Create(Guid.CreateVersion7(), "  Produkt ", "  ", " 🚀 ", 3, Guid.CreateVersion7(), Clock);

    [Fact]
    public void Create_TrimsAndClearsBlankTexts()
    {
        var space = Create();

        Assert.Equal("Produkt", space.Name);
        Assert.Null(space.Description);
        Assert.Equal("🚀", space.Icon);
        Assert.Equal(3, space.SortOrder);
        Assert.Equal(Clock.GetUtcNow(), space.CreatedAt);
    }

    [Fact]
    public void Create_EmptyWorkspaceOrName_Throws()
    {
        Assert.Throws<ArgumentException>(() => Space.Create(Guid.Empty, "A", null, null, 0, Guid.CreateVersion7(), Clock));
        Assert.Throws<ArgumentException>(() => Space.Create(Guid.CreateVersion7(), " ", null, null, 0, Guid.CreateVersion7(), Clock));
        Assert.Throws<ArgumentException>(() => Space.Create(Guid.CreateVersion7(), new string('a', Space.NameMaxLength + 1), null, null, 0, Guid.CreateVersion7(), Clock));
    }

    [Fact]
    public void Archive_MakesReadOnly()
    {
        var space = Create();
        space.Archive(Clock);

        Assert.True(space.IsArchived);
        Assert.Throws<InvalidOperationException>(() => space.Rename("Other"));
        Assert.Throws<InvalidOperationException>(() => space.MoveTo(1));
    }
}
