using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agcowork.Application.Identity;
using Agcowork.Application.Identity.Throttling;
using Agcowork.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Api.Tests.Identity;

/// <summary>
/// Login and password-change throttling end to end (AP-043). Every test runs its own host on the shared database
/// (<see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>) with throttling on, a fake clock for the limiter,
/// a hash counter and a test header that sets the connection's address.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class CredentialThrottlingTests(AgcoworkApiFactory factory)
{
    private const string Password = "initial-password-123";
    private const string NewPassword = "brand-new-password-456";

    /// <summary>The connection's address for one request (set by <see cref="ConnectionAddressFilter"/>).</summary>
    private const string ConnectionAddressHeader = "X-Test-Connection-Address";

    /// <summary>Proxy whose X-Forwarded-For the throttled host trusts (like nginx in docker compose).</summary>
    private const string TrustedProxy = "10.99.0.2";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly CountingHasher _hasher = new();

    [Fact]
    public async Task Login_SixthFailureForAccount_429WithRetryAfter_AllowedAgainAfterLockout()
    {
        var email = await CreateHumanAsync();
        await using var host = Throttled();
        using var client = Client(host, "203.0.113.10");

        for (var i = 0; i < 5; i++)
        {
            using var wrong = await LoginAsync(client, email, "wrong-password");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using (var locked = await LoginAsync(client, email, Password))
        {
            var problem = await ProblemAssert.IsProblemAsync(locked, HttpStatusCode.TooManyRequests, "rate_limited");
            Assert.Equal("60", Assert.Single(locked.Headers.GetValues("Retry-After")));
            Assert.Equal(60, problem.GetProperty("retryAfterSeconds").GetInt32());
            Assert.False(string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
            Assert.False(locked.Headers.Contains("Set-Cookie"));
        }

        _clock.Advance(TimeSpan.FromSeconds(59));
        using (var stillLocked = await LoginAsync(client, email, Password))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, stillLocked.StatusCode);
            Assert.Equal("1", Assert.Single(stillLocked.Headers.GetValues("Retry-After")));
        }

        _clock.Advance(TimeSpan.FromSeconds(1));
        using var allowed = await LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownAndKnownEmail_SameStatusBodyRetryAfterAndHashCost()
    {
        var known = await CreateHumanAsync();
        var unknown = $"nobody-{Guid.NewGuid():N}@agcowork.local";
        await using var host = Throttled();
        using var client = Client(host, "203.0.113.11");

        for (var i = 0; i < 7; i++)
        {
            var before = _hasher.Verifications;
            var (knownStatus, knownRetry, knownBody) = await AttemptAsync(client, known);
            var knownHashes = _hasher.Verifications - before;

            before = _hasher.Verifications;
            var (unknownStatus, unknownRetry, unknownBody) = await AttemptAsync(client, unknown);
            var unknownHashes = _hasher.Verifications - before;

            Assert.Equal(i < 5 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, knownStatus);
            Assert.Equal(knownStatus, unknownStatus);
            Assert.Equal(knownRetry, unknownRetry);
            Assert.Equal(knownBody, unknownBody);
            // Rough runtime: the same number of hash verifications (the expensive part), and none once locked.
            Assert.Equal(i < 5 ? 1 : 0, knownHashes);
            Assert.Equal(knownHashes, unknownHashes);
        }
    }

    [Fact]
    public async Task Login_ManyEmailsFromOneAddress_AddressLimitApplies_OtherAddressUnaffected()
    {
        await using var host = Throttled();
        using var attacker = Client(host, "198.51.100.20");
        using var other = Client(host, "198.51.100.21");

        for (var i = 0; i < 20; i++)
        {
            using var wrong = await LoginAsync(attacker, $"guess-{Guid.NewGuid():N}@agcowork.local", "x");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var fresh = $"fresh-{Guid.NewGuid():N}@agcowork.local";
        using var blocked = await LoginAsync(attacker, fresh, "x");
        using var unaffected = await LoginAsync(other, fresh, "x");

        await ProblemAssert.IsProblemAsync(blocked, HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.Equal("900", Assert.Single(blocked.Headers.GetValues("Retry-After")));
        Assert.Equal(HttpStatusCode.Unauthorized, unaffected.StatusCode);
    }

    [Fact]
    public async Task Login_SpoofedForwardedForFromUntrustedPeer_CountsPeerAddress()
    {
        await using var host = Throttled();
        using var untrusted = Client(host, "198.51.100.30");

        for (var i = 0; i < 20; i++)
        {
            using var wrong = await LoginAsync(untrusted, $"guess-{Guid.NewGuid():N}@agcowork.local", "x", forwardedFor: $"192.0.2.{i + 1}");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var blocked = await LoginAsync(untrusted, $"g-{Guid.NewGuid():N}@agcowork.local", "x", forwardedFor: "192.0.2.200");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // The spoofed addresses were never counted.
        using var spoofedAddress = Client(host, "192.0.2.1");
        using var notBlocked = await LoginAsync(spoofedAddress, $"g-{Guid.NewGuid():N}@agcowork.local", "x");
        Assert.Equal(HttpStatusCode.Unauthorized, notBlocked.StatusCode);
    }

    [Theory]
    [InlineData(TrustedProxy)]
    [InlineData("::ffff:" + TrustedProxy)]
    public async Task Login_ForwardedForFromTrustedProxy_CountsForwardedAddress(string proxyAddress)
    {
        await using var host = Throttled();
        using var viaProxy = Client(host, proxyAddress);

        // The client may send its own X-Forwarded-For; the proxy appends the real address, and only that last entry counts.
        for (var i = 0; i < 20; i++)
        {
            using var wrong = await LoginAsync(viaProxy, $"guess-{Guid.NewGuid():N}@agcowork.local", "x", forwardedFor: $"192.0.2.{i + 1}, 203.0.113.40");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var blocked = await LoginAsync(viaProxy, $"g-{Guid.NewGuid():N}@agcowork.local", "x", forwardedFor: "203.0.113.40");
        using var otherClient = await LoginAsync(viaProxy, $"g-{Guid.NewGuid():N}@agcowork.local", "x", forwardedFor: "203.0.113.41");
        using var proxyItself = await LoginAsync(viaProxy, $"g-{Guid.NewGuid():N}@agcowork.local", "x");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, proxyItself.StatusCode);
    }

    [Fact]
    public async Task Login_NoProxyConfigured_ForwardedForIgnored()
    {
        await using var host = Throttled(trustProxy: false);
        using var peer = Client(host, TrustedProxy);

        for (var i = 0; i < 20; i++)
        {
            using var wrong = await LoginAsync(peer, $"guess-{Guid.NewGuid():N}@agcowork.local", "x", forwardedFor: $"192.0.2.{i + 1}");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var blocked = await LoginAsync(peer, $"g-{Guid.NewGuid():N}@agcowork.local", "x", forwardedFor: "192.0.2.100");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [Theory]
    [InlineData(true, "198.51.100.50", "192.0.2.1", "not a trusted proxy")]
    [InlineData(true, TrustedProxy, "192.0.2.1, 203.0.113.60", "raise ForwardLimit")]
    [InlineData(false, TrustedProxy, "203.0.113.60", "no proxy is trusted")]
    public async Task Request_UnusedForwardedFor_LogsOneHintWithTheFix(bool trustProxy, string peer, string forwardedFor, string hint)
    {
        var warnings = new WarningCollector();
        await using var host = Throttled(trustProxy, warnings);
        using var client = Client(host, peer);

        for (var i = 0; i < 3; i++)
        {
            using var response = await LoginAsync(client, $"g-{Guid.NewGuid():N}@agcowork.local", "x", forwardedFor);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var message = Assert.Single(warnings.Messages, m => m.Contains("X-Forwarded-For", StringComparison.Ordinal));
        Assert.Contains(hint, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_ForwardedForFullyUsedByTrustedProxy_NoHint()
    {
        var warnings = new WarningCollector();
        await using var host = Throttled(warnings: warnings);
        using var client = Client(host, TrustedProxy);

        using var response = await LoginAsync(client, $"g-{Guid.NewGuid():N}@agcowork.local", "x", "203.0.113.61");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(warnings.Messages, m => m.Contains("X-Forwarded-For", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_Locked_ComputesNoPasswordHash()
    {
        var email = await CreateHumanAsync();
        await using var host = Throttled();
        using var client = Client(host, "203.0.113.12");
        for (var i = 0; i < 5; i++)
        {
            using var wrong = await LoginAsync(client, email, "wrong-password");
        }

        var before = (_hasher.Verifications, _hasher.Hashes);
        for (var i = 0; i < 3; i++)
        {
            using var locked = await LoginAsync(client, email, i == 0 ? Password : "wrong-password");
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        }

        Assert.Equal(before, (_hasher.Verifications, _hasher.Hashes));
    }

    [Fact]
    public async Task ChangeMyPassword_SixthAfterFiveWrong_429EvenWithRightPassword_NoHash()
    {
        var email = await CreateHumanAsync();
        await using var host = Throttled();
        using var session = Client(host, "203.0.113.13");
        using (var login = await LoginAsync(session, email, Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        }

        for (var i = 0; i < 5; i++)
        {
            using var wrong = await ChangeAsync(session, "not-my-password", NewPassword);
            var problem = await ProblemAssert.IsProblemAsync(wrong, HttpStatusCode.BadRequest, "validation_failed");
            Assert.Equal("invalid_credentials", ProblemAssert.Field(problem, "currentPassword").GetProperty("code").GetString());
        }

        var before = _hasher.Verifications;
        using (var locked = await ChangeAsync(session, Password, NewPassword))
        {
            var problem = await ProblemAssert.IsProblemAsync(locked, HttpStatusCode.TooManyRequests, "rate_limited");
            Assert.Equal("60", Assert.Single(locked.Headers.GetValues("Retry-After")));
            Assert.Equal(60, problem.GetProperty("retryAfterSeconds").GetInt32());
        }

        Assert.Equal(before, _hasher.Verifications);

        _clock.Advance(TimeSpan.FromMinutes(1));
        using var changed = await ChangeAsync(session, Password, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
    }

    [Fact]
    public async Task ChangeMyPassword_ApiKeyRequests_403NeverCountedOrLimited()
    {
        var agent = await factory.CreateAgentAsync(cancellationToken: Ct);
        await using var host = Throttled();
        using var keyClient = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        keyClient.DefaultRequestHeaders.Authorization = agent.Client.DefaultRequestHeaders.Authorization;
        agent.Dispose();

        for (var i = 0; i < 10; i++)
        {
            using var response = await ChangeAsync(keyClient, "not-my-password", NewPassword);
            await ProblemAssert.IsProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }

        Assert.Equal(0, ((InMemoryCredentialAttemptLimiter)host.Services.GetRequiredService<ICredentialAttemptLimiter>()).Count);
    }

    [Fact]
    public async Task Options_FromAppSettings_AreTheDocumentedLimits()
    {
        await using var host = Throttled();

        var options = host.Services.GetRequiredService<IOptions<CredentialThrottlingOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal(50_000, options.MaxEntries);
        Assert.Equal((5, TimeSpan.FromMinutes(15)), (options.Account.MaxFailures, options.Account.Window));
        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)], options.Account.EffectiveLockouts);
        Assert.Equal((20, TimeSpan.FromMinutes(15)), (options.Address.MaxFailures, options.Address.Window));
        Assert.Equal([TimeSpan.FromMinutes(15)], options.Address.EffectiveLockouts);
        Assert.Equal((5, TimeSpan.FromMinutes(15)), (options.PasswordChange.MaxFailures, options.PasswordChange.Window));
        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)], options.PasswordChange.EffectiveLockouts);
    }

    [Theory]
    [InlineData("Agcowork:ForwardedHeaders:KnownProxies:0", "nginx")]
    [InlineData("Agcowork:ForwardedHeaders:KnownNetworks:0", "172.30.43.0")]
    [InlineData("Agcowork:ForwardedHeaders:ForwardLimit", "0")]
    [InlineData("Agcowork:CredentialThrottling:Account:MaxFailures", "0")]
    [InlineData("Agcowork:CredentialThrottling:Address:Lockouts:0", "-00:01:00")]
    public void Startup_InvalidSetting_FailsToStart(string key, string value)
    {
        using var invalid = factory.WithWebHostBuilder(b => b.UseSetting(key, value));

        Assert.ThrowsAny<Exception>(() => invalid.CreateClient());
    }

    [Fact]
    public void Startup_ThrottlingDisabledOutsideDevelopment_FailsToStart()
    {
        // The shared factory disables throttling; in Production that must not start.
        using var production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production")
            .UseSetting("Agcowork:Bootstrap:AdminEmail", factory.AdminEmail)
            .UseSetting("Agcowork:Bootstrap:AdminPassword", factory.AdminPassword));

        var error = Assert.ThrowsAny<Exception>(() => production.CreateClient());
        Assert.Contains("Enabled=false", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A host with throttling on (limits from appsettings.json), the limiter on <see cref="_clock"/>, <see cref="_hasher"/>
    /// as password hasher and, unless <paramref name="trustProxy"/> is false, <see cref="TrustedProxy"/> as known proxy.
    /// </summary>
    private WebApplicationFactory<Program> Throttled(bool trustProxy = true, WarningCollector? warnings = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            if (warnings is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(warnings));
            }

            builder.UseSetting("Agcowork:CredentialThrottling:Enabled", "true");
            if (trustProxy)
            {
                builder.UseSetting("Agcowork:ForwardedHeaders:KnownProxies:0", TrustedProxy);
            }

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter, ConnectionAddressFilter>();
                services.Replace(ServiceDescriptor.Singleton<IPasswordHasher>(_hasher));
                services.Replace(ServiceDescriptor.Singleton<ICredentialAttemptLimiter>(sp => new InMemoryCredentialAttemptLimiter(
                    sp.GetRequiredService<IOptions<CredentialThrottlingOptions>>(),
                    _clock,
                    sp.GetRequiredService<ILogger<InMemoryCredentialAttemptLimiter>>())));
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> host, string connectionAddress)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(ConnectionAddressHeader, connectionAddress);
        return client;
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Status, Retry-After and the body without its traceId (which differs per request).</summary>
    private static async Task<(HttpStatusCode Status, string? RetryAfter, string Body)> AttemptAsync(HttpClient client, string email)
    {
        using var response = await LoginAsync(client, email, "wrong-password");
        var json = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await response.Content.ReadAsStringAsync(Ct))!;
        json.Remove("traceId");
        var body = string.Join(";", json.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value.GetRawText()}"));
        var retryAfter = response.Headers.TryGetValues("Retry-After", out var values) ? string.Join(",", values) : null;
        return (response.StatusCode, retryAfter, body);
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string currentPassword, string newPassword) =>
        client.PatchAsJsonAsync("/api/v1/me/password", new { currentPassword, newPassword }, Ct);

    private async Task<string> CreateHumanAsync()
    {
        using var admin = await factory.CreateAdminClientAsync(Ct);
        var email = $"human-{Guid.NewGuid():N}@agcowork.local";
        using var created = await admin.PostAsJsonAsync("/api/v1/actors", new { kind = "human", name = "Grace", email, password = Password }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return email;
    }

    /// <summary>Test stand-in for the TCP peer: sets the connection's address from a header, before any other middleware.</summary>
    private sealed class ConnectionAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(ConnectionAddressHeader, out var value))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(value.ToString());
                    context.Request.Headers.Remove(ConnectionAddressHeader);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>Collects the messages of warnings and errors a host logs.</summary>
    private sealed class WarningCollector : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    messages.Enqueue(formatter(state, exception));
                }
            }
        }
    }

    /// <summary>The real hasher (cost and all), counting calls.</summary>
    private sealed class CountingHasher : IPasswordHasher
    {
        private readonly AspNetPasswordHasher _inner = new();
        private int _verifications;
        private int _hashes;

        public int Verifications => Volatile.Read(ref _verifications);

        public int Hashes => Volatile.Read(ref _hashes);

        public string Hash(string password)
        {
            Interlocked.Increment(ref _hashes);
            return _inner.Hash(password);
        }

        public PasswordVerification Verify(string? passwordHash, string password)
        {
            Interlocked.Increment(ref _verifications);
            return _inner.Verify(passwordHash, password);
        }
    }
}
