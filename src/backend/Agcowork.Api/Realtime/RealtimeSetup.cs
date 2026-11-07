using System.Text.Json;
using System.Text.Json.Serialization;
using Agcowork.Api.Auth;
using Agcowork.Api.Tenancy;
using Agcowork.Application.Realtime;
using Agcowork.Infrastructure.Realtime;

namespace Agcowork.Api.Realtime;

/// <summary>
/// Realtime (AP-024): SignalR hub per workspace (<c>/w/{workspaceSlug}/hub</c>, cookie or bearer key) and the dispatcher that
/// delivers the activity log to it. Not part of the REST contract (OpenAPI).
/// </summary>
internal static class RealtimeSetup
{
    public const string HubPattern = "/w/{" + TenantEndpointMetadata.SlugRouteValue + "}/hub";

    /// <param name="services">The host's services.</param>
    /// <param name="startDispatcher">False only for build-time OpenAPI generation, which runs without a database.</param>
    public static IServiceCollection AddAgcoworkRealtime(this IServiceCollection services, bool startDispatcher = true)
    {
        // Same JSON as REST: snake_case enums, nulls omitted (e.g. targetTitle of member events).
        services.AddSignalR().AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
            options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        services.AddRealtimeOptions();
        services.AddSingleton<RealtimeConnections>();
        services.AddSingleton<IRealtimeConnections>(sp => sp.GetRequiredService<RealtimeConnections>());
        if (startDispatcher)
        {
            services.AddActivityDispatcher();
        }

        return services;
    }

    /// <summary>After the tenancy middleware: authorizes hub requests and hands the subscriber to the hub.</summary>
    public static IApplicationBuilder UseAgcoworkRealtime(this IApplicationBuilder app) => app.UseMiddleware<RealtimeConnectMiddleware>();

    public static IEndpointRouteBuilder MapAgcoworkRealtime(this IEndpointRouteBuilder app)
    {
        // Negotiate and transport endpoints both carry the metadata, so both pass tenant resolution and the read check.
        app.MapHub<WorkspaceHub>(HubPattern)
            .WithMetadata(TenantEndpointMetadata.Instance, RealtimeHubMetadata.Instance)
            .RequireAuthorization(AuthPolicies.Authenticated);
        return app;
    }
}
