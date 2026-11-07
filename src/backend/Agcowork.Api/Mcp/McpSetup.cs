using System.Reflection;
using Agcowork.Api.Auth;
using Agcowork.Api.Mcp.Tools;
using Agcowork.Api.Tenancy;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Mcp;

/// <summary>
/// The MCP server (architecture §4.2, AP-011): Streamable HTTP, stateless, two endpoints on one server registration.
/// <list type="bullet">
/// <item><c>/w/{workspaceSlug}/mcp</c>: the workspace tools and resources. Carries <see cref="TenantEndpointMetadata"/>, so the
/// tenancy middleware resolves the workspace and the membership before the MCP handler runs; unknown workspace, no membership
/// or a key for another workspace is a plain HTTP 404, no MCP answer.</item>
/// <item><c>/mcp</c>: the instance tools (workspaces, templates, me), no tenant.</item>
/// </list>
/// Both accept API keys only (<see cref="AuthPolicies.Agent"/>): MCP is for agents, a session cookie gets 401. Stateless means
/// every HTTP request builds its server from the request's DI scope, so the tenant set by the middleware is the tenant of every
/// tool call. Tools are thin adapters over the use cases; the per-request options pick the tool set by endpoint.
/// </summary>
internal static class McpSetup
{
    public const string WorkspacePattern = "/w/{" + TenantEndpointMetadata.SlugRouteValue + "}/mcp";
    public const string InstancePattern = "/mcp";

    public const string WorkspaceInstructions =
        "This server is bound to one agcowork workspace (a structured knowledge and work space: spaces, typed collections, records "
        + "with Markdown bodies). Start with workspace_overview to see the spaces and collections and what belongs where. Call "
        + "collection_describe before writing to a collection. Read with records_query or record_get; write with record_create, "
        + "record_update (send expectedRevision from record_get) and record_append (preferred for log-like additions). Errors are "
        + "structured: 'code', 'errors[]' with 'field' and 'allowed' values; correct the input and retry.";

    public const string InstanceInstructions =
        "Instance level of agcowork: who you are (me), your workspaces (workspaces_list), workspace templates and creating "
        + "workspaces. The content tools live on each workspace's own endpoint /w/{slug}/mcp.";

    /// <summary>Workspace tool classes, in listing order.</summary>
    public static IReadOnlyList<Type> WorkspaceToolTypes { get; } =
        [typeof(WorkspaceTools), typeof(CollectionTools), typeof(RecordTools), typeof(ViewAndActivityTools)];

    public static IReadOnlyList<Type> InstanceToolTypes { get; } = [typeof(InstanceTools)];

    public static IServiceCollection AddAgcoworkMcp(this IServiceCollection services)
    {
        services.AddSingleton<McpOutput>();
        services.AddSingleton<McpCatalog>();
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "agcowork", Version = typeof(McpSetup).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" };
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                options.ConfigureSessionOptions = (context, server, _) =>
                {
                    var catalog = context.RequestServices.GetRequiredService<McpCatalog>();
                    if (context.GetEndpoint()?.Metadata.GetMetadata<TenantEndpointMetadata>() is not null)
                    {
                        server.ToolCollection = catalog.WorkspaceTools;
                        server.ResourceCollection = catalog.WorkspaceResources;
                        server.Handlers.ListResourcesHandler = WorkspaceResources.ListAsync;
                        server.ServerInstructions = WorkspaceInstructions;
                    }
                    else
                    {
                        server.ToolCollection = catalog.InstanceTools;
                        server.ServerInstructions = InstanceInstructions;
                    }

                    return Task.CompletedTask;
                };
            })
            .WithRequestFilters(filters => filters.AddCallToolFilter(McpToolPipeline.Filter));
        return services;
    }

    public static IEndpointRouteBuilder MapAgcoworkMcp(this IEndpointRouteBuilder app)
    {
        // Not part of the REST contract (OpenAPI): MCP has its own discovery (tools/list).
        app.MapMcp(WorkspacePattern)
            .WithMetadata(TenantEndpointMetadata.Instance)
            .RequireAuthorization(AuthPolicies.Agent)
            .ExcludeFromDescription();

        app.MapMcp(InstancePattern)
            .RequireAuthorization(AuthPolicies.Agent)
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>Builds the tool collections of a set of tool classes (static methods with <see cref="McpServerToolAttribute"/>).</summary>
    internal static McpServerPrimitiveCollection<McpServerTool> BuildTools(IEnumerable<Type> types, IServiceProvider services, McpOutput output)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in types.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is null)
            {
                continue;
            }

            var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions { Services = services, SerializerOptions = output.Json });
            McpDescriptions.Apply(tool.ProtocolTool);
            collection.Add(tool);
        }

        return collection;
    }

    internal static McpServerResourceCollection BuildResources(Type type, IServiceProvider services)
    {
        var collection = new McpServerResourceCollection();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.GetCustomAttribute<McpServerResourceAttribute>() is not null)
            {
                collection.Add(McpServerResource.Create(method, target: null, new McpServerResourceCreateOptions { Services = services }));
            }
        }

        return collection;
    }
}

/// <summary>The tool and resource sets, built once; each stateless request picks one by endpoint.</summary>
internal sealed class McpCatalog(IServiceProvider services, McpOutput output)
{
    private readonly Lazy<McpServerPrimitiveCollection<McpServerTool>> _workspaceTools = new(() => McpSetup.BuildTools(McpSetup.WorkspaceToolTypes, services, output));
    private readonly Lazy<McpServerPrimitiveCollection<McpServerTool>> _instanceTools = new(() => McpSetup.BuildTools(McpSetup.InstanceToolTypes, services, output));
    private readonly Lazy<McpServerResourceCollection> _resources = new(() => McpSetup.BuildResources(typeof(WorkspaceResources), services));

    public McpServerPrimitiveCollection<McpServerTool> WorkspaceTools => _workspaceTools.Value;

    public McpServerPrimitiveCollection<McpServerTool> InstanceTools => _instanceTools.Value;

    public McpServerResourceCollection WorkspaceResources => _resources.Value;

    /// <summary>Parameter types by tool and parameter name, to name the argument a binder error belongs to.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, Type>> ParameterTypes { get; } =
        McpSetup.WorkspaceToolTypes.Concat(McpSetup.InstanceToolTypes)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name is not null)
            .ToDictionary(
                m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!,
                m => (IReadOnlyDictionary<string, Type>)m.GetParameters().ToDictionary(p => p.Name!, p => p.ParameterType, StringComparer.Ordinal),
                StringComparer.Ordinal);
}
