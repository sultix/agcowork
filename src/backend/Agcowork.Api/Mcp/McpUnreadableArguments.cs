using System.Text.Json;
using System.Text.Json.Nodes;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Validation;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agcowork.Api.Mcp;

/// <summary>
/// Tool arguments with text that cannot be read at all: an escaped unpaired surrogate (<c>"\ud800"</c>) in a value or a
/// member name. The SDK would fail to deserialize the whole <c>tools/call</c> request and answer a bare JSON-RPC error
/// (-32603), before any tool or filter of ours runs. This incoming message filter runs first: it replaces each such string
/// with an empty one, so the request can be read, and records its field (<c>title</c>, <c>properties.notes</c>,
/// <c>schema[0].options[0].label</c>) in the message's items. <see cref="McpToolPipeline"/> then answers the call with
/// <c>invalid_value</c> on these fields, like a use case reports NUL (architecture §4.1, "Fehler sind Hinweise").
/// Registered in <see cref="McpSetup"/>.
/// </summary>
internal static class McpUnreadableArguments
{
    /// <summary>Key in the message items: the fields (<see cref="IReadOnlyList{T}"/> of string) whose text could not be read.</summary>
    public const string ItemsKey = "agcowork.unreadable_argument_fields";

    public static McpMessageHandler Filter(McpMessageHandler next) =>
        (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is JsonRpcRequest { Method: RequestMethods.ToolsCall, Params: JsonObject parameters }
                && parameters["arguments"] is JsonObject arguments)
            {
                var fields = new List<string>();
                if (Sanitize(arguments, null, fields) is { } replacement)
                {
                    parameters["arguments"] = replacement;
                }

                if (fields.Count > 0)
                {
                    context.Items[ItemsKey] = fields;
                }
            }

            return next(context, cancellationToken);
        };

    /// <summary>The fields recorded by <see cref="Filter"/> for this call, as a validation error; null when there are none.</summary>
    public static ValidationError? ErrorOf(IDictionary<string, object?> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.TryGetValue(ItemsKey, out var value) && value is IReadOnlyList<string> { Count: > 0 } fields
            ? new ValidationError([.. fields.Select(f => new FieldError(f, ErrorCodes.InvalidValue, $"'{f}' {StorableText.Hint}"))])
            : null;
    }

    /// <summary>Replaces unreadable strings below <paramref name="node"/>; returns a replacement for the node itself, or null to keep it.</summary>
    private static JsonNode? Sanitize(JsonNode? node, string? path, List<string> fields)
    {
        switch (node)
        {
            case JsonObject obj:
                List<KeyValuePair<string, JsonNode?>> members;
                try
                {
                    // Member names are unescaped here: one with an unpaired surrogate makes the whole object unreadable.
                    members = [.. obj];
                }
                catch (InvalidOperationException)
                {
                    fields.Add(path ?? McpToolPipeline.ArgumentsField);
                    return new JsonObject();
                }

                foreach (var (name, child) in members)
                {
                    if (Sanitize(child, path is null ? name : $"{path}.{name}", fields) is { } replacement)
                    {
                        obj[name] = replacement;
                    }
                }

                return null;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (Sanitize(array[i], $"{path}[{i}]", fields) is { } replacement)
                    {
                        array[i] = replacement;
                    }
                }

                return null;

            case JsonValue value when value.GetValueKind() == JsonValueKind.String && !IsReadable(value):
                fields.Add(path ?? McpToolPipeline.ArgumentsField);
                return JsonValue.Create(string.Empty);

            default:
                return null;
        }
    }

    private static bool IsReadable(JsonValue value)
    {
        try
        {
            _ = value.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
