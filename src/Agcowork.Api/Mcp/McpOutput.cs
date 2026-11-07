using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Agcowork.Api.Http;
using Agcowork.Application.Common.Results;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace Agcowork.Api.Mcp;

/// <summary>
/// Turns use-case results into MCP tool results: <c>structuredContent</c> is the JSON the REST endpoint would answer
/// (same serializer settings, so both adapters agree), the text block a readable rendering for clients that only show text.
/// Failures become <c>isError: true</c> with the ProblemDetails members of REST (<c>status</c>, <c>code</c>, <c>detail</c>,
/// <c>errors[]</c> with <c>allowed</c>, conflict details such as <c>currentRevision</c>), so an agent can correct itself.
/// </summary>
internal sealed class McpOutput(IOptions<JsonOptions> jsonOptions)
{
    /// <summary>The REST serializer settings (camelCase, snake_case enums, nulls omitted).</summary>
    public JsonSerializerOptions Json { get; } = jsonOptions.Value.SerializerOptions;

    /// <summary>Success with a rendered text; failure as structured error.</summary>
    public CallToolResult From<T>(Result<T> result, Func<T, string> render)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(render);
        return result.IsSuccess ? Success(result.Value, render(result.Value)) : Failure(result.Error);
    }

    /// <summary>Success: <paramref name="value"/> as <c>structuredContent</c>, <paramref name="text"/> as text block.</summary>
    public CallToolResult Success<T>(T value, string text) => new()
    {
        StructuredContent = JsonSerializer.SerializeToElement(value, Json),
        Content = [new TextContentBlock { Text = text }],
    };

    /// <summary>Summary line plus the JSON, for results without a dedicated rendering.</summary>
    public string SummaryWithJson<T>(string summary, T value) =>
        $"{summary}\n\n```json\n{JsonSerializer.Serialize(value, Json)}\n```";

    /// <summary>A failure in the shape of the REST ProblemDetails.</summary>
    public CallToolResult Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var problem = ProblemDetailsMapping.ToProblemDetails(error);
        if (Activity.Current is { } activity)
        {
            problem.Extensions.TryAdd("traceId", activity.TraceId.ToString());
        }

        var element = JsonSerializer.SerializeToElement(problem, Json);
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = element,
            Content = [new TextContentBlock { Text = RenderProblem(problem.Status, error, element) }],
        };
    }

    /// <summary>An unexpected failure (500): logged by the caller, nothing about it on the wire.</summary>
    public CallToolResult Internal()
    {
        var problem = new Dictionary<string, object?>
        {
            ["status"] = StatusCodes.Status500InternalServerError,
            ["code"] = ProblemDetailsMapping.CodeForStatus(StatusCodes.Status500InternalServerError),
            ["traceId"] = Activity.Current?.TraceId.ToString(),
        };
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(problem, Json),
            Content = [new TextContentBlock { Text = "Error 500: internal_error. The failure was logged; retrying later may help." }],
        };
    }

    /// <summary>A failure for invalid tool input detected by the adapter itself (e.g. a malformed filter).</summary>
    public CallToolResult InvalidInput(string field, string code, string message, IReadOnlyList<string>? allowed = null) =>
        Failure(new ValidationError([new FieldError(field, code, message, allowed)]));

    private string RenderProblem(int? status, Error error, JsonElement problem)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Error {status}: {error.Code}");
        if (problem.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
        {
            text.Append(CultureInfo.InvariantCulture, $" – {detail.GetString()}");
        }

        if (error is ValidationError validation)
        {
            foreach (var field in validation.Errors)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n- {field.Field}: {field.Code} – {field.Message}");
                if (field.Allowed is { Count: > 0 } allowed)
                {
                    text.Append(CultureInfo.InvariantCulture, $" (allowed: {string.Join(", ", allowed)})");
                }
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"\n\n```json\n{JsonSerializer.Serialize(problem, Json)}\n```");
        return text.ToString();
    }
}
