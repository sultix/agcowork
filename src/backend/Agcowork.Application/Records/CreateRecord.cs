using System.Text.Json;
using Agcowork.Application.Activity;
using Agcowork.Application.Collections;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Records;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Records;

/// <param name="CollectionId">Collection of the new record. Either this or <paramref name="SpaceId"/>.</param>
/// <param name="SpaceId">Space of a free page (no collection). With a collection it may be omitted; if sent it must be the collection's space.</param>
/// <param name="Title">Title, max. 500 characters. Required (may also come as <c>properties.title</c> or from the template).</param>
/// <param name="Properties">Property values by key, validated against the collection schema; omitted properties get their default.</param>
/// <param name="Body">Markdown body, max. 2 MiB. Omitted: the template's body.</param>
/// <param name="TemplateId">Template of the collection to start from; omitted: the collection's default template, if any.</param>
/// <param name="ParentRecordId">Parent page in the same workspace, for nested pages.</param>
/// <param name="IdempotencyKey">From the <c>Idempotency-Key</c> header: the same key by the same actor within 24 h returns the first result.</param>
public sealed record CreateRecordRequest(
    Guid? CollectionId = null,
    Guid? SpaceId = null,
    string? Title = null,
    IReadOnlyDictionary<string, JsonElement>? Properties = null,
    string? Body = null,
    Guid? TemplateId = null,
    Guid? ParentRecordId = null,
    string? IdempotencyKey = null);

