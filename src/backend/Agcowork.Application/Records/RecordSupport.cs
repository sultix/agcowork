using System.Text.Json;
using Agcowork.Application.Collections;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Agcowork.Application.Records;

/// <summary>Conflicts of the record module. Codes are part of the API contract.</summary>
public static class RecordConflicts
{
    public const string RevisionMismatchCode = "revision_mismatch";

    public static ConflictError Archived { get; } =
        new("record_archived", "The record is archived and read-only. Restore it first (POST …/restore).");

    public static ConflictError NotArchived { get; } =
        new("record_not_archived", "The record is not archived; there is nothing to restore.");

    /// <summary>Another change of the same idempotency key is in progress or was stored concurrently.</summary>
    /// <summary>
    /// 409 <c>idempotency_key_reused</c>: the key already belongs to another request (creating a record, or adding a comment to
    /// another record). Keys are shared by both kinds of request (AP-029).
    /// </summary>
    public static ConflictError IdempotencyKeyReused(string field) =>
        new(
            "idempotency_key_reused",
            "This Idempotency-Key was already used within 24 h for another request (creating a record, or a comment on another record). Use a new key for every request.",
            field);

    public static UniqueConflict IdempotencyKey { get; } = new(
        "ix_idempotency_keys_workspace_id_actor_id_key",
        new ConflictError("idempotency_key_in_use", "A request with this Idempotency-Key was processed concurrently. Retry the same request to get its result."));

    /// <summary>
    /// 409 <c>revision_mismatch</c>: the record is no longer at <c>expectedRevision</c>. Carries <c>currentRevision</c> and
    /// <c>changedKeys</c> (title, property keys, body changed since the expected revision; null when that revision does not
    /// exist), so the caller can decide whether its change still applies.
    /// </summary>
    public static ConflictError RevisionMismatch(int currentRevision, IReadOnlyList<string>? changedKeys) =>
        new(
            RevisionMismatchCode,
            changedKeys is null
                ? $"The record is at revision {currentRevision}, not at the expected one. Read it again (GET …/records/{{id}}) and retry with expectedRevision {currentRevision}."
                : $"The record changed since the expected revision; it is now at revision {currentRevision}. Changed: {(changedKeys.Count == 0 ? "nothing" : string.Join(", ", changedKeys))}. "
                    + $"If your change does not touch these, retry with expectedRevision {currentRevision}; otherwise read the record again first.",
            "expectedRevision",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["currentRevision"] = currentRevision,
                ["changedKeys"] = changedKeys,
            });
}

/// <summary>Input rules shared by the record use cases.</summary>
internal static class RecordInput
{
    public const string PropertiesField = "properties";
    public const int SectionMaxLength = 500;

    /// <summary>Free pages have no collection: their only property is the title.</summary>
    public static IReadOnlyList<PropertyDefinition> FreePageSchema { get; } = [PropertyDefinition.Title()];

    public static IReadOnlyList<PropertyDefinition> SchemaOf(Collection? collection) => collection?.Schema ?? FreePageSchema;

    /// <summary>
    /// The title may come as <c>title</c> or, following the JSON Schema of the collection, as <c>properties.title</c>.
    /// Removes it from <paramref name="properties"/> and returns it; both with different values is an error.
    /// </summary>
    public static string? TakeTitle(FieldErrors errors, string? title, Dictionary<string, JsonElement> properties)
    {
        if (!properties.Remove(CollectionSchema.TitleKey, out var fromProperties))
        {
            return title;
        }

        var field = $"{PropertiesField}.{CollectionSchema.TitleKey}";
        if (fromProperties.ValueKind != JsonValueKind.String)
        {
            errors.Add(
                field,
                fromProperties.ValueKind == JsonValueKind.Null ? ErrorCodes.Required : ErrorCodes.InvalidType,
                "The title is required and must be a string.");
            return title;
        }

        if (!StorableText.TryRead(fromProperties, out var value))
        {
            errors.Add(field, ErrorCodes.InvalidValue, $"'{field}' {StorableText.Hint}");
            return title;
        }

        if (title is not null && !string.Equals(title.Trim(), value.Trim(), StringComparison.Ordinal))
        {
            errors.Add(field, ErrorCodes.Conflict, "Send the title once: either as 'title' or as 'properties.title'.");
        }

        return title ?? value;
    }

    public static void Title(FieldErrors errors, string? title)
    {
        if (errors.Required("title", title) && errors.Storable("title", title))
        {
            errors.MaxLength("title", title, Record.TitleMaxLength);
        }
    }

    public static void Body(FieldErrors errors, string field, string? body)
    {
        if (!errors.Storable(field, body))
        {
            return;
        }

        if (!Record.IsBodyWithinLimit(body))
        {
            errors.Add(field, ErrorCodes.TooLong, $"The body must not exceed {Record.BodyMaxBytes / (1024 * 1024)} MiB (UTF-8). Split the content into several records.");
        }
    }

    public static void Reason(FieldErrors errors, string? reason)
    {
        if (errors.Storable("reason", reason))
        {
            errors.MaxLength("reason", reason, Revision.ReasonMaxLength);
        }
    }

