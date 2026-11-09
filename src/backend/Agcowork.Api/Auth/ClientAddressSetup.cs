using System.Net;
using Agcowork.Application.Common.Security;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Agcowork.Api.Auth;

/// <summary>
/// Client address of a request (AP-043), for the login throttling. By default the connection's address. Only when
/// <c>Agcowork:ForwardedHeaders</c> names trusted proxies (<c>KnownProxies</c>: addresses, <c>KnownNetworks</c>: CIDR), the
/// last <c>X-Forwarded-For</c> entry a trusted proxy added replaces it; headers from any other peer are ignored.
/// </summary>
/// <remarks>
/// Docker Compose: the web container (nginx) has a fixed address that the api trusts (see <c>docker-compose.yml</c>).
/// Behind a further proxy, add its address and raise <c>ForwardLimit</c> by one per proxy. A missing entry would let all
/// users share one address and its login limit; <see cref="ForwardedHeadersWarnings"/> logs a hint when headers look like that.
/// </remarks>
internal static class ClientAddressSetup
{
    public const string SectionName = "Agcowork:ForwardedHeaders";

    public static IServiceCollection AddAgcoworkClientAddress(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IClientAddress, HttpClientAddress>();
        services.AddSingleton<ForwardedHeadersWarnings>();

        services.AddOptions<ForwardedHeadersSettings>()
            .BindConfiguration(SectionName)
            .Validate(settings => settings.Errors().Count == 0, $"Invalid {SectionName}: KnownProxies must be IP addresses, KnownNetworks CIDR ranges (e.g. 172.30.43.0/24), ForwardLimit at least 1.")
            .ValidateOnStart();

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<ForwardedHeadersSettings>>((options, settings) => settings.Value.ApplyTo(options));

        return services;
    }

    /// <summary>
    /// First middleware of the pipeline: applies trusted <c>X-Forwarded-For</c> (only when proxies are configured), then
    /// checks what is left of the header (<see cref="ForwardedHeadersWarnings"/>).
    /// </summary>
    public static WebApplication UseAgcoworkForwardedHeaders(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var settings = app.Services.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value;
        if (settings.IsConfigured)
        {
            app.UseForwardedHeaders();
        }

        var warnings = app.Services.GetRequiredService<ForwardedHeadersWarnings>();
        app.Use((context, next) =>
        {
            warnings.Inspect(context, settings);
            return next(context);
        });
        return app;
    }
}

/// <summary>Bound from <c>Agcowork:ForwardedHeaders</c>; an invalid value fails the host start.</summary>
internal sealed class ForwardedHeadersSettings
{
    /// <summary>Addresses of proxies whose <c>X-Forwarded-For</c> is trusted.</summary>
    public IReadOnlyList<string>? KnownProxies { get; set; }

    /// <summary>CIDR ranges of proxies whose <c>X-Forwarded-For</c> is trusted.</summary>
    public IReadOnlyList<string>? KnownNetworks { get; set; }

    /// <summary>How many <c>X-Forwarded-For</c> entries are read from the right, one per trusted proxy in the chain.</summary>
    public int ForwardLimit { get; set; } = 1;

    public bool IsConfigured => KnownProxies is { Count: > 0 } || KnownNetworks is { Count: > 0 };

    public IReadOnlyList<string> Errors()
    {
        var errors = new List<string>();
        errors.AddRange((KnownProxies ?? []).Where(proxy => !IPAddress.TryParse(proxy, out _)).Select(proxy => $"KnownProxies: '{proxy}'"));
        errors.AddRange((KnownNetworks ?? []).Where(network => !System.Net.IPNetwork.TryParse(network, out _)).Select(network => $"KnownNetworks: '{network}'"));
        if (ForwardLimit < 1)
        {
            errors.Add("ForwardLimit");
        }

        return errors;
    }

    /// <summary>
    /// Only <c>X-Forwarded-For</c>, only from the configured proxies. The framework defaults (loopback trusted) are dropped,
    /// so nothing is trusted that was not configured.
    /// </summary>
    public void ApplyTo(ForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        options.ForwardLimit = ForwardLimit;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in KnownProxies ?? [])
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        foreach (var network in KnownNetworks ?? [])
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    }
}

/// <summary><see cref="IClientAddress"/> of the current HTTP request (after the forwarded-headers middleware).</summary>
internal sealed class HttpClientAddress(IHttpContextAccessor httpContextAccessor) : IClientAddress
{
    public IPAddress? Address => httpContextAccessor.HttpContext?.Connection.RemoteIpAddress;
}
