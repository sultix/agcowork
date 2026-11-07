using Agcowork.Application.Common.Results;

namespace Agcowork.Application.Workspaces.Templates;

/// <summary>The workspace templates of this instance (in code until templates become data, phase 3).</summary>
public static class WorkspaceTemplates
{
    public const string EmptyId = "empty";

    public const string DefaultId = SoftwareProjectTemplate.TemplateId;

    public static IReadOnlyList<IWorkspaceTemplate> All { get; } = [new SoftwareProjectTemplate(), new EmptyTemplate()];

    /// <summary>Ids accepted as <c>templateId</c>, default first.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. All.Select(t => t.Id)];

    public static IWorkspaceTemplate? Find(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    private sealed class EmptyTemplate : IWorkspaceTemplate
    {
        public string Id => EmptyId;

        public string Name => "Leer";

        public string Description => "Ein leerer Workspace ohne Spaces und Collections. Gedacht für Fälle, in denen die Struktur selbst entworfen wird.";

        public Task<Result> ApplyAsync(IServiceProvider services, Guid workspaceId, Guid actorId, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }
}
