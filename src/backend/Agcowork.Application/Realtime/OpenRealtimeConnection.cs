using Agcowork.Application.Common;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Realtime;

/// <param name="PasswordStamp">Session connections: credential stamp of the session cookie, as read by the authentication adapter.</param>
/// <param name="SessionIssuedAt">Session connections: issue time of the cookie.</param>
public sealed record OpenRealtimeConnectionRequest(DateTimeOffset? PasswordStamp = null, DateTimeOffset? SessionIssuedAt = null);

/// <summary>
/// Authorizes a realtime connection to the current workspace (<c>/w/{slug}/hub</c>). The tenant has been resolved like for
/// REST (unknown workspace, no membership, key for another workspace: 404 before this runs); this adds the <c>read</c> scope and
/// describes the subscriber, whose access the dispatcher checks again on every delivery (<see cref="RealtimeAccess"/>).
/// </summary>
public sealed class OpenRealtimeConnection(ITenantContext tenant, ICurrentActor currentActor) : IUseCase<OpenRealtimeConnectionRequest, RealtimeSubscriber>
{
    public Task<Result<RealtimeSubscriber>> HandleAsync(OpenRealtimeConnectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentActor.RequireAuthenticated() is { } unauthenticated)
        {
            return Task.FromResult<Result<RealtimeSubscriber>>(unauthenticated);
        }

        if (tenant.RequireScope(WorkspaceScope.Read) is { } denied)
        {
            return Task.FromResult<Result<RealtimeSubscriber>>(denied);
        }

        var isKey = currentActor.ApiKeyId is not null;
        var subscriber = new RealtimeSubscriber(
            tenant.WorkspaceId,
            currentActor.Id,
            currentActor.ApiKeyId,
            isKey ? null : request.PasswordStamp,
            isKey ? null : request.SessionIssuedAt);
        return Task.FromResult<Result<RealtimeSubscriber>>(subscriber);
    }
}
