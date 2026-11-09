using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Agcowork.Api.Auth;

/// <summary>
/// Notices <c>X-Forwarded-For</c> that the client-address setup (<see cref="ClientAddressSetup"/>) did not use, and logs a
/// warning with the fix: behind an unconfigured proxy every user shares the proxy's address and its login limit, which
/// otherwise only shows as users locked out together. Runs after the forwarded-headers middleware; each kind of warning
/// at most once per <see cref="Interval"/>, so clients that send the header themselves cannot flood the log.
/// </summary>
internal sealed partial class ForwardedHeadersWarnings(TimeProvider timeProvider, ILogger<ForwardedHeadersWarnings> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private long _notConfigured = long.MinValue;
    private long _untrustedPeer = long.MinValue;
    private long _longerChain = long.MinValue;

    public void Inspect(HttpContext context, ForwardedHeadersSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        // After the middleware the header holds only the entries it did not use (it removes it when all were used).
        var headers = context.Request.Headers;
        if (!headers.TryGetValue(ForwardedHeadersDefaults.XForwardedForHeaderName, out var remaining) || string.IsNullOrWhiteSpace(remaining.ToString()))
        {
            return;
        }

        var address = context.Connection.RemoteIpAddress;
        if (!settings.IsConfigured)
        {
            if (Due(ref _notConfigured))
            {
                LogNotConfigured(logger, address);
            }
        }
        // The middleware sets X-Original-For (the connection address) when it applied an entry, i.e. the peer was trusted.
        else if (!headers.ContainsKey(ForwardedHeadersDefaults.XOriginalForHeaderName))
        {
            if (Due(ref _untrustedPeer))
            {
                LogUntrustedPeer(logger, address);
            }
        }
        else if (Due(ref _longerChain))
        {
            LogLongerChain(logger, settings.ForwardLimit, address);
        }
    }

    private bool Due(ref long lastTicks)
    {
        var now = timeProvider.GetUtcNow().UtcTicks;
        var previous = Volatile.Read(ref lastTicks);
        if (previous != long.MinValue && now - previous < Interval.Ticks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref lastTicks, now, previous) == previous;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A request carries X-Forwarded-For, but no proxy is trusted: login throttling counts the connection address {Address}. Behind a reverse proxy all users share that address and its limit (by default 20 failed logins in 15 minutes); set Agcowork:ForwardedHeaders:KnownProxies (or KnownNetworks) to the proxy's address. Logged at most every 10 minutes.")]
    private static partial void LogNotConfigured(ILogger logger, IPAddress? address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "X-Forwarded-For from {Address} was ignored because that peer is not a trusted proxy: login throttling counts {Address}. If it is your reverse proxy, add it to Agcowork:ForwardedHeaders:KnownProxies (or KnownNetworks); otherwise a client sent the header itself. Logged at most every 10 minutes.")]
    private static partial void LogUntrustedPeer(ILogger logger, IPAddress? address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "X-Forwarded-For has more entries than Agcowork:ForwardedHeaders:ForwardLimit ({ForwardLimit}); login throttling counts {Address}. If a further proxy forwards to the trusted one (e.g. TLS in front of nginx), add its address to KnownProxies and raise ForwardLimit by one; otherwise a client sent the header itself. Logged at most every 10 minutes.")]
    private static partial void LogLongerChain(ILogger logger, int forwardLimit, IPAddress? address);
}
