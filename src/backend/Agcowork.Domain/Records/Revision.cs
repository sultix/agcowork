using System.Text.Json;
using Agcowork.Domain.Common;

namespace Agcowork.Domain.Records;

/// <summary>
/// Full snapshot of a record after one change (architecture §5: snapshots, diffs are computed for the activity
/// feed). Numbered per record from 1 without gaps; order is <see cref="No"/>, never the id.
/// </summary>
public sealed class Revision : ITenantEntity
{
    public const int ReasonMaxLength = 1000;

    private Revision(
        Guid id,
        Guid workspaceId,
        Guid recordId,
        int no,
        RevisionKind kind,
        string title,
        IReadOnlyDictionary<string, JsonElement> properties,
        string body,
        Guid actorId,
        DateTimeOffset at,
        string? reason)
    {
        Id = id;
        WorkspaceId = workspaceId;
        RecordId = recordId;
        No = no;
        Kind = kind;
        Title = title;
        Properties = properties;
        Body = body;
        ActorId = actorId;
        At = at;
        Reason = reason;
    }

    // For EF Core materialization.
    private Revision()
    {
        Title = string.Empty;
        Properties = new Dictionary<string, JsonElement>();
        Body = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid RecordId { get; private set; }

    /// <summary>Revision number, equal to the record's <see cref="Record.RevisionNo"/> after the change.</summary>
    public int No { get; private set; }

    public RevisionKind Kind { get; private set; }

    public string Title { get; private set; }

    public IReadOnlyDictionary<string, JsonElement> Properties { get; private set; }

    public string Body { get; private set; }

    /// <summary>Who made the change.</summary>
    public Guid ActorId { get; private set; }

    public DateTimeOffset At { get; private set; }

    /// <summary>Why, as given by the caller (e.g. an agent's explanation).</summary>
    public string? Reason { get; private set; }

    public RecordSnapshot Snapshot() => new(Title, Properties, Body);

    /// <summary>Snapshot of <paramref name="record"/> as it is now, attributed to its last updater.</summary>
    public static Revision Capture(Record record, RevisionKind kind, string? reason)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown revision kind.");
        }

        return new Revision(
            Guid.CreateVersion7(record.UpdatedAt),
            record.WorkspaceId,
            record.Id,
            record.RevisionNo,
            kind,
            record.Title,
            new Dictionary<string, JsonElement>(record.Properties, StringComparer.Ordinal),
            record.Body,
            record.UpdatedBy,
            record.UpdatedAt,
            GuardText.Optional(reason, ReasonMaxLength, nameof(reason)));
    }
}
