using System.Text.Json;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Schema;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Collections.Templates;

/// <summary>Partial update: null leaves a field unchanged.</summary>
/// <param name="CollectionId">Collection of the template.</param>
/// <param name="TemplateId">Template to change.</param>
/// <param name="Name">New display name.</param>
/// <param name="Description">New description; an empty string clears it.</param>
/// <param name="PropertiesPreset">New complete preset (replaces the old one); validated against the schema.</param>
/// <param name="BodyMarkdown">New body (replaces the old one); an empty string clears it.</param>
/// <param name="IsDefault">true makes it the default (the previous default stops being one); false removes the flag.</param>
public sealed record UpdateTemplateRequest(
    Guid CollectionId,
    Guid TemplateId,
    string? Name = null,
    string? Description = null,
    IReadOnlyDictionary<string, JsonElement>? PropertiesPreset = null,
    string? BodyMarkdown = null,
    bool? IsDefault = null);

/// <summary>Changes a record template. Requires <c>schema</c>.</summary>
public sealed class UpdateTemplate(IAgcoworkDbContext db, ITenantContext tenant, PropertyValueValidator validator)
    : IUseCase<UpdateTemplateRequest, TemplateDto>
{
    public async Task<Result<TemplateDto>> HandleAsync(UpdateTemplateRequest request, CancellationToken cancellationToken)
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
        if (request.Name is not null)
        {
            TemplateSupport.Name(errors, request.Name);
        }

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

        var template = await db.Templates
            .SingleOrDefaultAsync(t => t.Id == request.TemplateId && t.CollectionId == collection!.Id, cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
        {
            return new NotFoundError("template");
        }

        if (request.PropertiesPreset is not null)
        {
            var (preset, presetErrors) = await TemplateSupport.ValidatePresetAsync(validator, collection!, request.PropertiesPreset, cancellationToken).ConfigureAwait(false);
            if (presetErrors.Count > 0)
            {
                return new ValidationError(presetErrors);
            }

            template.ChangePreset(preset);
        }

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (await db.Templates.AnyAsync(t => t.CollectionId == template.CollectionId && t.Name == name && t.Id != template.Id, cancellationToken).ConfigureAwait(false))
            {
                return CollectionConflicts.TemplateName.Error;
            }

            template.Rename(name);
        }

        if (request.Description is not null)
        {
            template.Describe(request.Description);
        }

        if (request.BodyMarkdown is not null)
        {
            template.ChangeBody(request.BodyMarkdown);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (request.IsDefault is { } isDefault)
        {
            if (isDefault)
            {
                await TemplateSupport.ClearOtherDefaultsAsync(db, template.CollectionId, template.Id, cancellationToken).ConfigureAwait(false);
            }

            template.MarkDefault(isDefault);
        }

        if (await db.SaveChangesOrConflictAsync(cancellationToken, CollectionConflicts.TemplateName, TemplateSupport.DefaultConflict).ConfigureAwait(false) is { } conflict)
        {
            return conflict;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return TemplateDto.From(template);
    }
}
