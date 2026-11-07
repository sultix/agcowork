using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Agcowork.Api.Tests.Realtime;

/// <summary>
/// SignalR client against the test server (<c>/w/{slug}/hub</c>), authenticated by bearer key or session cookie. Collects the
/// <c>activity</c> messages and notices when the server closes the connection.
/// </summary>
internal sealed class HubClient : IAsyncDisposable
{
    public static readonly TimeSpan Delivery = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
    private readonly ConcurrentQueue<JsonElement> _received = new();
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HubClient(HubConnection connection)
    {
        Connection = connection;
        connection.On<JsonElement>("activity", activity =>
        {
            var copy = activity.Clone();
            _received.Enqueue(copy);
            _events.Writer.TryWrite(copy);
        });
        connection.Closed += exception =>
        {
            _closed.TrySetResult(exception);
            return Task.CompletedTask;
        };
    }

    public HubConnection Connection { get; }

    /// <summary>Completes when the server (or the client) closed the connection.</summary>
    public Task<Exception?> Closed => _closed.Task;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string HubPath(string slug) => $"/w/{slug}/hub";

    /// <summary>Origin of a page served by the test server itself (its host is <c>localhost</c>).</summary>
    public const string SameOrigin = "http://localhost";

    /// <summary>A client with <c>Authorization: Bearer</c>, without Origin unless given (agents are no browsers).</summary>
    public static HubClient WithKey(AgcoworkApiFactory factory, string slug, string key, HttpTransportType transport = HttpTransportType.WebSockets, string? origin = null) =>
        Create(factory, slug, Headers(("Authorization", $"Bearer {key}"), ("Origin", origin)), transport);

    /// <summary>A client with a session cookie (<c>agcowork_session=…</c>), sending <paramref name="origin"/> like a browser (null: none).</summary>
    public static HubClient WithCookie(AgcoworkApiFactory factory, string slug, string cookie, HttpTransportType transport = HttpTransportType.WebSockets, string? origin = SameOrigin) =>
        Create(factory, slug, Headers(("Cookie", cookie), ("Origin", origin)), transport);

    /// <summary>Starts and connects; throws like the real client when the server refuses (e.g. 404 at negotiate).</summary>
    public async Task<HubClient> StartAsync()
    {
        await Connection.StartAsync(Ct);
        return this;
    }

    /// <summary>The next <c>activity</c> message matching <paramref name="match"/>; other messages are skipped.</summary>
    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> match, TimeSpan? timeout = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancellation.CancelAfter(timeout ?? Patience);
        try
        {
            while (true)
            {
                var activity = await _events.Reader.ReadAsync(cancellation.Token);
                if (match(activity))
                {
                    return activity;
                }
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No matching activity within {timeout ?? Patience}.");
        }
    }

    /// <summary>Every <c>activity</c> message received so far, also those <see cref="WaitForAsync"/> skipped.</summary>
    public IReadOnlyList<JsonElement> Received => [.. _received];

    /// <summary>Waits until the connection is closed by the server.</summary>
    public async Task WaitClosedAsync() => await Closed.WaitAsync(Patience, Ct);

    /// <summary>Logs in by password and returns the session cookie as a <c>Cookie</c> header value.</summary>
    public static async Task<string> SessionCookieAsync(AgcoworkApiFactory factory, string email, string password)
    {
        ArgumentNullException.ThrowIfNull(factory);
        using var client = factory.CreateClientWithoutCookies();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, Ct);
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("agcowork_session=", StringComparison.Ordinal));
        return setCookie.Split(';')[0];
    }

    public static bool Is(JsonElement activity, string type, Guid targetId) =>
        activity.GetProperty("type").GetString() == type && activity.GetProperty("targetId").GetGuid() == targetId;

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

    private static Dictionary<string, string> Headers(params (string Name, string? Value)[] headers) =>
        headers.Where(h => h.Value is not null).ToDictionary(h => h.Name, h => h.Value!, StringComparer.OrdinalIgnoreCase);

    private static HubClient Create(AgcoworkApiFactory factory, string slug, Dictionary<string, string> headers, HttpTransportType transport)
    {
        var server = factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, HubPath(slug)), transport, options =>
            {
                foreach (var (name, value) in headers)
                {
                    options.Headers[name] = value;
                }

                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    client.ConfigureRequest = request =>
                    {
                        foreach (var (name, value) in headers)
                        {
                            request.Headers[name] = value;
                        }
                    };
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .Build();
        return new HubClient(connection);
    }

    /// <summary>Opens the WebSocket transport directly (no negotiate), as a browser would after negotiating.</summary>
    public static async Task<System.Net.WebSockets.WebSocket> OpenRawWebSocketAsync(AgcoworkApiFactory factory, string slug, params (string Name, string? Value)[] headers)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var client = factory.Server.CreateWebSocketClient();
        var values = Headers(headers);
        client.ConfigureRequest = request =>
        {
            foreach (var (name, value) in values)
            {
                request.Headers[name] = value;
            }
        };
        return await client.ConnectAsync(new Uri($"ws://localhost{HubPath(slug)}"), Ct);
    }
}
