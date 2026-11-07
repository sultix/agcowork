using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;
using Agcowork.Application.Tenancy;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Mcp;

/// <summary>
/// Wraps every tool call (architecture §5 Observability, AP-011): one span per call with <c>tool</c>, <c>actor_id</c> and
/// <c>workspace_id</c>, and no exception ever leaves a tool.
/// <list type="number">
/// <item>Before the call the top-level arguments are checked against the tool's input schema (required, JSON type, uuid and
/// date-time formats): such mistakes come back as the same structured validation error as a use-case failure, with the
/// parameter name as field, independent of the SDK binder's wording.</item>
/// <item>An exception thrown by our own code (any stack frame in an <c>Agcowork.*</c> assembly) is a bug: logged, answered as
/// <c>internal_error</c> with status, code and traceId only, exactly like the REST 500.</item>
/// <item>Only exceptions that come purely from the SDK's argument binder (nested values of the wrong shape) become
/// <c>required</c>/<c>invalid_value</c>.</item>
/// </list>
/// </summary>
internal static partial class McpToolPipeline
{
    /// <summary>Source of the tool spans; equal to the application name, which the service defaults already export.</summary>
    public const string ActivitySourceName = "Agcowork.Api";

    public const string SpanPrefix = "mcp.tool ";

    /// <summary>Field reported when a binder error names no parameter.</summary>
    public const string ArgumentsField = "arguments";

    public static ActivitySource Source { get; } = new(ActivitySourceName);

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var tool = request.Params?.Name ?? "unknown";
            var services = request.Services;
            using var activity = Source.StartActivity(SpanPrefix + tool, ActivityKind.Internal);
            if (activity is not null)
            {
                var actor = services?.GetService<ICurrentActor>();
                activity.SetTag("tool", tool);
                activity.SetTag("actor_id", actor is { IsAuthenticated: true } ? actor.Id.ToString() : null);
                activity.SetTag("workspace_id", services?.GetService<ITenantContextAccessor>()?.Current?.WorkspaceId.ToString());
            }

