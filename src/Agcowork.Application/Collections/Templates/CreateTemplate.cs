using System.Text.Json;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections.Templates;

/// <param name="CollectionId">Collection the template belongs to.</param>
/// <param name="Name">Display name, unique within the collection, e.g. <c>ADR</c>.</param>
/// <param name="Description">When to use the template. Written for agents.</param>
/// <param name="PropertiesPreset">Property values by key, validated against the schema (required properties may be missing).</param>
/// <param name="BodyMarkdown">Markdown body a new record starts with, e.g. an outline.</param>
/// <param name="IsDefault">Make this the collection's default template (the previous default stops being one).</param>
public sealed record CreateTemplateRequest(
    Guid CollectionId,
    string? Name,
    string? Description = null,
    IReadOnlyDictionary<string, JsonElement>? PropertiesPreset = null,
    string? BodyMarkdown = null,
    bool IsDefault = false);

/// <summary>Creates a record template for a collection. Requires <c>schema</c>.</summary>
public sealed class CreateTemplate(
    IAgcoworkDbContext db,
    ICurrentActor currentActor,
    ITenantContext tenant,
    PropertyValueValidator validator,
    TimeProvider timeProvider)
    : IUseCase<CreateTemplateRequest, TemplateDto>
{
    public async Task<Result<TemplateDto>> HandleAsync(CreateTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireWritable() is { } archived)
        {
            return archived;
        }

        if (tenant.RequireScope(WorkspaceScope.Schema) is { } denied)
        {
            return denied;
        }

        var errors = new FieldErrors();
        TemplateSupport.Name(errors, request.Name);
        TemplateSupport.Texts(errors, request.Description, request.BodyMarkdown);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var (collection, error) = await TemplateSupport.LoadWritableCollectionAsync(db, request.CollectionId, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var (preset, presetErrors) = await TemplateSupport
            .ValidatePresetAsync(validator, collection!, request.PropertiesPreset ?? new Dictionary<string, JsonElement>(), cancellationToken)
            .ConfigureAwait(false);
        if (presetErrors.Count > 0)
        {
            return new ValidationError(presetErrors);
        }

        var name = request.Name!.Trim();
        if (await db.Templates.AnyAsync(t => t.CollectionId == collection!.Id && t.Name == name, cancellationToken).ConfigureAwait(false))
        {
            return CollectionConflicts.TemplateName.Error;
        }

        var template = Template.Create(
            tenant.WorkspaceId, collection!.Id, name, request.Description, preset, request.BodyMarkdown, request.IsDefault, currentActor.Id, timeProvider);
        db.Templates.Add(template);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (template.IsDefault)
        {
            await TemplateSupport.ClearOtherDefaultsAsync(db, collection.Id, template.Id, cancellationToken).ConfigureAwait(false);
        }

        if (await db.SaveChangesOrConflictAsync(cancellationToken, CollectionConflicts.TemplateName, TemplateSupport.DefaultConflict).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return TemplateDto.From(template);
    }
}
