using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Agcowork.Api.Http;

/// <summary>
/// Documents the extensions <see cref="ProblemDetailsMapping"/> adds to <see cref="ProblemDetails"/>
/// (<c>code</c>, <c>errors[]</c>, <c>traceId</c>), so generated clients (Angular) know the full error shape.
/// </summary>
internal sealed class ProblemDetailsSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);
        if (context.JsonTypeInfo.Type != typeof(ProblemDetails))
        {
            return Task.CompletedTask;
        }

        schema.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        schema.Properties[ProblemDetailsMapping.CodeExtension] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "Machine-readable error code, stable across releases: validation_failed, unauthorized, forbidden, "
                + "not_found, bad_request, rate_limited, or a specific conflict code such as email_taken.",
        };
        schema.Properties["traceId"] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "W3C trace id of the request; quote it when reporting a problem.",
        };
        schema.Properties[ProblemDetailsMapping.RetryAfterSecondsExtension] = new OpenApiSchema
        {
            Type = JsonSchemaType.Integer,
            Description = "Only for 429 rate_limited: seconds to wait before the next attempt (same as the Retry-After header).",
        };
        schema.Properties["currentRevision"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Integer,
            Description = "Only for 409 revision_mismatch: the record's current revision. Retry with it as expectedRevision.",
        };
        schema.Properties["changedKeys"] = new OpenApiSchema
        {
            Type = JsonSchemaType.Array | JsonSchemaType.Null,
            Items = new OpenApiSchema { Type = JsonSchemaType.String },
            Description = "Only for 409 revision_mismatch: what changed since expectedRevision ('title', property keys, 'body'); null when that revision does not exist.",
        };
        schema.Properties[ProblemDetailsMapping.ErrorsExtension] = new OpenApiSchema
        {
            Type = JsonSchemaType.Array,
            Description = "Present for 400 validation errors and for 409 conflicts caused by a specific field. One entry per invalid field.",
            Items = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Required = new HashSet<string>(StringComparer.Ordinal) { "field", "code", "message" },
                Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                {
                    ["field"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "camelCase path of the input, e.g. 'email' or 'instanceScopes[1]'." },
                    ["code"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "required, invalid_format, not_in_options, too_long, too_short, out_of_range, not_allowed, conflict." },
                    ["message"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "What is wrong and how to fix it." },
                    ["allowed"] = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Array,
                        Items = new OpenApiSchema { Type = JsonSchemaType.String },
                        Description = "Accepted values, for enum-like fields only.",
                    },
                },
            },
        };

        return Task.CompletedTask;
    }
}
