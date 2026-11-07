using Agcowork.Api.Auth;
using Agcowork.Api.Http;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Realtime;
using Agcowork.Infrastructure.Realtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agcowork.Api.Realtime;

/// <summary>Marks the hub endpoints (negotiate and transport), which need <see cref="RealtimeConnectMiddleware"/>.</summary>
internal sealed class RealtimeHubMetadata
{
    public static RealtimeHubMetadata Instance { get; } = new();
}

/// <summary>
/// Authorizes every HTTP request to the hub after the tenancy middleware and hands the subscriber to the hub through
/// <see cref="HttpContext.Items"/>:
/// <list type="number">
/// <item>Session cookies only: the browser origin must be the API's own host or one of
/// <see cref="RealtimeOptions.AllowedOrigins"/> (<see cref="RealtimeOriginPolicy"/>), else 403. Protects against cross-site
/// WebSocket hijacking: a WebSocket handshake is not subject to CORS, so SameSite is the only other guard. Bearer keys are
/// not checked: a browser page cannot attach them to another site's WebSocket.</item>
/// <item><see cref="OpenRealtimeConnection"/>: <c>read</c> required, else 403.</item>
/// </list>
/// A middleware because the hub runs outside the request scope (SignalR creates a scope per hub call), so it cannot see the
/// tenant; <c>Items</c> travel with every transport (WebSockets and Server-Sent Events keep the request, long polling copies
/// its items to the connection). Long polling passes here on every poll, so a lost membership ends such a connection at its
/// next poll.
/// </summary>
internal sealed class RealtimeConnectMiddleware(RequestDelegate next, IOptions<RealtimeOptions> options)
{
    public static readonly object SubscriberItemKey = new();

    public async Task InvokeAsync(HttpContext context, OpenRealtimeConnection openConnection, IProblemDetailsService problemDetails)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RealtimeHubMetadata>() is null)
        {
            await next(context);
            return;
        }

        var isSession = context.User.Identity?.AuthenticationType != AuthSchemes.ApiKey;
        if (isSession && !RealtimeOriginPolicy.IsAllowed(
                context.Request.Headers.Origin.ToString(),
                context.Request.Method,
                context.WebSockets.IsWebSocketRequest,
                context.Request.Host,
                options.Value.AllowedOrigins))
        {
            await WriteProblemAsync(context, problemDetails, new ForbiddenError("origin not allowed for session connections"));
            return;
        }

        // Session connections remember the credential stamp, so a password change also ends live connections (AP-022).
        var properties = context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties;
        var request = new OpenRealtimeConnectionRequest(
            properties is null ? null : SessionCookie.PasswordStamp(properties),
            properties?.IssuedUtc);
        var result = await openConnection.HandleAsync(request, context.RequestAborted);
        if (result.IsFailure)
        {
            await WriteProblemAsync(context, problemDetails, result.Error);
            return;
        }

        context.Items[SubscriberItemKey] = result.Value;
        await next(context);
    }

    private static async Task WriteProblemAsync(HttpContext context, IProblemDetailsService problemDetails, Error error)
    {
        var problem = ProblemDetailsMapping.ToProblemDetails(error);
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await problemDetails.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }
}

/// <summary>Which browser origins may open the hub with a session cookie.</summary>
internal static class RealtimeOriginPolicy
{
    /// <summary>
    /// <list type="bullet">
    /// <item>Origin present: allowed when its host and port equal the request's <c>Host</c> (the scheme is not compared, so a TLS
    /// proxy in front of plain HTTP still matches), or when it equals one of <paramref name="allowedOrigins"/> exactly
    /// (scheme, host, port). <c>null</c> and malformed origins are refused.</item>
    /// <item>Origin missing: refused for WebSocket handshakes and every method but GET (browsers always send Origin there).
    /// Allowed for plain GET, which browsers send without Origin from the same origin (long-polling polls, Server-Sent Events);
    /// a cross-site page cannot make such a request carry the SameSite=Lax session cookie.</item>
    /// </list>
    /// </summary>
    public static bool IsAllowed(string? origin, string method, bool isWebSocket, HostString host, IReadOnlyList<string> allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);
        if (string.IsNullOrEmpty(origin))
        {
            return !isWebSocket && HttpMethods.IsGet(method);
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (host.HasValue && string.Equals(uri.Authority, host.Value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return allowedOrigins.Any(allowed =>
            Uri.TryCreate(allowed, UriKind.Absolute, out var candidate)
            && string.Equals(candidate.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Authority, uri.Authority, StringComparison.OrdinalIgnoreCase));
    }
}
