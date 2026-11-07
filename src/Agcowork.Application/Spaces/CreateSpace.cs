using Agcowork.Application.Activity;
using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Common.Validation;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Agcowork.Domain.Spaces;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Spaces;

/// <param name="Name">Display name, max. 200 characters, e.g. <c>Produkt</c>.</param>
/// <param name="Description">What the space is for, max. 2000 characters. Agents read it to decide where things belong.</param>
/// <param name="Icon">Emoji or icon name, max. 64 characters.</param>
/// <param name="SortOrder">Position in the navigation; default: after the last space.</param>
public sealed record CreateSpaceRequest(string? Name, string? Description = null, string? Icon = null, int? SortOrder = null);

/// <summary>Creates a space in the current workspace. Requires <c>write</c>.</summary>
public sealed class CreateSpace(IAgcoworkDbContext db, ICurrentActor currentActor, ITenantContext tenant, TimeProvider timeProvider, IActivityRecorder activity)
    : IUseCase<CreateSpaceRequest, SpaceDto>
{
    public async Task<Result<SpaceDto>> HandleAsync(CreateSpaceRequest request, CancellationToken cancellationToken)
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
        SpaceInputValidation.Name(errors, request.Name);
        SpaceInputValidation.Description(errors, request.Description);
        SpaceInputValidation.Icon(errors, request.Icon);
        if (errors.Any)
        {
            return errors.ToError();
        }

        var sortOrder = request.SortOrder
            ?? (await db.Spaces.MaxAsync(s => (int?)s.SortOrder, cancellationToken).ConfigureAwait(false) ?? -1) + 1;

        var space = Space.Create(tenant.WorkspaceId, request.Name!, request.Description, request.Icon, sortOrder, currentActor.Id, timeProvider);
        db.Spaces.Add(space);
        activity.Record(ActivityTypes.SpaceCreated, ActivityTargets.Space, space.Id, space.Id, diff: new ActivityDiff().Set("name", space.Name).ToJson());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SpaceDto.From(space);
    }
}
