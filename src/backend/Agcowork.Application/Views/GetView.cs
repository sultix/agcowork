using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Views;

public sealed record GetViewRequest(Guid ViewId);

/// <summary>One view, also of an archived collection. Requires <c>read</c>.</summary>
public sealed class GetView(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<GetViewRequest, ViewDto>
{
    public async Task<Result<ViewDto>> HandleAsync(GetViewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        var view = await db.Views.AsNoTracking().SingleOrDefaultAsync(v => v.Id == request.ViewId, cancellationToken).ConfigureAwait(false);
        return view is null ? new NotFoundError("view") : ViewDto.From(view);
    }
}