    /// <summary>
    /// The one place that validates record properties together with the title. <see cref="PropertyValidationMode.Full"/>
    /// requires <c>title</c> in the checked dictionary, but records keep it in their own column: the title is mixed in
    /// under its key for the check, and errors on it are reported on <c>title</c> (its own length limit,
    /// <see cref="Record.TitleMaxLength"/>, is checked by <see cref="Title"/>). Other errors name <c>properties.&lt;key&gt;</c>.
    /// <c>title</c> is null in a patch that leaves it unchanged.
    /// </summary>
    public static async Task<IReadOnlyList<FieldError>> ValidatePropertiesAsync(
        PropertyValueValidator validator,
        IReadOnlyList<PropertyDefinition> schema,
        string? title,
        IReadOnlyDictionary<string, JsonElement> properties,
        PropertyValidationMode mode,
        CancellationToken cancellationToken)
    {
        // A title that cannot be stored is reported by Title(); it is not mixed in (it could not even be serialized).
        var titleStorable = title is null || StorableText.IsStorable(title);
        var checkedValues = new Dictionary<string, JsonElement>(properties, StringComparer.Ordinal);
        if (title is not null && titleStorable)
        {
            checkedValues[CollectionSchema.TitleKey] = JsonSerializer.SerializeToElement(title.Trim());
        }

        var titleField = $"{PropertiesField}.{CollectionSchema.TitleKey}";
        var errors = await validator.ValidateAsync(schema, checkedValues, mode, PropertiesField, cancellationToken).ConfigureAwait(false);
        return [.. errors
            .Where(e => !(e.Field == titleField && (e.Code == ErrorCodes.TooLong || !titleStorable)))
            .Select(e => e.Field == titleField ? e with { Field = "title" } : e)];
    }

    /// <summary>Copy of the input without changing it; null becomes empty.</summary>
    public static Dictionary<string, JsonElement> Copy(IReadOnlyDictionary<string, JsonElement>? properties) =>
        properties is null ? new(StringComparer.Ordinal) : new(properties, StringComparer.Ordinal);
}

/// <summary>Loading and saving in the writing record use cases.</summary>
internal static class RecordWrites
{
    /// <summary>
    /// Opens the transaction of a record change and takes the locks in the fixed order collection → record: a shared lock on
    /// the record's collection (<see cref="IAgcoworkDbContext.LockCollectionSharedAsync"/>; free pages have none), so the
    /// change is validated against a schema that cannot change until commit, then the record row
    /// (<see cref="IAgcoworkDbContext.LockRecordAsync"/>), so concurrent changes of the same record run one after the other:
    /// revision numbers stay gapless, appends never get lost and the <c>expectedRevision</c> check sees the latest state.
    /// </summary>
    public static async Task<(IDbContextTransaction Transaction, Record? Record)> BeginAsync(IAgcoworkDbContext db, Guid recordId, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // The collection of a record never changes, so it can be read before any lock.
        var collectionId = await db.Records.AsNoTracking()
            .Where(r => r.Id == recordId)
            .Select(r => r.CollectionId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (collectionId is { } id)
        {
            await db.LockCollectionSharedAsync(id, cancellationToken).ConfigureAwait(false);
        }

        await db.LockRecordAsync(recordId, cancellationToken).ConfigureAwait(false);
        var record = await db.Records.SingleOrDefaultAsync(r => r.Id == recordId, cancellationToken).ConfigureAwait(false);
        return (transaction, record);
    }

    /// <summary>
    /// The collection of a record (null for free pages). Records of an archived collection or space are read-only:
    /// 409 <c>collection_archived</c> / <c>space_archived</c>.
    /// </summary>
    public static async Task<(Collection? Collection, Error? Error)> LoadWritableContainerAsync(IAgcoworkDbContext db, Record record, CancellationToken cancellationToken)
    {
        Collection? collection = null;
        if (record.CollectionId is { } collectionId)
        {
            collection = await db.Collections.AsNoTracking().SingleAsync(c => c.Id == collectionId, cancellationToken).ConfigureAwait(false);
            if (collection.IsArchived)
            {
                return (null, CollectionConflicts.Archived);
            }
        }

        var spaceArchived = await db.Spaces.AsNoTracking()
            .Where(s => s.Id == record.SpaceId)
            .Select(s => s.ArchivedAt != null)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        return spaceArchived ? (null, SpaceConflicts.Archived()) : (collection, null);
    }

    /// <summary>
    /// Saves and commits. The row lock makes concurrent changes impossible; should the revision check still fail (a
    /// write that bypassed the lock), the answer is <c>revision_mismatch</c> rather than a 500.
    /// </summary>
    public static async Task<Error?> SaveAsync(IAgcoworkDbContext db, IDbContextTransaction transaction, Guid recordId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException
            or UniqueConstraintViolationException { ConstraintName: "ix_revisions_workspace_id_record_id_no" })
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var current = await db.Records.AsNoTracking().Where(r => r.Id == recordId).Select(r => (int?)r.RevisionNo).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return current is { } revision ? RecordConflicts.RevisionMismatch(revision, null) : new NotFoundError("record");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }
}
