namespace Agcowork.Api.Auth;

/// <summary>Authentication scheme names.</summary>
public static class AuthSchemes
{
    /// <summary>Default policy scheme: forwards to <see cref="ApiKey"/> for <c>Authorization: Bearer …</c>, else to <see cref="Cookie"/>.</summary>
    public const string Default = "Agcowork";

    /// <summary>Agents: <c>Authorization: Bearer agck_…</c>.</summary>
    public const string ApiKey = "ApiKey";

    /// <summary>Humans: session cookie set by <c>POST /api/v1/auth/login</c>.</summary>
    public const string Cookie = "Cookie";
}

/// <summary>Authorization policy names, shared by REST and (later) MCP and SignalR.</summary>
public static class AuthPolicies
{
    public const string Authenticated = "Authenticated";
    public const string InstanceAdmin = "InstanceAdmin";
    public const string WorkspacesCreate = "WorkspacesCreate";
}
