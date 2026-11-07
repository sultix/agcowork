using Agcowork.Domain.Spaces;

namespace Agcowork.Application.Spaces;

/// <summary>A space of the workspace.</summary>
/// <param name="Id">Space id; collections reference it as <c>spaceId</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">What the space is for.</param>
/// <param name="Icon">Emoji or icon name.</param>
/// <param name="SortOrder">Position in the navigation, ascending.</param>
/// <param name="CreatedBy">Actor that created the space.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="ArchivedAt">Set when archived; archived spaces are read-only.</param>
public sealed record SpaceDto(
    Guid Id,
    string Name,
    string? Description,
    string? Icon,
    int SortOrder,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt)
{
    public static SpaceDto From(Space space)
    {
        ArgumentNullException.ThrowIfNull(space);
        return new SpaceDto(space.Id, space.Name, space.Description, space.Icon, space.SortOrder, space.CreatedBy, space.CreatedAt, space.ArchivedAt);
    }
}
