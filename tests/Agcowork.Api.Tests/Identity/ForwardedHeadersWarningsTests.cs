using System.Net;
using Agcowork.Api.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Api.Tests.Identity;

/// <summary>Hints for unused X-Forwarded-For (AP-043): which case, which fix, at most every 10 minutes per case.</summary>
public sealed class ForwardedHeadersWarningsTests
{
    private static readonly ForwardedHeadersSettings NotConfigured = new();
    private static readonly ForwardedHeadersSettings Configured = new() { KnownProxies = ["10.99.0.2"] };

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly WarningLogger _logger = new();

    [Fact]
    public void Inspect_NoForwardedFor_NoWarning()
    {
        Sut().Inspect(Request(null), NotConfigured);
        Sut().Inspect(Request(null), Configured);

        Assert.Empty(_logger.Messages);
    }

    [Fact]
    public void Inspect_ForwardedForWithoutConfiguration_WarnsToSetKnownProxies()
    {
        Sut().Inspect(Request("203.0.113.5"), NotConfigured);

        var message = Assert.Single(_logger.Messages);
        Assert.Contains("no proxy is trusted", message, StringComparison.Ordinal);
        Assert.Contains("Agcowork:ForwardedHeaders:KnownProxies", message, StringComparison.Ordinal);
        Assert.Contains("172.30.43.99", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_ForwardedForFromUntrustedPeer_WarnsToAddPeer()
    {
        // The middleware left the header untouched and set no X-Original-For: the peer was not trusted.
        Sut().Inspect(Request("203.0.113.5"), Configured);

        var message = Assert.Single(_logger.Messages);
        Assert.Contains("not a trusted proxy", message, StringComparison.Ordinal);
        Assert.Contains("KnownProxies", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_EntriesLeftAfterTrustedProxy_WarnsToRaiseForwardLimit()
    {
        var context = Request("198.51.100.1", originalFor: "10.99.0.2");

        Sut().Inspect(context, Configured);

        var message = Assert.Single(_logger.Messages);
        Assert.Contains("ForwardLimit (1)", message, StringComparison.Ordinal);
        Assert.Contains("raise ForwardLimit", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_AllEntriesUsed_NoWarning()
    {
        // The middleware removes X-Forwarded-For when it used every entry.
        Sut().Inspect(Request(null, originalFor: "10.99.0.2"), Configured);

        Assert.Empty(_logger.Messages);
    }

    [Fact]
    public void Inspect_Repeated_AtMostOncePerTenMinutesPerCase()
    {
        var sut = Sut();

        for (var i = 0; i < 5; i++)
        {
            sut.Inspect(Request("203.0.113.5"), Configured);
            sut.Inspect(Request("198.51.100.1", originalFor: "10.99.0.2"), Configured);
        }

        Assert.Equal(2, _logger.Messages.Count);
        _clock.Advance(ForwardedHeadersWarnings.Interval - TimeSpan.FromSeconds(1));
        sut.Inspect(Request("203.0.113.5"), Configured);
        Assert.Equal(2, _logger.Messages.Count);

        _clock.Advance(TimeSpan.FromSeconds(1));
        sut.Inspect(Request("203.0.113.5"), Configured);
        Assert.Equal(3, _logger.Messages.Count);
    }

    private ForwardedHeadersWarnings Sut() => new(_clock, _logger);

    private static DefaultHttpContext Request(string? forwardedFor, string? originalFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("172.30.43.99");
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        if (originalFor is not null)
        {
            context.Request.Headers["X-Original-For"] = originalFor;
        }

        return context;
    }

    private sealed class WarningLogger : ILogger<ForwardedHeadersWarnings>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(LogLevel.Warning, logLevel);
            Messages.Add(formatter(state, exception));
        }
    }
}
