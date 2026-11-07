using Agcowork.Application.Common.Results;

namespace Agcowork.Application.Workspaces.Templates;

/// <summary>
/// Starting structure of a new workspace (spaces, collections, record templates). Applied by
/// <see cref="CreateWorkspace"/> inside its transaction and with the tenant set to the new workspace, always
/// through the regular use cases (validation, standard views and other hooks run as for any other caller).
/// </summary>
public interface IWorkspaceTemplate
{
    /// <summary>Stable id used as <c>templateId</c>, e.g. <c>software-projekt</c>.</summary>
    string Id { get; }

    /// <summary>Display name.</summary>
    string Name { get; }

    /// <summary>What the template contains and who it is for; agents choose by it.</summary>
    string Description { get; }

    /// <summary>
    /// Creates the content. A failure result makes the caller roll everything back.
    /// </summary>
    /// <param name="services">Scope that resolves the use cases; its tenant is the new workspace.</param>
    /// <param name="workspaceId">The new workspace (equals the scope's tenant).</param>
    /// <param name="actorId">The creator, who is the owner.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<Result> ApplyAsync(IServiceProvider services, Guid workspaceId, Guid actorId, CancellationToken cancellationToken);
}
