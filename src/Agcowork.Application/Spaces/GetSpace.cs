using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Spaces;

public sealed record GetSpaceRequest(Guid SpaceId);

/// <summary>One space of the current workspace, archived ones included. Requires <c>read</c>.</summary>
public sealed class GetSpace(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<GetSpaceRequest, SpaceDto>
{
    public async Task<Result<SpaceDto>> HandleAsync(GetSpaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var space = await db.Spaces.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.SpaceId, cancellationToken).ConfigureAwait(false);
        return space is null ? new NotFoundError("space") : SpaceDto.From(space);
    }
}