/// <summary>
/// Creates a record in a collection, or a free page in a space. Requires <c>write</c>. The template (named or the
/// collection's default) provides starting values: its preset is re-checked against the current schema (values the
/// schema no longer accepts are dropped) and mixed with the sent properties (sent wins, sent null removes a preset value);
/// its body is used when none is sent. Then defaults are applied and all properties are validated (relation and actor
/// targets included). Record, revision 1 and the activity event are stored in one transaction.
/// </summary>
public sealed class CreateRecord(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    ITenantContext tenant,
    PropertyValueValidator validator,
    IActivityRecorder activity,
    TimeProvider timeProvider)
    : IUseCase<CreateRecordRequest, RecordDto>
{
    public async Task<Result<RecordDto>> HandleAsync(CreateRecordRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireWritable() is { } archived)
        {
            return archived;
        }

        if (tenant.RequireScope(WorkspaceScope.Write) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        var idempotencyKey = request.IdempotencyKey;
        if (idempotencyKey is not null && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > IdempotencyKey.KeyMaxLength))
        {
            errors.Add("Idempotency-Key", ErrorCodes.InvalidFormat, $"The Idempotency-Key header must be 1 to {IdempotencyKey.KeyMaxLength} characters, e.g. a UUID.");
            return errors.ToError();
        }

        if (idempotencyKey is not null && !StorableText.IsStorable(idempotencyKey))
        {
            errors.Add("Idempotency-Key", ErrorCodes.InvalidValue, $"The Idempotency-Key header {StorableText.Hint}");
            return errors.ToError();
        }

        // A retry with the same key returns the first result, whatever its body says.
        IdempotencyKey? expiredKey = null;
        if (idempotencyKey is not null)
        {
            var existing = await FindKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null && !existing.IsExpired(timeProvider.GetUtcNow()))
            {
                return await ReplayAsync(existing.RecordId, cancellationToken).ConfigureAwait(false);
            }

            expiredKey = existing;
        }

        if (request.CollectionId is null && request.SpaceId is null)
        {
            errors.Add("collectionId", ErrorCodes.Required, "Send 'collectionId' for a record of a collection, or 'spaceId' for a free page of a space.");
        }

        var sent = RecordInput.Copy(request.Properties);
        var title = RecordInput.TakeTitle(errors, request.Title, sent);
        RecordInput.Body(errors, "body", request.Body);
        if (errors.Any)
        {
            return errors.ToError();
        }

        // Shared collection lock before reading the schema (order: collection → record): a schema change waits until this
        // record is committed and then cleans it up, or this create waits and validates against the new schema.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Collection and space: must exist in this workspace (tenant filter) and be writable.
        Collection? collection = null;
        if (request.CollectionId is { } collectionId)
        {
            await db.LockCollectionSharedAsync(collectionId, cancellationToken).ConfigureAwait(false);
            collection = await db.Collections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == collectionId, cancellationToken).ConfigureAwait(false);
            if (collection is null)
            {
                return new FieldError("collectionId", ErrorCodes.NotFound, "No collection with this id exists in this workspace (see GET /collections).").ToValidationError();
            }

            if (collection.IsArchived)
            {
                return CollectionConflicts.Archived;
            }

            if (request.SpaceId is { } sentSpace && sentSpace != collection.SpaceId)
            {
                return new FieldError("spaceId", ErrorCodes.NotAllowed, $"The collection belongs to space {collection.SpaceId}. Omit 'spaceId' or send that one.").ToValidationError();
            }
        }

        var spaceId = collection?.SpaceId ?? request.SpaceId!.Value;
        var space = await db.Spaces.AsNoTracking().SingleOrDefaultAsync(s => s.Id == spaceId, cancellationToken).ConfigureAwait(false);
        if (space is null)
        {
            return new FieldError("spaceId", ErrorCodes.NotFound, "No space with this id exists in this workspace (see GET /spaces).").ToValidationError();
        }

        if (space.IsArchived)
        {
            return SpaceConflicts.Archived("spaceId");
        }

        var (template, templateError) = await LoadTemplateAsync(collection, request.TemplateId, cancellationToken).ConfigureAwait(false);
        if (templateError is not null)
        {
            return templateError;
        }

        // Values: template preset (re-checked) < sent properties (normalized) < defaults for what is still missing.
        var schema = RecordInput.SchemaOf(collection);
        var properties = template is null ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) : UsablePreset(schema, template.PropertiesPreset);
        title ??= template is not null && template.PropertiesPreset.TryGetValue(CollectionSchema.TitleKey, out var presetTitle) && presetTitle.ValueKind == JsonValueKind.String
            ? presetTitle.GetString()
            : null;
        foreach (var (key, value) in PropertyValueNormalizer.Normalize(schema, sent))
        {
            properties[key] = value;
        }

        properties = PropertyValueNormalizer.ApplyDefaults(schema.Where(p => p.Key != CollectionSchema.TitleKey).ToList(), properties);
        var body = request.Body ?? template?.BodyMarkdown ?? string.Empty;

        RecordInput.Title(errors, title);
        errors.AddRange(await RecordInput.ValidatePropertiesAsync(validator, schema, title ?? string.Empty, properties, PropertyValidationMode.Full, cancellationToken).ConfigureAwait(false));
        RecordInput.Body(errors, "body", body);
        var (parent, ancestors) = await LoadParentAsync(errors, request.ParentRecordId, cancellationToken).ConfigureAwait(false);
        if (errors.Any)
        {
            return new ValidationError([.. errors.Items.DistinctBy(e => (e.Field, e.Code))]);
        }

        var record = Record.Create(tenant.WorkspaceId, spaceId, collection, parent, ancestors, title!, properties, body, currentActor.Id, timeProvider);
        db.Records.Add(record);
        db.Revisions.Add(Revision.Capture(record, RevisionKind.Created, reason: null));
        activity.Record(ActivityTypes.RecordCreated, ActivityTargets.Record, record.Id, record.SpaceId, record.CollectionId, RecordDiff.Created(record.Snapshot()));

        if (idempotencyKey is not null)
        {
            if (expiredKey is not null)
            {
                await db.IdempotencyKeys.Where(k => k.Id == expiredKey.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            db.IdempotencyKeys.Add(IdempotencyKey.Create(tenant.WorkspaceId, currentActor.Id, idempotencyKey, record.Id, timeProvider));
        }

        if (await db.SaveChangesOrConflictAsync(cancellationToken, RecordConflicts.IdempotencyKey).ConfigureAwait(false) is { } conflict)
        {
            // A concurrent request with the same key won: answer with its record (nothing of ours was stored).
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await FindKeyAsync(idempotencyKey!, cancellationToken).ConfigureAwait(false) is { } winner
                ? await ReplayAsync(winner.RecordId, cancellationToken).ConfigureAwait(false)
                : conflict;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return RecordMapper.ToDto(record, schema, includeBody: false);
    }

    /// <summary>
    /// The preset values the current schema still accepts (the schema may have changed since the template was written):
    /// unknown and computed keys and values that fail the type check are dropped; the title is handled separately.
    /// </summary>
    internal static Dictionary<string, JsonElement> UsablePreset(IReadOnlyList<PropertyDefinition> schema, IReadOnlyDictionary<string, JsonElement> preset)
    {
        var usable = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, raw) in PropertyValueNormalizer.Normalize(schema, preset))
        {
            if (key == CollectionSchema.TitleKey)
            {
                continue;
            }

            var check = new FieldErrors();
            if (schema.Find(key) is { IsComputed: false } property && raw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                PropertyTypeRegistry.CheckValue(property, raw, key, check);
                if (!check.Any)
                {
                    usable[key] = raw;
                }
            }
        }

        return usable;
    }

    private Task<IdempotencyKey?> FindKeyAsync(string key, CancellationToken cancellationToken) =>
        db.IdempotencyKeys.AsNoTracking().SingleOrDefaultAsync(k => k.ActorId == currentActor.Id && k.Key == key, cancellationToken);

    private async Task<Result<RecordDto>> ReplayAsync(Guid recordId, CancellationToken cancellationToken)
    {
        var record = await db.Records.AsNoTracking().SingleAsync(r => r.Id == recordId, cancellationToken).ConfigureAwait(false);
        var schema = record.CollectionId is { } collectionId
            ? await db.Collections.AsNoTracking().Where(c => c.Id == collectionId).Select(c => c.Schema).SingleAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return RecordMapper.ToDto(record, schema, includeBody: false);
    }

    private async Task<(Template? Template, Error? Error)> LoadTemplateAsync(Collection? collection, Guid? templateId, CancellationToken cancellationToken)
    {
        if (collection is null)
        {
            return templateId is null
                ? (null, null)
                : (null, new FieldError("templateId", ErrorCodes.NotAllowed, "Free pages have no templates; templates belong to collections.").ToValidationError());
        }

        var templates = db.Templates.AsNoTracking().Where(t => t.CollectionId == collection.Id);
        if (templateId is { } id)
        {
            var named = await templates.SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
            return named is null
                ? (null, new FieldError("templateId", ErrorCodes.NotFound, "The collection has no template with this id (see GET /collections/{id}/templates).").ToValidationError())
                : (named, null);
        }

        return (await templates.SingleOrDefaultAsync(t => t.IsDefault, cancellationToken).ConfigureAwait(false), null);
    }

    /// <summary>The parent and the ids above it (nearest first); errors when it does not exist or nesting gets too deep.</summary>
    private async Task<(Record? Parent, IReadOnlyList<Guid> Ancestors)> LoadParentAsync(FieldErrors errors, Guid? parentRecordId, CancellationToken cancellationToken)
    {
        if (parentRecordId is not { } parentId)
        {
            return (null, []);
        }

        var parent = await db.Records.AsNoTracking().SingleOrDefaultAsync(r => r.Id == parentId, cancellationToken).ConfigureAwait(false);
        if (parent is null)
        {
            errors.Add("parentRecordId", ErrorCodes.NotFound, "No record with this id exists in this workspace.");
            return (null, []);
        }

        var ancestors = new List<Guid>();
        var next = parent.ParentRecordId;
        while (next is { } id && ancestors.Count < Record.MaxDepth && !ancestors.Contains(id))
        {
            ancestors.Add(id);
            next = await db.Records.AsNoTracking().Where(r => r.Id == id).Select(r => r.ParentRecordId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ancestors.Count + 2 > Record.MaxDepth)
        {
            errors.Add("parentRecordId", ErrorCodes.OutOfRange, $"Records nest at most {Record.MaxDepth} levels deep; choose a parent higher up.");
            return (null, []);
        }

        return (parent, ancestors);
    }
}
