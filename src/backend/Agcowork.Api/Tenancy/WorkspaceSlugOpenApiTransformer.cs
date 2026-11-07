using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Agcowork.Api.Tenancy;

/// <summary>
/// Documents the <c>{workspaceSlug}</c> path parameter on every workspace endpoint. Handlers do not bind
/// it (the tenancy middleware reads it), so the generator would otherwise omit it.
/// </summary>
internal sealed class WorkspaceSlugOpenApiTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<TenantEndpointMetadata>().Any())
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        if (operation.Parameters.Any(p => string.Equals(p.Name, TenantEndpointMetadata.SlugRouteValue, StringComparison.Ordinal)))
        {
            return Task.CompletedTask;
        }

        operation.Parameters.Insert(0, new OpenApiParameter
        {
            Name = TenantEndpointMetadata.SlugRouteValue,
            In = ParameterLocation.Path,
            Required = true,
            Description = "Slug of the workspace (see GET /api/v1/workspaces). Unknown workspaces and workspaces the caller "
                + "is not a member of both answer 404.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        return Task.CompletedTask;
    }
}
