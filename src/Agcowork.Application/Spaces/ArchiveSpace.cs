using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Spaces;

public sealed record ArchiveSpaceRequest(Guid SpaceId);

/// <summary>
/// Archives a space: it stays readable, but cannot be changed and gets no new collections. Its collections
/// are left as they are. Requires <c>admin</c>. There is no un-archive yet.
/// </summary>
public sealed class ArchiveSpace(IAgcoworkDbContext db, ITenantContext tenant, TimeProvider timeProvider) : IUseCase<ArchiveSpaceRequest, SpaceDto>
{
    public async Task<Result<SpaceDto>> HandleAsync(ArchiveSpaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireWritable() is { } archived)
        {
            return archived;
        }

        if (tenant.RequireScope(WorkspaceScope.Admin) is { } denied)
        {
            return denied;
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

        space.Archive(timeProvider);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SpaceDto.From(space);
    }
}
