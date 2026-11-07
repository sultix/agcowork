using System.Text.RegularExpressions;

namespace Agcowork.Domain.Workspaces;

/// <summary>
/// A workspace is the tenant boundary of agcowork. Minimal shape for phase 0;
/// memberships, archiving and templates follow in phase 1.
/// </summary>
public sealed partial class Workspace
{
    public const int SlugMaxLength = 63;
    public const int NameMaxLength = 200;

    private Workspace(Guid id, string slug, string name, DateTimeOffset createdAt)
    {
        Id = id;
        Slug = slug;
        Name = name;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    /// <summary>URL-safe identifier used in routes (<c>/w/{slug}/…</c>). Lowercase letters, digits and single hyphens.</summary>
    public string Slug { get; private set; }

    public string Name { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Workspace Create(string slug, string name, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (slug.Length > SlugMaxLength || !SlugPattern().IsMatch(slug))
        {
            throw new ArgumentException(
                $"Slug must be 1-{SlugMaxLength} characters of lowercase letters, digits and single hyphens, not starting or ending with a hyphen.",
                nameof(slug));
        }

        var trimmedName = name.Trim();
        if (trimmedName.Length > NameMaxLength)
        {
            throw new ArgumentException($"Name must not exceed {NameMaxLength} characters.", nameof(name));
        }

        var createdAt = timeProvider.GetUtcNow();
        return new Workspace(Guid.CreateVersion7(createdAt), slug, trimmedName, createdAt);
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
