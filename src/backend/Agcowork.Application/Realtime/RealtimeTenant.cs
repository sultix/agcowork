using Agcowork.Application.Tenancy;
using Agcowork.Domain.Workspaces;

namespace Agcowork.Application.Realtime;

/// <summary>
/// Tenant of a dispatcher scope: the workspace whose events are being delivered, so title lookups and membership checks run
/// through the tenant filter like in any request. There is no caller, hence no membership and no scopes (use cases would
/// answer 403, and 409 for writes because it reports itself as archived). Never use it for use cases.
/// </summary>
/// <param name="workspaceId">Workspace of the events.</param>
public sealed class RealtimeTenant(Guid workspaceId) : ITenantContext
{
    private static readonly IReadOnlySet<string> NoScopes = new HashSet<string>(StringComparer.Ordinal);

    public Guid WorkspaceId { get; } = workspaceId;

    public string Slug => throw new InvalidOperationException("The realtime dispatcher's tenant has no slug.");

    public bool IsArchived => true;

    public Membership Membership => throw new InvalidOperationException("The realtime dispatcher's tenant has no caller and no membership.");

    /// <summary>None: every use case's scope check fails. The dispatcher only queries, it never runs use cases.</summary>
    public IReadOnlySet<string> EffectiveScopes => NoScopes;
}
