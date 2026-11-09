using System.Net;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Identity;
using Agcowork.Application.Identity.Throttling;
using Agcowork.Infrastructure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agcowork.Application.Tests.Identity;

/// <summary>A fixed client address (null: unknown, like an in-process call).</summary>
internal sealed class FixedAddress(IPAddress? address) : IClientAddress
{
    public IPAddress? Address { get; } = address;
}

internal static class NoAddress
{
    public static IClientAddress Instance { get; } = new FixedAddress(null);
}

/// <summary>Counts verifications and hashes of an inner hasher.</summary>
internal sealed class CountingHasher(IPasswordHasher inner) : IPasswordHasher
{
    private int _verifications;
    private int _hashes;

    public int Verifications => Volatile.Read(ref _verifications);

    public int Hashes => Volatile.Read(ref _hashes);

    public string Hash(string password)
    {
        Interlocked.Increment(ref _hashes);
        return inner.Hash(password);
    }

    public PasswordVerification Verify(string? passwordHash, string password)
    {
        Interlocked.Increment(ref _verifications);
        return inner.Verify(passwordHash, password);
    }
}

/// <summary>Collects formatted log messages.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

internal static class Limiters
{
    /// <summary>The production limiter with the default limits (5/15 min account, 20/15 min address), optionally adjusted.</summary>
    public static InMemoryCredentialAttemptLimiter Create(
        TimeProvider clock,
        Action<CredentialThrottlingOptions>? configure = null,
        ListLogger<InMemoryCredentialAttemptLimiter>? logger = null)
    {
        var options = new CredentialThrottlingOptions();
        configure?.Invoke(options);
        return new InMemoryCredentialAttemptLimiter(Options.Create(options), clock, logger ?? new ListLogger<InMemoryCredentialAttemptLimiter>());
    }
}
