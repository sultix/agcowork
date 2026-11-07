using System.Globalization;
using System.Text.Json;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Records;

namespace Agcowork.Application.Records;

/// <summary>A record (data set or page). The single JSON shape of records for REST, MCP (AP-011) and the query endpoint (AP-009).</summary>
/// <param name="Id">Record id.</param>
/// <param name="SpaceId">Space of the record.</param>
/// <param name="CollectionId">Collection of the record; null for a free page of the space.</param>
/// <param name="ParentRecordId">Parent page, if nested.</param>
/// <param name="Title">Title (the collection's <c>title</c> property).</param>
/// <param name="Properties">
/// Property values by key in schema order, as a JSON object; absent keys have no value. The title is not repeated here.
/// Computed properties of the schema (created_at, updated_at, created_by, updated_by) are filled from the record and read-only.
/// </param>
/// <param name="Body">Markdown body; null unless requested (<c>includeBody=true</c>).</param>
/// <param name="Revision">Current revision number; send it as <c>expectedRevision</c> to avoid overwriting concurrent changes.</param>
/// <param name="CreatedBy">Actor that created the record.</param>
/// <param name="CreatedAt">Creation time (UTC).</param>
/// <param name="UpdatedBy">Actor of the last change.</param>
/// <param name="UpdatedAt">Time of the last change (UTC).</param>
/// <param name="ArchivedAt">Set when archived; archived records are read-only until restored.</param>
/// <param name="History">Latest revisions, newest first; only with <c>includeHistory</c>.</param>
public sealed record RecordDto(
    Guid Id,
    Guid SpaceId,
    Guid? CollectionId,
    Guid? ParentRecordId,
    string Title,
    IReadOnlyDictionary<string, JsonElement> Properties,
    string? Body,
    int Revision,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    Guid UpdatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    IReadOnlyList<RevisionSummaryDto>? History = null);

/// <summary>One revision without its content.</summary>
/// <param name="No">Revision number, from 1.</param>
/// <param name="Kind">created, updated, appended, archived or restored.</param>
/// <param name="ActorId">Who made the change.</param>
/// <param name="At">When (UTC).</param>
/// <param name="Reason">Why, as given by the caller.</param>
public sealed record RevisionSummaryDto(int No, RevisionKind Kind, Guid ActorId, DateTimeOffset At, string? Reason);

/// <summary>A full revision: the record's content after the change.</summary>
/// <param name="RecordId">Record id.</param>
/// <param name="No">Revision number, from 1.</param>
/// <param name="Kind">created, updated, appended, archived or restored.</param>
/// <param name="Title">Title at this revision.</param>
/// <param name="Properties">Property values at this revision.</param>
/// <param name="Body">Markdown body at this revision.</param>
/// <param name="ActorId">Who made the change.</param>
/// <param name="At">When (UTC).</param>
/// <param name="Reason">Why, as given by the caller.</param>
public sealed record RevisionDto(
    Guid RecordId,
    int No,
    RevisionKind Kind,
    string Title,
    IReadOnlyDictionary<string, JsonElement> Properties,
    string Body,
    Guid ActorId,
    DateTimeOffset At,
    string? Reason);

/// <summary>Maps records and revisions to their DTOs; the one place that decides the JSON shape.</summary>
public static class RecordMapper
{
    /// <param name="record">The record.</param>
    /// <param name="schema">Schema of its collection (null for free pages): orders the properties.</param>
    /// <param name="includeBody">Whether to include the body.</param>
    /// <param name="history">Revisions to attach, newest first.</param>
    public static RecordDto ToDto(Record record, IReadOnlyList<PropertyDefinition>? schema, bool includeBody, IReadOnlyList<RevisionSummaryDto>? history = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new RecordDto(
            record.Id,
            record.SpaceId,
            record.CollectionId,
            record.ParentRecordId,
            record.Title,
            Ordered(WithComputed(record, schema), schema),
            includeBody ? record.Body : null,
            record.RevisionNo,
            record.CreatedBy,
            record.CreatedAt,
            record.UpdatedBy,
            record.UpdatedAt,
            record.ArchivedAt,
            history);
    }

    public static RevisionSummaryDto ToSummary(Revision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return new RevisionSummaryDto(revision.No, revision.Kind, revision.ActorId, revision.At, revision.Reason);
    }

    public static RevisionDto ToDto(Revision revision, IReadOnlyList<PropertyDefinition>? schema)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return new RevisionDto(
            revision.RecordId,
            revision.No,
            revision.Kind,
            revision.Title,
            Ordered(revision.Properties, schema),
            revision.Body,
            revision.ActorId,
            revision.At,
            revision.Reason);
    }

    /// <summary>
    /// Stored values plus the computed properties the schema defines, filled from the record's columns under their schema
    /// key (date-times as UTC ISO 8601, actors as ids). They are never stored and cannot be written (<c>read_only</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> WithComputed(Record record, IReadOnlyList<PropertyDefinition>? schema)
    {
        ArgumentNullException.ThrowIfNull(record);
        var computed = (schema ?? []).Where(p => p.IsComputed).ToList();
        if (computed.Count == 0)
        {
            return record.Properties;
        }

        var values = new Dictionary<string, JsonElement>(record.Properties, StringComparer.Ordinal);
        foreach (var property in computed)
        {
            values[property.Key] = property.Type switch
            {
                PropertyType.CreatedAt => JsonSerializer.SerializeToElement(Time(record.CreatedAt)),
                PropertyType.UpdatedAt => JsonSerializer.SerializeToElement(Time(record.UpdatedAt)),
                PropertyType.CreatedBy => JsonSerializer.SerializeToElement(record.CreatedBy.ToString("D")),
                _ => JsonSerializer.SerializeToElement(record.UpdatedBy.ToString("D")),
            };
        }

        return values;
    }

    private static string Time(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    /// <summary>Values in schema order; keys the schema does not know (any more) follow in ordinal order.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Ordered(IReadOnlyDictionary<string, JsonElement> properties, IReadOnlyList<PropertyDefinition>? schema)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var position = (schema ?? []).Select((p, i) => (p.Key, i)).ToDictionary(p => p.Key, p => p.i, StringComparer.Ordinal);
        var ordered = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in properties
                     .OrderBy(p => position.TryGetValue(p.Key, out var i) ? i : int.MaxValue)
                     .ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            ordered[key] = value;
        }

        return ordered;
    }
}
