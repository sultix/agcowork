using Agcowork.Application.Activity;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Spaces;

/// <summary>Partial update: null leaves a field unchanged.</summary>
/// <param name="SpaceId">Space to change.</param>
/// <param name="Name">New display name.</param>
/// <param name="Description">New description; an empty string clears it.</param>
/// <param name="Icon">New icon; an empty string clears it.</param>
/// <param name="SortOrder">New position in the navigation.</param>
public sealed record UpdateSpaceRequest(Guid SpaceId, string? Name = null, string? Description = null, string? Icon = null, int? SortOrder = null);

/// <summary>Renames, re-describes or moves a space. Requires <c>write</c>; archived spaces answer 409 <c>space_archived</c>.</summary>
public sealed class UpdateSpace(IAgcoworkDbContext db, ITenantContext tenant, IActivityRecorder activity) : IUseCase<UpdateSpaceRequest, SpaceDto>
{
    public async Task<Result<SpaceDto>> HandleAsync(UpdateSpaceRequest request, CancellationToken cancellationToken)
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
        if (request.Name is not null)
        {
            SpaceInputValidation.Name(errors, request.Name);
        }

        SpaceInputValidation.Description(errors, request.Description);
        SpaceInputValidation.Icon(errors, request.Icon);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var space = await db.Spaces.SingleOrDefaultAsync(s => s.Id == request.SpaceId, cancellationToken).ConfigureAwait(false);
        if (space is null)
        {
            return new NotFoundError("space");
        }

        if (space.IsArchived)
        {
            return SpaceConflicts.Archived();
        }

        var before = (space.Name, space.Description, space.Icon, space.SortOrder);
        if (request.Name is not null)
        {
            space.Rename(request.Name);
        }

        if (request.Description is not null)
        {
            space.Describe(request.Description);
        }

        if (request.Icon is not null)
        {
            space.ChangeIcon(request.Icon);
        }

        if (request.SortOrder is { } sortOrder)
        {
            space.MoveTo(sortOrder);
        }

        var diff = new ActivityDiff()
            .Change("name", before.Name, space.Name)
            .Change("description", before.Description, space.Description)
            .Change("icon", before.Icon, space.Icon)
            .Change("sortOrder", before.SortOrder, space.SortOrder)
            .ToJson();
        if (diff.Count > 0)
        {
            activity.Record(ActivityTypes.SpaceUpdated, ActivityTargets.Space, space.Id, space.Id, diff: diff);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SpaceDto.From(space);
    }
}
