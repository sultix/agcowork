using Agcowork.Application.Activity;
using Agcowork.Application.Realtime;
using Agcowork.Application.Tenancy;
using Agcowork.Domain.Activity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agcowork.Infrastructure.Realtime;

/// <summary>Settings of the realtime dispatcher (<c>Agcowork:Realtime</c>).</summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Agcowork:Realtime";

    /// <summary>How often the activity log is polled when nothing is pending. Default 250 ms.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Events read per poll; a full batch is followed by the next read without waiting. Default 500.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// How long one message to one connection may take. A connection whose client does not read (the write blocks on
    /// back-pressure) is closed after this time; other connections and workspaces never wait for it. Default 5 s.
    /// </summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Further browser origins (<c>scheme://host[:port]</c>) allowed to open the hub with a session cookie, besides the API's
    /// own host. Needed when the UI is served from another host than the one the API sees (e.g. a TLS proxy that rewrites Host).
    /// </summary>
    public IReadOnlyList<string> AllowedOrigins { get; set; } = [];
}

/// <summary>
/// Delivers the activity log to live realtime connections (architecture §5, "Outbox"): the table <c>activity_events</c> is the
/// outbox, written in the same transaction as every change. The dispatcher polls it for stable events after its cursor, in the
/// commit-safe order of <see cref="ActivityLog"/> (instance-wide, <see cref="InstanceActivityReader"/>), and hands each
/// workspace's events to <see cref="PublishActivity"/> in a scope whose tenant is that workspace.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The cursor lives in memory and starts at the newest stable event when the host starts: no replay of old events, and
/// events that become stable while no one is connected are skipped. Clients load state over REST and then follow the hub.</item>
/// <item>Polling, no LISTEN/NOTIFY: an idle poll is one index probe on <c>(xact_id, seq)</c>, and a notification would not make
/// events stable any sooner. The latency is the poll interval plus the time the oldest running transaction takes.</item>
/// <item>At most once: the cursor moves on before publishing, so a failing delivery is logged and not retried; clients
/// reload after reconnecting.</item>
/// <item>Single instance: connections of other instances are not reached. Several API instances need a SignalR backplane
/// (e.g. Redis) and one dispatcher per instance or a shared one; not built (architecture §5).</item>
/// </list>
/// </remarks>
internal sealed partial class ActivityDispatcher(
    IServiceScopeFactory scopeFactory,
    IRealtimeConnections connections,
    IOptions<RealtimeOptions> options,
    TimeProvider timeProvider,
    ILogger<ActivityDispatcher> logger) : BackgroundService
{
    private readonly RealtimeOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ActivityPosition? cursor = null;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var more = false;
            var wait = _options.PollInterval;
            try
            {
                cursor ??= await ReadHeadAsync(stoppingToken).ConfigureAwait(false);
                (cursor, more) = await DispatchOnceAsync(cursor.Value, stoppingToken).ConfigureAwait(false);
                if (failures > 0)
                {
                    LogPollRecovered(logger, failures);
                    failures = 0;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Database unavailable or similar: keep the cursor and back off (doubling up to MaxBackoff), so a lasting
                // outage does not log a warning every poll interval.
                failures++;
                wait = Backoff(_options.PollInterval, failures);
                LogPollFailed(logger, exception, failures, wait);
            }

            if (!more)
            {
                try
                {
                    await Task.Delay(wait, timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>One poll: reads a batch after <paramref name="cursor"/> and publishes it per workspace.</summary>
    /// <returns>The new cursor and whether the batch was full (read again without waiting).</returns>
    internal async Task<(ActivityPosition Cursor, bool More)> DispatchOnceAsync(ActivityPosition cursor, CancellationToken cancellationToken)
    {
        IReadOnlyList<ActivityEvent> batch;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            batch = await scope.ServiceProvider.GetRequiredService<InstanceActivityReader>()
                .ReadAsync(cursor, _options.BatchSize, cancellationToken)
                .ConfigureAwait(false);
        }

        if (batch.Count == 0)
        {
            return (cursor, false);
        }

        foreach (var workspace in batch.GroupBy(e => e.WorkspaceId))
        {
            if (connections.InWorkspace(workspace.Key).Count == 0)
            {
                continue;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(new RealtimeTenant(workspace.Key));
                var dropped = await scope.ServiceProvider.GetRequiredService<PublishActivity>()
                    .PublishAsync([.. workspace], cancellationToken)
                    .ConfigureAwait(false);
                if (dropped > 0)
                {
                    LogDropped(logger, dropped, workspace.Key);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPublishFailed(logger, exception, workspace.Key);
            }
        }

        return (ActivityLog.PositionOf(batch[^1]), batch.Count == _options.BatchSize);
    }

    /// <summary>Upper bound of the wait after failed polls.</summary>
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Wait after the <paramref name="failures"/>-th failure in a row: the interval doubled per failure, at most <see cref="MaxBackoff"/>.</summary>
    internal static TimeSpan Backoff(TimeSpan interval, int failures)
    {
        var factor = Math.Pow(2, Math.Min(failures, 16));
        var wait = TimeSpan.FromTicks((long)Math.Min(interval.Ticks * factor, MaxBackoff.Ticks));
        return wait > interval ? wait : interval;
    }

    private async Task<ActivityPosition> ReadHeadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<InstanceActivityReader>().HeadAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime dispatcher could not read the activity log ({Failures} failure(s) in a row); retrying in {Wait}.")]
    private static partial void LogPollFailed(ILogger logger, Exception exception, int failures, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Information, Message = "Realtime dispatcher reads the activity log again after {Failures} failure(s).")]
    private static partial void LogPollRecovered(ILogger logger, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime delivery to workspace {WorkspaceId} failed; these events are not sent again.")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, Guid workspaceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed {Count} realtime connection(s) of workspace {WorkspaceId} that lost access.")]
    private static partial void LogDropped(ILogger logger, int count, Guid workspaceId);
}

/// <summary>Registration of the dispatcher.</summary>
public static class RealtimeRegistration
{
    /// <summary>
    /// Registers the realtime dispatcher (hosted service) and its options. The host must also register
    /// <see cref="IRealtimeConnections"/> (the transport).
    /// </summary>
    public static IServiceCollection AddActivityDispatcher(this IServiceCollection services)
    {
        services.AddRealtimeOptions();
        services.AddHostedService<ActivityDispatcher>();
        return services;
    }

    /// <summary>Binds and validates <see cref="RealtimeOptions"/> (<c>Agcowork:Realtime</c>); also used by the transport.</summary>
    public static IServiceCollection AddRealtimeOptions(this IServiceCollection services)
    {
        services.AddOptions<RealtimeOptions>()
            .BindConfiguration(RealtimeOptions.SectionName)
            .Validate(o => o.PollInterval > TimeSpan.Zero && o.PollInterval <= TimeSpan.FromSeconds(5), $"{RealtimeOptions.SectionName}:PollInterval must be between 0 and 5 seconds.")
            .Validate(o => o.BatchSize is > 0 and <= 5000, $"{RealtimeOptions.SectionName}:BatchSize must be between 1 and 5000.")
            .Validate(o => o.SendTimeout > TimeSpan.Zero && o.SendTimeout <= TimeSpan.FromMinutes(1), $"{RealtimeOptions.SectionName}:SendTimeout must be between 0 and 60 seconds.")
            .Validate(o => o.AllowedOrigins.All(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/"), $"{RealtimeOptions.SectionName}:AllowedOrigins must be origins like https://host[:port].")
            .ValidateOnStart();
        return services;
    }
}
