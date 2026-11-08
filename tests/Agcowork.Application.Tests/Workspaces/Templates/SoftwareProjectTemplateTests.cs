using Agcowork.Application.Collections;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Workspaces;
using Agcowork.Domain.Collections;
using Agcowork.Domain.Collections.Schema;
using Agcowork.Domain.Identity;
using Agcowork.Domain.Views;
using Agcowork.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Workspaces.Templates;

public sealed class SoftwareProjectTemplateTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Apply_SoftwareProjekt_CreatesThreeSpacesAndFourCollections()
    {
        var (workspace, _) = await CreateAsync("acme", "software-projekt");

        var spaces = await Db.Spaces.AsNoTracking().Where(s => s.WorkspaceId == workspace.Id).OrderBy(s => s.SortOrder).ToListAsync(Ct);
        Assert.Equal(["Produkt", "Technik", "Notizen"], spaces.Select(s => s.Name));
        Assert.All(spaces, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));
        Assert.All(spaces, s => Assert.False(string.IsNullOrWhiteSpace(s.Icon)));

        var collections = await CollectionsAsync(workspace.Id);
        Assert.Equal(["Analysen", "Aufgaben", "Entscheidungen", "Komponenten"], collections.Keys.Order());
        Assert.Equal(spaces[0].Id, collections["Aufgaben"].SpaceId);
        Assert.Equal(spaces[0].Id, collections["Analysen"].SpaceId);
        Assert.Equal(spaces[1].Id, collections["Entscheidungen"].SpaceId);
        Assert.Equal(spaces[1].Id, collections["Komponenten"].SpaceId);
        Assert.All(collections.Values, c => Assert.True(c.Description.Length > 100, "descriptions are written for agents"));
    }

    [Fact]
    public async Task Apply_SoftwareProjekt_CollectionsHaveExpectedKeysTypesAndOptions()
    {
        var (workspace, _) = await CreateAsync("acme", null);
        var collections = await CollectionsAsync(workspace.Id);

        Assert.Equal(
            [("title", PropertyType.Text), ("status", PropertyType.Status), ("owner", PropertyType.Actor), ("priority", PropertyType.Select), ("sprint", PropertyType.Select),
                ("related_decision", PropertyType.Relation), ("due", PropertyType.Date), ("estimate", PropertyType.Number)],
            KeysAndTypes(collections["Aufgaben"]));
        Assert.Equal(
            [("title", PropertyType.Text), ("kind", PropertyType.Select), ("author", PropertyType.Actor), ("sources", PropertyType.Markdown), ("confidence", PropertyType.Select),
                ("summary", PropertyType.Text)],
            KeysAndTypes(collections["Analysen"]));
        Assert.Equal(
            [("title", PropertyType.Text), ("status", PropertyType.Status), ("decided_by", PropertyType.Actor), ("date", PropertyType.Date), ("affects", PropertyType.Relation),
                ("supersedes", PropertyType.Relation)],
            KeysAndTypes(collections["Entscheidungen"]));
        Assert.Equal(
            [("title", PropertyType.Text), ("kind", PropertyType.Select), ("owner", PropertyType.Actor), ("repo_url", PropertyType.Url), ("status", PropertyType.Status)],
            KeysAndTypes(collections["Komponenten"]));

        var task = collections["Aufgaben"].Schema;
        Assert.Equal(["Offen", "In Arbeit", "Erledigt", "Verworfen"], Property(task, "status").Options!.Select(o => o.Label));
        Assert.Equal([StatusGroup.Todo, StatusGroup.InProgress, StatusGroup.Done, StatusGroup.Done], Property(task, "status").Options!.Select(o => o.Group!.Value));
        Assert.Equal(["Niedrig", "Mittel", "Hoch", "Kritisch"], Property(task, "priority").Options!.Select(o => o.Label));
        Assert.Empty(Property(task, "sprint").Options!);
        Assert.Equal("h", Property(task, "estimate").Unit);
        Assert.True(Property(collections["Analysen"].Schema, "summary").Required);
        Assert.Equal(["Markt", "Technik", "Wettbewerb", "Nutzer"], Property(collections["Analysen"].Schema, "kind").Options!.Select(o => o.Label));
        Assert.Equal(["Service", "Bibliothek", "Datenbank", "UI", "Infrastruktur"], Property(collections["Komponenten"].Schema, "kind").Options!.Select(o => o.Label));
        Assert.Equal(["Geplant", "Aktiv", "Veraltet"], Property(collections["Komponenten"].Schema, "status").Options!.Select(o => o.Label));
        Assert.Equal(["Vorgeschlagen", "Akzeptiert", "Verworfen", "Ersetzt"], Property(collections["Entscheidungen"].Schema, "status").Options!.Select(o => o.Label));
        Assert.All(collections.Values.SelectMany(c => c.Schema).Where(p => p.Key != "title"), p => Assert.False(string.IsNullOrWhiteSpace(p.Description), p.Key));
    }

    [Fact]
    public async Task Apply_SoftwareProjekt_RelationsPointToTheRightCollections()
    {
        var (workspace, _) = await CreateAsync("acme", null);
        var collections = await CollectionsAsync(workspace.Id);

        var decisions = collections["Entscheidungen"];
        var supersedes = Property(decisions.Schema, "supersedes");
        Assert.Equal(decisions.Id, supersedes.TargetCollectionId);
        var affects = Property(decisions.Schema, "affects");
        Assert.Equal(collections["Komponenten"].Id, affects.TargetCollectionId);
        Assert.True(affects.Multiple);
        Assert.Equal(decisions.Id, Property(collections["Aufgaben"].Schema, "related_decision").TargetCollectionId);
        Assert.Equal(2, decisions.SchemaVersion);
    }

    [Fact]
    public async Task Apply_SoftwareProjekt_EachCollectionHasDefaultRecordTemplate()
    {
        var (workspace, _) = await CreateAsync("acme", null);
        var collections = await CollectionsAsync(workspace.Id);
        var templates = await Db.Templates.AsNoTracking().Where(t => t.WorkspaceId == workspace.Id).ToListAsync(Ct);

        Assert.Equal(4, templates.Count);
        Assert.All(templates, t => Assert.True(t.IsDefault));
        Assert.Contains("## Kontext", Body("Entscheidungen"));
        Assert.Contains("## Optionen", Body("Entscheidungen"));
        Assert.Contains("## Entscheidung", Body("Entscheidungen"));
        Assert.Contains("## Konsequenzen", Body("Entscheidungen"));
        Assert.Contains("## Ziel", Body("Aufgaben"));
        Assert.Contains("## Akzeptanzkriterien", Body("Aufgaben"));
        Assert.Contains("## Fragestellung", Body("Analysen"));
        Assert.Contains("## Befunde", Body("Analysen"));
        Assert.Contains("## Empfehlung", Body("Analysen"));
        Assert.Contains("## Zweck", Body("Komponenten"));
        Assert.Contains("## Schnittstellen", Body("Komponenten"));

        string Body(string collection) => Assert.Single(templates, t => t.CollectionId == collections[collection].Id).BodyMarkdown;
    }

    [Fact]
    public async Task Describe_SoftwareProjektCollections_ReturnValidJsonSchema()
    {
        var (workspace, _) = await CreateAsync("acme", null);
        var collections = await CollectionsAsync(workspace.Id);

        foreach (var collection in collections.Values)
        {
            var description = await new DescribeCollection(Db, Tenant.Current!).HandleAsync(new DescribeCollectionRequest(collection.Id), Ct);

            Assert.True(description.IsSuccess, collection.Name);
            var schema = description.Value.JsonSchema;
            Assert.Equal("object", schema["type"]?.GetValue<string>());
            var properties = schema["properties"]!.AsObject();
            Assert.Equal(collection.Schema.Select(p => p.Key), properties.Select(p => p.Key));
            Assert.Single(description.Value.Templates);
        }
    }

    [Fact]
    public async Task Apply_Empty_CreatesNothingBesidesOwnerMembership()
    {
        var (workspace, _) = await CreateAsync("acme", "empty");

        Assert.Empty(await Db.Spaces.Where(s => s.WorkspaceId == workspace.Id).ToListAsync(Ct));
        Assert.Empty(await Db.Collections.Where(c => c.WorkspaceId == workspace.Id).ToListAsync(Ct));
        Assert.Empty(await Db.Templates.Where(t => t.WorkspaceId == workspace.Id).ToListAsync(Ct));
        Assert.Single(await Db.Memberships.Where(m => m.WorkspaceId == workspace.Id).ToListAsync(Ct));
    }

    [Fact]
    public async Task Apply_FailureInTemplate_RollsBackWorkspaceAndEverythingCreatedSoFar()
    {
        var caller = await SeedCallerAsync();
        // Fails on the third collection, after workspace, spaces, two collections and the schema update were written.
        var sut = WorkspaceTemplateHost.CreateWorkspace(Db, Tenant, caller, _clock, new FailingOnNthCollectionHook(3));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.HandleAsync(new CreateWorkspaceRequest("acme", "Acme"), Ct));

        Db.ChangeTracker.Clear();
        Tenant.Current = null;
        Assert.Null(Db.Database.CurrentTransaction);
        Assert.Empty(await Db.Workspaces.ToListAsync(Ct));
        using var instance = Instance.Enter("test: nothing of any workspace was stored");
        Assert.Empty(await Db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).ToListAsync(Ct));
        Assert.Empty(await Db.Spaces.IgnoreQueryFilters([TenantQueryFilter.Name]).ToListAsync(Ct));
        Assert.Empty(await Db.Collections.IgnoreQueryFilters([TenantQueryFilter.Name]).ToListAsync(Ct));
        Assert.Empty(await Db.ActivityEvents.IgnoreQueryFilters([TenantQueryFilter.Name]).ToListAsync(Ct));
    }

    [Fact]
    public async Task Apply_TwoWorkspacesInARow_CollectionsAreSeparated()
    {
        var caller = await SeedCallerAsync();
        var (first, _) = await CreateAsync("first", null, caller);
        Tenant.Current = null;
        var (second, _) = await CreateAsync("second", null, caller);

        var firstCollections = await CollectionsAsync(first.Id);
        var secondCollections = await CollectionsAsync(second.Id);
        Assert.Equal(4, firstCollections.Count);
        Assert.Equal(4, secondCollections.Count);
        Assert.Empty(firstCollections.Values.Select(c => c.Id).Intersect(secondCollections.Values.Select(c => c.Id)));

        // Relations stay inside their own workspace.
        var firstIds = firstCollections.Values.Select(c => c.Id).ToHashSet();
        Assert.All(
            secondCollections.Values.SelectMany(c => c.Schema).Where(p => p.TargetCollectionId is not null),
            p => Assert.DoesNotContain(p.TargetCollectionId!.Value, firstIds));
        var secondSpaceIds = (await Db.Spaces.AsNoTracking().Where(s => s.WorkspaceId == second.Id).ToListAsync(Ct)).Select(s => s.Id).ToHashSet();
        Assert.All(secondCollections.Values, c => Assert.Contains(c.SpaceId, secondSpaceIds));
    }

    [Fact]
    public async Task Apply_SoftwareProjekt_CreatesStandardViewsPerCollection()
    {
        var (workspace, _) = await CreateAsync("acme", null);
        var collections = await CollectionsAsync(workspace.Id);

        var views = await Db.Views.AsNoTracking().Where(v => v.WorkspaceId == workspace.Id).ToListAsync(Ct);
        List<Agcowork.Domain.Views.View> Own(string name) => [.. views.Where(v => v.CollectionId == collections[name].Id).OrderBy(v => v.SortOrder)];

        foreach (var name in collections.Keys)
        {
            var system = Own(name).Where(v => v.IsSystem).ToList();
            Assert.Equal([ViewType.Table, ViewType.Raw], system.Select(v => v.Type));
        }

        var tasks = Own("Aufgaben");
        Assert.Equal(3, tasks.Count);
        var board = Assert.Single(tasks, v => v.Type == ViewType.Board);
        Assert.Equal("Nach Status", board.Name);
        Assert.Equal("status", board.GroupByProperty);
        Assert.False(board.IsSystem);

        var decisionTable = Assert.Single(Own("Entscheidungen"), v => v.IsSystem && v.Type == ViewType.Table);
        var sort = Assert.Single(decisionTable.Sorts);
        Assert.Equal(("date", "desc"), (sort.Prop, sort.Dir));
        Assert.Equal(2, Own("Entscheidungen").Count);

        var analyses = Own("Analysen");
        Assert.Equal(3, analyses.Count);
        var gallery = Assert.Single(analyses, v => v.Type == ViewType.Gallery);
        Assert.Equal("Galerie", gallery.Name);
        Assert.Null(gallery.CardCoverProperty);
        Assert.Equal(2, Own("Komponenten").Count);
    }

    [Fact]
    public async Task Descriptions_QuotedOptions_ExistAsValuesOrKeysInTheSchema()
    {
        var (workspace, _) = await CreateAsync("acme", null);
        var collections = await CollectionsAsync(workspace.Id);

        foreach (var collection in collections.Values)
        {
            // Keys of any collection are fine (Komponenten points to `affects` of Entscheidungen); option values must exist in the own collection.
            var allowed = collections.Values.SelectMany(c => c.Schema).Select(p => p.Key)
                .Concat(collection.Schema.SelectMany(p => p.Options ?? []).Select(o => o.Value))
                .Append("schema")
                .ToHashSet();
            var texts = new[] { collection.Description }.Concat(collection.Schema.Select(p => p.Description ?? string.Empty));
            foreach (var text in texts)
            {
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, "`([^`]+)`"))
                {
                    var token = match.Groups[1].Value;
                    if (System.Text.RegularExpressions.Regex.IsMatch(token, "^[a-z_]+$"))
                    {
                        Assert.True(allowed.Contains(token), $"{collection.Name}: `{token}` is neither a property key nor an option value");
                    }
                }
            }
        }

        Assert.Contains("`superseded`", collections["Entscheidungen"].Description);
        Assert.Contains("`schema`", Property(collections["Aufgaben"].Schema, "sprint").Description);
    }

    [Fact]
    public async Task Apply_TemplateReturnsFailure_ThrowsAndRollsBackEverything()
    {
        var caller = await SeedCallerAsync();
        // The hook adds a second "Komponenten" to the same space: the save hits the unique name index and CreateCollection returns a conflict result.
        var sut = WorkspaceTemplateHost.CreateWorkspace(Db, Tenant, caller, _clock, new DuplicateFirstCollectionHook(Db, _clock));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.HandleAsync(new CreateWorkspaceRequest("acme", "Acme"), Ct));

        Assert.Contains("software-projekt", exception.Message);
        Assert.Contains("acme", exception.Message);
        Db.ChangeTracker.Clear();
        Tenant.Current = null;
        Assert.Null(Db.Database.CurrentTransaction);
        Assert.Empty(await Db.Workspaces.ToListAsync(Ct));
        using var instance = Instance.Enter("test: nothing of any workspace was stored");
        Assert.Empty(await Db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).ToListAsync(Ct));
        Assert.Empty(await Db.Spaces.IgnoreQueryFilters([TenantQueryFilter.Name]).ToListAsync(Ct));
        Assert.Empty(await Db.Collections.IgnoreQueryFilters([TenantQueryFilter.Name]).ToListAsync(Ct));
        Assert.Empty(await Db.ActivityEvents.IgnoreQueryFilters([TenantQueryFilter.Name]).ToListAsync(Ct));
    }

    private async Task<(Workspace Workspace, TestActor Caller)> CreateAsync(string slug, string? templateId, TestActor? caller = null)
    {
        caller ??= await SeedCallerAsync();
        var sut = WorkspaceTemplateHost.CreateWorkspace(Db, Tenant, caller, _clock);
        var result = await sut.HandleAsync(new CreateWorkspaceRequest(slug, slug, TemplateId: templateId), Ct);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Db.ChangeTracker.Clear();
        var workspace = await Db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == result.Value.Id, Ct);

        // Further reads run in the tenant of the new workspace, like a follow-up request would.
        var membership = await Db.Memberships.IgnoreQueryFilters([TenantQueryFilter.Name]).AsNoTracking()
            .SingleAsync(m => m.WorkspaceId == workspace.Id && m.ActorId == caller.Id, Ct);
        Tenant.Current = new TenantContext(workspace.Id, workspace.Slug, false, membership, EffectiveScopes.Compute(membership.Scopes, new HashSet<string>()));
        return (workspace, caller);
    }

    private async Task<Dictionary<string, Collection>> CollectionsAsync(Guid workspaceId)
    {
        using var instance = Instance.Enter("test: collections of any workspace");
        return (await Db.Collections.AsNoTracking().IgnoreQueryFilters([TenantQueryFilter.Name]).Where(c => c.WorkspaceId == workspaceId).ToListAsync(Ct))
            .ToDictionary(c => c.Name);
    }

    private static List<(string Key, PropertyType Type)> KeysAndTypes(Collection collection) => [.. collection.Schema.Select(p => (p.Key, p.Type))];

    private static PropertyDefinition Property(IReadOnlyList<PropertyDefinition> schema, string key) => Assert.Single(schema, p => p.Key == key);

    private async Task<TestActor> SeedCallerAsync()
    {
        var actor = Actor.CreateAgent("Creator", null, null, [InstanceScope.WorkspacesCreate], _clock);
        Db.Actors.Add(actor);
        await Db.SaveChangesAsync(Ct);
        return TestActor.Agent(actor.Id, InstanceScope.WorkspacesCreate);
    }

    private sealed class DuplicateFirstCollectionHook(Agcowork.Infrastructure.Persistence.AgcoworkDbContext db, TimeProvider clock) : ICollectionCreatedHook
    {
        private bool _done;

        public Task OnCollectionCreatedAsync(Collection collection, CancellationToken cancellationToken)
        {
            if (!_done)
            {
                _done = true;
                db.Collections.Add(Collection.Create(collection.WorkspaceId, collection.SpaceId, collection.Name, collection.Description, null, collection.CreatedBy, clock));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FailingOnNthCollectionHook(int failOn) : ICollectionCreatedHook
    {
        private int _count;

        public Task OnCollectionCreatedAsync(Collection collection, CancellationToken cancellationToken) =>
            ++_count == failOn ? throw new InvalidOperationException("simulated failure") : Task.CompletedTask;
    }
}
