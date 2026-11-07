using System.Diagnostics.CodeAnalysis;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Common;

namespace Agcowork.Domain.Collections;

/// <summary>
/// A typed collection of records inside a space (Notion's "database"). Its <see cref="Schema"/> is an
/// ordered list of <see cref="PropertyDefinition"/>s (JSONB) that always contains the title property;
/// every schema change increments <see cref="SchemaVersion"/>. Archived collections are read-only.
/// </summary>
/// <remarks>Guards throw: user input is validated beforehand in the application layer.</remarks>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Domain term of the architecture (Notion's database); not a .NET collection type.")]
public sealed class Collection : ITenantEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 4000;

    private Collection(
        Guid id,
        Guid workspaceId,
        Guid spaceId,
        string name,
        string description,
        IReadOnlyList<PropertyDefinition> schema,
        Guid createdBy,
        DateTimeOffset createdAt)
    {
        Id = id;
        WorkspaceId = workspaceId;
        SpaceId = spaceId;
        Name = name;
        Description = description;
        Schema = schema;
        TitlePropertyKey = CollectionSchema.TitleKey;
        SchemaVersion = 1;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
    }

    // For EF Core materialization.
    private Collection()
    {
        Name = string.Empty;
        Description = string.Empty;
        Schema = [];
        TitlePropertyKey = CollectionSchema.TitleKey;
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid SpaceId { get; private set; }

    public string Name { get; private set; }

    /// <summary>What the collection holds and when to add a record. Required: agents decide by it.</summary>
    public string Description { get; private set; }

    /// <summary>Ordered property definitions; contains <see cref="TitlePropertyKey"/>.</summary>
    public IReadOnlyList<PropertyDefinition> Schema { get; private set; }

    /// <summary>Key of the title property; always <see cref="CollectionSchema.TitleKey"/>.</summary>
    public string TitlePropertyKey { get; private set; }

    /// <summary>Starts at 1, +1 on every schema change. Also the concurrency token of schema updates.</summary>
    public int SchemaVersion { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool IsArchived => ArchivedAt is not null;

    /// <summary>Creates a collection. The title property is added in front when <paramref name="schema"/> lacks it.</summary>
    public static Collection Create(
        Guid workspaceId,
        Guid spaceId,
        string name,
        string description,
        IReadOnlyList<PropertyDefinition>? schema,
        Guid createdBy,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("Workspace id must be set.", nameof(workspaceId));
        }

        if (spaceId == Guid.Empty)
        {
            throw new ArgumentException("Space id must be set.", nameof(spaceId));
        }

        if (createdBy == Guid.Empty)
        {
            throw new ArgumentException("Creator must be set.", nameof(createdBy));
        }

        var createdAt = timeProvider.GetUtcNow();
        return new Collection(
            Guid.CreateVersion7(createdAt),
            workspaceId,
            spaceId,
            GuardText.Required(name, NameMaxLength, nameof(name)),
            GuardText.Required(description, DescriptionMaxLength, nameof(description)),
            GuardSchema(CollectionSchema.WithTitle(schema ?? [])),
            createdBy,
            createdAt);
    }

    public void Rename(string name)
    {
        EnsureNotArchived();
        Name = GuardText.Required(name, NameMaxLength, nameof(name));
    }

    public void Describe(string description)
    {
        EnsureNotArchived();
        Description = GuardText.Required(description, DescriptionMaxLength, nameof(description));
    }

    /// <summary>
    /// Replaces the schema and increments <see cref="SchemaVersion"/>. Properties kept under the same key
    /// must satisfy <see cref="CollectionSchema.ValidateChange"/>; the title property cannot be removed.
    /// </summary>
    public void ReplaceSchema(IReadOnlyList<PropertyDefinition> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        EnsureNotArchived();
        var guarded = GuardSchema(schema);
        foreach (var before in Schema)
        {
            if (guarded.Find(before.Key) is { } after && CollectionSchema.ValidateChange(before, after) is [var first, ..])
            {
                throw new ArgumentException($"Property '{before.Key}': {first.Message}", nameof(schema));
            }
        }

        Schema = guarded;
        SchemaVersion++;
    }

    /// <summary>Makes the collection read-only. Throws when it already is archived.</summary>
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
            throw new InvalidOperationException($"Collection {Id} is archived and read-only.");
        }
    }

    private static PropertyDefinition[] GuardSchema(IReadOnlyList<PropertyDefinition> schema)
    {
        if (CollectionSchema.Validate(schema) is [var first, ..])
        {
            throw new ArgumentException($"Invalid schema at '{first.Path}': {first.Message}", nameof(schema));
        }

        if (schema.Find(CollectionSchema.TitleKey) is null)
        {
            throw new ArgumentException($"The schema must contain the '{CollectionSchema.TitleKey}' property.", nameof(schema));
        }

        return [.. schema];
    }
}
