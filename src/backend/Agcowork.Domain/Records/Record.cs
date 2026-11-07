using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Common;

namespace Agcowork.Domain.Records;

/// <summary>
/// A record (data set or page) of a space: either an entry of a <see cref="Collection"/> or, with
/// <see cref="CollectionId"/> null, a free page of the space. <see cref="Body"/> is canonical Markdown (E2);
/// <see cref="Properties"/> hold the schema values (JSONB) except the title, which lives in <see cref="Title"/>.
/// Every change increments <see cref="RevisionNo"/>; the caller stores a <see cref="Revision"/> of the new state
/// (<see cref="Revision.Capture"/>). Archived records are read-only until restored.
/// </summary>
/// <remarks>
/// Invariants: collection and parent belong to the same workspace (and the collection to the same space); the
/// parent chain has no cycle and at most <see cref="MaxDepth"/> levels; the title is not empty and at most
/// <see cref="TitleMaxLength"/> characters; the body is at most <see cref="BodyMaxBytes"/> bytes (UTF-8).
/// Guards throw: user input is validated beforehand in the application layer.
/// </remarks>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Domain term of the architecture; C#-only code base.")]
public sealed class Record : ITenantEntity
{
    public const int TitleMaxLength = 500;

    /// <summary>2 MiB of UTF-8.</summary>
    public const int BodyMaxBytes = 2 * 1024 * 1024;

    /// <summary>Maximum nesting: a root page has depth 1.</summary>
    public const int MaxDepth = 32;

    private Record(
        Guid id,
        Guid workspaceId,
        Guid spaceId,
        Guid? collectionId,
        Guid? parentRecordId,
        string title,
        IReadOnlyDictionary<string, JsonElement> properties,
        string body,
        Guid createdBy,
        DateTimeOffset createdAt)
    {
        Id = id;
        WorkspaceId = workspaceId;
        SpaceId = spaceId;
        CollectionId = collectionId;
        ParentRecordId = parentRecordId;
        Title = title;
        Properties = properties;
        Body = body;
        RevisionNo = 1;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        UpdatedBy = createdBy;
        UpdatedAt = createdAt;
    }

