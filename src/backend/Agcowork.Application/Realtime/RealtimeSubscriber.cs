namespace Agcowork.Application.Realtime;

/// <summary>
/// Who receives realtime events over a connection: the authenticated actor and the credential it connected with, so every
/// delivery can check again that the credential is still valid (key not revoked or expired, session not signed out by a
/// password change, actor active, membership with <c>read</c>).
/// </summary>
/// <param name="WorkspaceId">Workspace of the connection (from the tenant of the connect request).</param>
/// <param name="ActorId">The authenticated actor.</param>
/// <param name="ApiKeyId">Key connections: the key used. Null for session connections.</param>
/// <param name="PasswordStamp">Session connections: the credential stamp of the session cookie (AP-022).</param>
/// <param name="SessionIssuedAt">Session connections: when the cookie was issued (fallback for cookies without stamp).</param>
public sealed record RealtimeSubscriber(
    Guid WorkspaceId,
    Guid ActorId,
    Guid? ApiKeyId,
    DateTimeOffset? PasswordStamp,
    DateTimeOffset? SessionIssuedAt);

/// <summary>A live connection and its subscriber.</summary>
/// <param name="ConnectionId">Transport connection id.</param>
/// <param name="Subscriber">Who is connected.</param>
public sealed record RealtimeConnection(string ConnectionId, RealtimeSubscriber Subscriber);

/// <summary>
/// The live realtime connections of this instance and how to reach them. Implemented by the transport adapter (SignalR hub
/// in the Api); the dispatcher only calls it. Single instance: the connections of other instances are not visible here, more
/// than one instance needs a backplane (architecture §5).
/// </summary>
public interface IRealtimeConnections
{
    /// <summary>The connections of a workspace (snapshot).</summary>
    IReadOnlyList<RealtimeConnection> InWorkspace(Guid workspaceId);

    /// <summary>Sends one <c>activity</c> event to every connection of the workspace except <paramref name="excludedConnectionIds"/>.</summary>
    Task SendActivityAsync(Guid workspaceId, Activity.ActivityEventDto activity, IReadOnlyCollection<string> excludedConnectionIds, CancellationToken cancellationToken);

    /// <summary>Removes connections from the workspace and closes them (access revoked).</summary>
    Task DisconnectAsync(Guid workspaceId, IReadOnlyCollection<string> connectionIds, CancellationToken cancellationToken);
}
