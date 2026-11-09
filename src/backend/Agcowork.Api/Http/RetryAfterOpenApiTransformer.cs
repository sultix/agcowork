using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Agcowork.Api.Http;

/// <summary>
/// Declares the <c>Retry-After</c> header on every documented 429 response (rate-limited endpoints, AP-043); it carries the
/// same seconds as <c>retryAfterSeconds</c> in the body (<see cref="ProblemDetailsMapping.ToProblem(Application.Common.Results.Error, HttpResponse)"/>).
/// </summary>
internal sealed class RetryAfterOpenApiTransformer : IOpenApiOperationTransformer
{
    public const string HeaderName = "Retry-After";

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Responses?.TryGetValue("429", out var tooMany) is true && tooMany is OpenApiResponse response)
        {
            response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
            response.Headers[HeaderName] = new OpenApiHeader
            {
                Description = "Seconds to wait before the next attempt (same value as 'retryAfterSeconds' in the body).",
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
            };
        }

        return Task.CompletedTask;
    }
}