    // For EF Core materialization.
    private Record()
    {
        Title = string.Empty;
        Properties = new Dictionary<string, JsonElement>();
        Body = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid SpaceId { get; private set; }

    /// <summary>Collection the record belongs to; null for a free page of the space.</summary>
    public Guid? CollectionId { get; private set; }

    /// <summary>Parent page, if the record is nested.</summary>
    public Guid? ParentRecordId { get; private set; }

    public string Title { get; private set; }

    /// <summary>Property values by key (no <c>title</c>, no nulls), validated against the collection schema by the application layer.</summary>
    public IReadOnlyDictionary<string, JsonElement> Properties { get; private set; }

    /// <summary>Markdown body.</summary>
    public string Body { get; private set; }

    /// <summary>Starts at 1, +1 on every change. Number of the latest <see cref="Revision"/>; send it as <c>expectedRevision</c>.</summary>
    public int RevisionNo { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool IsArchived => ArchivedAt is not null;

    /// <summary>The current content.</summary>
    public RecordSnapshot Snapshot() => new(Title, Properties, Body);

    /// <summary>
    /// Creates a record in <paramref name="spaceId"/>, optionally in <paramref name="collection"/> (same workspace and space)
    /// and below <paramref name="parent"/> (same workspace). <paramref name="parentAncestors"/> are the ids above the parent,
    /// nearest first (the parent's parent, its parent, …).
    /// </summary>
    public static Record Create(
        Guid workspaceId,
        Guid spaceId,
        Collection? collection,
        Record? parent,
        IReadOnlyList<Guid>? parentAncestors,
        string title,
        IReadOnlyDictionary<string, JsonElement>? properties,
        string? body,
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

        if (collection is not null && (collection.WorkspaceId != workspaceId || collection.SpaceId != spaceId))
        {
            throw new ArgumentException("The collection must belong to the same workspace and space as the record.", nameof(collection));
        }

        var createdAt = timeProvider.GetUtcNow();
        var id = Guid.CreateVersion7(createdAt);
        GuardParent(id, workspaceId, parent, parentAncestors);
        return new Record(
            id,
            workspaceId,
            spaceId,
            collection?.Id,
            parent?.Id,
            GuardTitle(title),
            GuardProperties(properties),
            GuardBody(body),
            createdBy,
            createdAt);
    }

    /// <summary>
    /// Replaces the content with <paramref name="next"/>. Returns false (and changes nothing, no new revision) when the
    /// content is the same.
    /// </summary>
    public bool Change(RecordSnapshot next, Guid actorId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(next);
        EnsureNotArchived();
        var title = GuardTitle(next.Title);
        var properties = GuardProperties(next.Properties);
        var body = GuardBody(next.Body);
        if (Snapshot().SameContentAs(new RecordSnapshot(title, properties, body)))
        {
            return false;
        }

        Title = title;
        Properties = properties;
        Body = body;
        Touch(actorId, timeProvider);
        return true;
    }

    /// <summary>Appends <paramref name="markdown"/> to the body, under <paramref name="section"/> if given (see <see cref="MarkdownAppend"/>).</summary>
    public void Append(string markdown, string? section, Guid actorId, TimeProvider timeProvider)
    {
        EnsureNotArchived();
        Body = GuardBody(MarkdownAppend.Append(Body, markdown, section));
        Touch(actorId, timeProvider);
    }

    /// <summary>Makes the record read-only. Throws when it already is archived.</summary>
    public void Archive(Guid actorId, TimeProvider timeProvider)
    {
        EnsureNotArchived();
        Touch(actorId, timeProvider);
        ArchivedAt = UpdatedAt;
    }

    /// <summary>Makes an archived record writable again. Throws when it is not archived.</summary>
    public void Restore(Guid actorId, TimeProvider timeProvider)
    {
        if (!IsArchived)
        {
            throw new InvalidOperationException($"Record {Id} is not archived.");
        }

        ArchivedAt = null;
        Touch(actorId, timeProvider);
    }

    /// <summary>
    /// Moves the record below <paramref name="parent"/> (null: top level). <paramref name="parentAncestors"/> as in
    /// <see cref="Create"/>. Throws when the move would create a cycle or exceed <see cref="MaxDepth"/>.
    /// Does not create a revision: the parent is structure, not content.
    /// </summary>
    public void MoveTo(Record? parent, IReadOnlyList<Guid>? parentAncestors)
    {
        EnsureNotArchived();
        GuardParent(Id, WorkspaceId, parent, parentAncestors);
        ParentRecordId = parent?.Id;
    }

    public static bool IsBodyWithinLimit(string? body) => body is null || Encoding.UTF8.GetByteCount(body) <= BodyMaxBytes;

    private void Touch(Guid actorId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (actorId == Guid.Empty)
        {
            throw new ArgumentException("Actor must be set.", nameof(actorId));
        }

        UpdatedBy = actorId;
        UpdatedAt = timeProvider.GetUtcNow();
        RevisionNo++;
    }

    private void EnsureNotArchived()
    {
        if (IsArchived)
        {
            throw new InvalidOperationException($"Record {Id} is archived and read-only.");
        }
    }

    private static void GuardParent(Guid id, Guid workspaceId, Record? parent, IReadOnlyList<Guid>? parentAncestors)
    {
        if (parent is null)
        {
            return;
        }

        if (parent.WorkspaceId != workspaceId)
        {
            throw new ArgumentException("The parent must belong to the same workspace.", nameof(parent));
        }

        var chain = new List<Guid> { parent.Id };
        chain.AddRange(parentAncestors ?? []);
        if (chain.Contains(id) || chain.Distinct().Count() != chain.Count)
        {
            throw new ArgumentException("The parent chain must not contain a cycle.", nameof(parent));
        }

        // The record itself is one level below the chain.
        if (chain.Count + 1 > MaxDepth)
        {
            throw new ArgumentException($"Records nest at most {MaxDepth} levels deep.", nameof(parent));
        }
    }

    private static string GuardTitle(string title) => GuardText.Required(title, TitleMaxLength, nameof(title));

    private static Dictionary<string, JsonElement> GuardProperties(IReadOnlyDictionary<string, JsonElement>? properties)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in properties ?? new Dictionary<string, JsonElement>())
        {
            if (string.Equals(key, Collections.Schema.CollectionSchema.TitleKey, StringComparison.Ordinal))
            {
                throw new ArgumentException("The title is stored in Title, not in the properties.", nameof(properties));
            }

            // Null clears a value: absent and null are the same.
            if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                result[key] = value.Clone();
            }
        }

        return result;
    }

    private static string GuardBody(string? body)
    {
        if (!IsBodyWithinLimit(body))
        {
            throw new ArgumentException($"Body must not exceed {BodyMaxBytes} bytes (UTF-8).", nameof(body));
        }

        return body ?? string.Empty;
    }
}