            var output = services?.GetRequiredService<McpOutput>() ?? throw new InvalidOperationException("MCP tool call without request services.");
            CallToolResult result;
            if (request.MatchedPrimitive is McpServerTool matched && CheckArguments(matched.ProtocolTool, request.Params?.Arguments) is { } invalid)
            {
                result = output.Failure(invalid);
            }
            else
            {
                try
                {
                    result = await next(request, cancellationToken);
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or McpProtocolException))
                {
                    result = FromException(services, output, tool, request.Params?.Arguments, exception);
                }
            }

            if (activity is not null && result.IsError == true)
            {
                activity.SetStatus(ActivityStatusCode.Error);
                if (result.StructuredContent is { } problem && problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("code", out var code))
                {
                    activity.SetTag("error_code", code.GetString());
                }
            }

            return result;
        };

    /// <summary>Top-level argument check against the input schema; null when the arguments fit.</summary>
    internal static ValidationError? CheckArguments(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        var schema = tool.InputSchema;
        var errors = new List<FieldError>();
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Select(e => e.GetString()!))
            {
                if (arguments is null || !arguments.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    errors.Add(new FieldError(name, ErrorCodes.Required, $"'{name}' is required. See the input schema of '{tool.Name}'."));
                }
            }
        }

        if (arguments is not null && schema.TryGetProperty("properties", out var properties))
        {
            foreach (var (name, value) in arguments)
            {
                if (properties.TryGetProperty(name, out var property) && Mismatch(property, value) is { } expected)
                {
                    errors.Add(new FieldError(name, ErrorCodes.InvalidValue, $"'{name}' must be {expected}."));
                }
            }
        }

        return errors.Count == 0 ? null : new ValidationError(errors);
    }

    /// <summary>What the schema expects when <paramref name="value"/> does not fit, else null. Nested values are left to the binder and the use case.</summary>
    private static string? Mismatch(JsonElement property, JsonElement value)
    {
        if (!property.TryGetProperty("type", out var typeNode))
        {
            return null;
        }

        var types = typeNode.ValueKind == JsonValueKind.Array
            ? typeNode.EnumerateArray().Select(t => t.GetString()!).ToList()
            : [typeNode.GetString()!];
        if (value.ValueKind == JsonValueKind.Null)
        {
            return types.Contains("null") ? null : "a value, not null";
        }

        var format = property.TryGetProperty("format", out var formatNode) ? formatNode.GetString() : null;
        var fits = types.Any(type => type switch
        {
            "string" => value.ValueKind == JsonValueKind.String && format switch
            {
                "uuid" => Guid.TryParse(value.GetString(), out _),
                "date-time" => DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                _ => true,
            },
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            _ => true,
        });
        if (fits)
        {
            return null;
        }

        var expected = string.Join(" or ", types.Where(t => t != "null").Select(t => (t, format) switch
        {
            ("string", "uuid") => "a UUID string",
            ("string", "date-time") => "an ISO 8601 date-time string",
            ("integer", _) => "an integer",
            ("boolean", _) => "true or false",
            ("object", _) => "an object",
            ("array", _) => "an array",
            _ => $"a {t}",
        }));
        return expected;
    }

    private static CallToolResult FromException(
        IServiceProvider services, McpOutput output, string tool, IDictionary<string, JsonElement>? arguments, Exception exception)
    {
        // Thrown by the SDK's argument binder before our tool method ran: the caller sent a nested value of the wrong shape.
        if (!ThrownByOurCode(exception) && exception is ArgumentException or JsonException or FormatException or InvalidCastException)
        {
            if (FindNestedMismatch(services, output, tool, arguments) is { } nested)
            {
                return output.InvalidInput(nested.Field, ErrorCodes.InvalidValue, $"The value does not match the input schema of '{tool}': {nested.Message}");
            }

            var field = BinderField(exception);
            var missing = exception.Message.Contains("missing", StringComparison.OrdinalIgnoreCase);
            return output.InvalidInput(
                field,
                missing ? ErrorCodes.Required : ErrorCodes.InvalidValue,
                missing
                    ? $"'{field}' is required. See the input schema of '{tool}'."
                    : string.Create(CultureInfo.InvariantCulture, $"The value does not match the input schema of '{tool}': {BinderDetail(exception)}"));
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpToolPipeline));
        LogToolFailed(logger, tool, exception);
        return output.Internal();
    }

    /// <summary>
    /// Names the argument behind a binder <see cref="JsonException"/> (which carries no parameter name): each argument is
    /// deserialized on its own into its parameter type, the first failure gives the field (parameter plus JSON path, e.g.
    /// <c>schema[0].required</c>).
    /// </summary>
    private static (string Field, string Message)? FindNestedMismatch(
        IServiceProvider services, McpOutput output, string tool, IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || !services.GetRequiredService<McpCatalog>().ParameterTypes.TryGetValue(tool, out var parameters))
        {
            return null;
        }

        foreach (var (name, value) in arguments)
        {
            if (!parameters.TryGetValue(name, out var type))
            {
                continue;
            }

            try
            {
                _ = value.Deserialize(type, output.Json);
            }
            catch (JsonException exception)
            {
                var path = exception.Path is { Length: > 1 } p ? p[1..] : string.Empty;
                return (name + path, $"invalid value at '{name + path}'.");
            }
        }

        return null;
    }

    /// <summary>
    /// True when any frame of the exception (or an inner one) lies in an <c>Agcowork.*</c> assembly: then it is our bug, not bad
    /// input. The frames of this filter are skipped: the trace runs up to the catch here.
    /// </summary>
    internal static bool ThrownByOurCode(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var frames = new StackTrace(current, fNeedFileInfo: false).GetFrames();
            if (frames.Any(frame => frame.GetMethod()?.DeclaringType is { } type && IsOurs(type)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOurs(Type type)
    {
        if (type.Assembly.GetName().Name?.StartsWith("Agcowork.", StringComparison.Ordinal) != true)
        {
            return false;
        }

        for (var outer = type; outer is not null; outer = outer.DeclaringType)
        {
            if (outer == typeof(McpToolPipeline))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The parameter the binder names: "… the required parameter 'markdown'. (Parameter 'arguments')" names it in quotes; a
    /// <see cref="JsonException"/> carries no name, so the field is <see cref="ArgumentsField"/> and the JSON path goes into the message.
    /// </summary>
    private static string BinderField(Exception exception) =>
        QuotedName().Matches(exception.Message).Select(m => m.Groups[1].Value).FirstOrDefault(name => name != ArgumentsField)
        ?? (exception is ArgumentException { ParamName: { Length: > 0 } name } && name != ArgumentsField ? name : ArgumentsField);

    private static string BinderDetail(Exception exception) =>
        exception is JsonException { Path: { Length: > 0 } path } ? $"invalid value at JSON path {path}." : exception.Message;

    [GeneratedRegex("'([A-Za-z][A-Za-z0-9]*)'")]
    private static partial Regex QuotedName();

    [LoggerMessage(Level = LogLevel.Error, Message = "MCP tool {Tool} failed.")]
    private static partial void LogToolFailed(ILogger logger, string tool, Exception exception);
}
