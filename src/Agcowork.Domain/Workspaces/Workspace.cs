using System.Text.RegularExpressions;

namespace Agcowork.Domain.Workspaces;

/// <summary>
/// A workspace is the tenant boundary of agcowork: every workspace-bound entity carries its id
/// (<see cref="Common.ITenantEntity"/>). Archived workspaces are read-only.
/// </summary>
/// <remarks>
/// Guards throw: they protect invariants against programming errors. User input is validated
/// beforehand in the application layer, which reports structured field errors.
/// </remarks>
public sealed partial class Workspace
{
    public const int SlugMaxLength = 63;
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;

    /// <summary>Stored value of <see cref="Settings"/> until settings exist.</summary>
    public const string EmptySettings = "{}";

    private Workspace(Guid id, string slug, string name, string? description, Guid createdBy, DateTimeOffset createdAt)
    {
        Id = id;
        Slug = slug;
        Name = name;
        Description = description;
        Settings = EmptySettings;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    /// <summary>URL-safe identifier used in routes (<c>/w/{slug}/…</c>). Lowercase letters, digits and single hyphens. Immutable.</summary>
    public string Slug { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    /// <summary>Workspace settings as a JSON object (JSONB). Empty for now; typed settings follow when needed.</summary>
    public string Settings { get; private set; }

    /// <summary>Actor that created the workspace (and became its first owner).</summary>
    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Set once the workspace is archived; from then on it is read-only.</summary>
    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool IsArchived => ArchivedAt is not null;

    public static Workspace Create(string slug, string name, string? description, Guid createdBy, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        if (!IsValidSlug(slug))
        {
            throw new ArgumentException(
                $"Slug must be 1-{SlugMaxLength} characters of lowercase letters, digits and single hyphens, not starting or ending with a hyphen.",
                nameof(slug));
        }

        if (createdBy == Guid.Empty)
        {
            throw new ArgumentException("Creator must be set.", nameof(createdBy));
        }

        var createdAt = timeProvider.GetUtcNow();
        return new Workspace(Guid.CreateVersion7(createdAt), slug, GuardName(name), GuardDescription(description), createdBy, createdAt);
    }

    /// <summary>True for 1-63 characters of lowercase letters, digits and single inner hyphens.</summary>
    public static bool IsValidSlug(string? slug) =>
        slug is not null && slug.Length <= SlugMaxLength && SlugPattern().IsMatch(slug);

    public void Rename(string name)
    {
        EnsureNotArchived();
        Name = GuardName(name);
    }

    /// <summary>Sets the description; null, empty or whitespace clears it.</summary>
    public void Describe(string? description)
    {
        EnsureNotArchived();
        Description = GuardDescription(description);
    }

    /// <summary>Makes the workspace read-only. Throws when it already is archived.</summary>
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
            throw new InvalidOperationException($"Workspace '{Slug}' is archived and read-only.");
        }
    }

    private static string GuardName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new ArgumentException($"Name must not exceed {NameMaxLength} characters.", nameof(name));
        }

        return trimmed;
    }

    private static string? GuardDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var trimmed = description.Trim();
        if (trimmed.Length > DescriptionMaxLength)
        {
            throw new ArgumentException($"Description must not exceed {DescriptionMaxLength} characters.", nameof(description));
        }

        return trimmed;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
