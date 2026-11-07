using Agcowork.Application.Common;
using Agcowork.Application.Common.Persistence;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Workspaces;

public sealed record ListMembersRequest;

/// <summary>Members of the current workspace, owners first, then by name. Requires <c>read</c>.</summary>
public sealed class ListMembers(IAgcoworkDbContext db, ITenantContext tenant) : IUseCase<ListMembersRequest, IReadOnlyList<MemberDto>>
{
    public async Task<Result<IReadOnlyList<MemberDto>>> HandleAsync(ListMembersRequest request, CancellationToken cancellationToken)
    {
        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return denied;
        }

        // Memberships are filtered to the tenant; actors are global and joined by id.
        var rows = await (
                from membership in db.Memberships.AsNoTracking()
                join actor in db.Actors.AsNoTracking() on membership.ActorId equals actor.Id
                select new { Membership = membership, Actor = actor })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Sorted in memory: roles are stored as strings, whose SQL order is not the role hierarchy.
        return Result.Success<IReadOnlyList<MemberDto>>([.. rows
            .OrderByDescending(row => row.Membership.Role)
            .ThenBy(row => row.Actor.Name, StringComparer.Ordinal)
            .ThenBy(row => row.Membership.CreatedAt)
            .Select(row => MemberDto.From(row.Membership, row.Actor))]);
    }
}
