using Agcowork.Domain.Common;

namespace Agcowork.Domain.Spaces;

/// <summary>
/// An area of a workspace (project, team, topic) that groups collections and free pages. Archived
/// spaces stay readable but cannot be changed and get no new collections.
/// </summary>
/// <remarks>Guards throw: user input is validated beforehand in the application layer.</remarks>
public sealed class Space : ITenantEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int IconMaxLength = 64;

    private Space(Guid id, Guid workspaceId, string name, string? description, string? icon, int sortOrder, Guid createdBy, DateTimeOffset createdAt)
    {
        Id = id;
        WorkspaceId = workspaceId;
        Name = name;
        Description = description;
        Icon = icon;
        SortOrder = sortOrder;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public string Name { get; private set; }

    /// <summary>What the space is for. Agents read it to decide where things belong.</summary>
    public string? Description { get; private set; }

    /// <summary>Emoji or icon name, shown by the UI.</summary>
    public string? Icon { get; private set; }

    /// <summary>Position in the navigation, ascending.</summary>
    public int SortOrder { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool IsArchived => ArchivedAt is not null;

    public static Space Create(Guid workspaceId, string name, string? description, string? icon, int sortOrder, Guid createdBy, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must be set.", nameof(workspaceId));
        }

        if (createdBy == Guid.Empty)
        {
            throw new ArgumentException("Creator must be set.", nameof(createdBy));
        }

        var createdAt = timeProvider.GetUtcNow();
        return new Space(
            Guid.CreateVersion7(createdAt),
            workspaceId,
            GuardText.Required(name, NameMaxLength, nameof(name)),
            GuardText.Optional(description, DescriptionMaxLength, nameof(description)),
            GuardText.Optional(icon, IconMaxLength, nameof(icon)),
            sortOrder,
            createdBy,
            createdAt);
    }

    public void Rename(string name)
    {
        EnsureNotArchived();
        Name = GuardText.Required(name, NameMaxLength, nameof(name));
    }

    /// <summary>Sets the description; null, empty or whitespace clears it.</summary>
    public void Describe(string? description)
    {
        EnsureNotArchived();
        Description = GuardText.Optional(description, DescriptionMaxLength, nameof(description));
    }

    /// <summary>Sets the icon; null, empty or whitespace clears it.</summary>
    public void ChangeIcon(string? icon)
    {
        EnsureNotArchived();
        Icon = GuardText.Optional(icon, IconMaxLength, nameof(icon));
    }

    public void MoveTo(int sortOrder)
    {
        EnsureNotArchived();
        SortOrder = sortOrder;
    }

    /// <summary>Makes the space read-only. Throws when it already is archived.</summary>
    public void Archive(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        EnsureNotArchived();
        ArchivedAt = timeProvider.GetUtcNow();
    }

    private void EnsureNotArchived()
    {
        if (IsArchived)
        {
            throw new InvalidOperationException($"Space {Id} is archived and read-only.");
        }
    }
}
