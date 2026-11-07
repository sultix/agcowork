using Agcowork.Application.Common;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Common.Security;

namespace Agcowork.Application.Workspaces.Templates;

public sealed record ListWorkspaceTemplatesRequest;

/// <summary>A workspace template one can pass as <c>templateId</c> when creating a workspace.</summary>
/// <param name="Id">Value for <c>templateId</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">What the template creates.</param>
/// <param name="IsDefault">Applied when <c>templateId</c> is omitted.</param>
public sealed record WorkspaceTemplateDto(string Id, string Name, string Description, bool IsDefault);

/// <summary>Lists the available workspace templates. Instance level: any authenticated caller.</summary>
public sealed class ListWorkspaceTemplates(ICurrentActor currentActor)
    : IUseCase<ListWorkspaceTemplatesRequest, IReadOnlyList<WorkspaceTemplateDto>>
{
    public Task<Result<IReadOnlyList<WorkspaceTemplateDto>>> HandleAsync(ListWorkspaceTemplatesRequest request, CancellationToken cancellationToken)
    {
        if (currentActor.RequireAuthenticated() is { } denied)
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<WorkspaceTemplateDto>>(denied));
        }

        return Task.FromResult(Result.Success<IReadOnlyList<WorkspaceTemplateDto>>(
            [.. WorkspaceTemplates.All.Select(t => new WorkspaceTemplateDto(t.Id, t.Name, t.Description, t.Id == WorkspaceTemplates.DefaultId))]));
    }
}
