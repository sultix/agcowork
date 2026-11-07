using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Schema;
using Agcowork.Application.Spaces;
using Agcowork.Application.Tenancy;
using Agcowork.Application.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Agcowork.Application.Workspaces.Templates;

/// <summary>
/// The default workspace template (architecture §2): spaces Produkt, Technik, Notizen and the collections
/// Aufgaben, Analysen (Produkt), Entscheidungen, Komponenten (Technik) with schema, agent-facing descriptions
/// and a default record template each. Everything is created through the use cases.
/// </summary>
public sealed class SoftwareProjectTemplate : IWorkspaceTemplate
{
    public const string TemplateId = "software-projekt";

    public string Id => TemplateId;

    public string Name => "Software-Projekt";

    public string Description =>
        "Standard-Struktur für Software-Arbeit: die Spaces Produkt, Technik und Notizen mit den Collections Aufgaben, Analysen, "
        + "Entscheidungen und Komponenten (inklusive Schema, Beschreibungen für Agenten und Record-Vorlagen).";

    public async Task<Result> ApplyAsync(IServiceProvider services, Guid workspaceId, Guid actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        var tenant = services.GetRequiredService<ITenantContext>();
        if (tenant.WorkspaceId != workspaceId || tenant.Membership.ActorId != actorId)
        {
            throw new InvalidOperationException("The template must run in the tenant context of the new workspace and its creator.");
        }

        var createSpace = services.GetRequiredService<CreateSpace>();
        var createCollection = services.GetRequiredService<CreateCollection>();
        var updateSchema = services.GetRequiredService<UpdateSchema>();
        var createTemplate = services.GetRequiredService<CreateTemplate>();

        // Spaces.
        var product = await createSpace.HandleAsync(new CreateSpaceRequest(
            "Produkt",
            "Was gebaut werden soll und warum: Aufgaben und Analysen. Lege hier alles ab, was Anforderungen, Planung und Recherche betrifft.",
            "🎯",
            0), cancellationToken).ConfigureAwait(false);
        if (product.IsFailure)
        {
            return product.Error;
        }

        var tech = await createSpace.HandleAsync(new CreateSpaceRequest(
            "Technik",
            "Wie es gebaut wird: Entscheidungen und Komponenten. Lege hier Architektur, Technologiewahl und Systembausteine ab.",
            "🛠️",
            1), cancellationToken).ConfigureAwait(false);
        if (tech.IsFailure)
        {
            return tech.Error;
        }

        var notes = await createSpace.HandleAsync(new CreateSpaceRequest(
            "Notizen",
            "Freie Notizen ohne feste Struktur: Meeting-Protokolle, Ideen, Entwürfe. Collections kannst du hier selbst anlegen, wenn sich ein Muster zeigt.",
            "📝",
            2), cancellationToken).ConfigureAwait(false);
        if (notes.IsFailure)
        {
            return notes.Error;
        }

        // Order matters for relations: Komponenten -> Entscheidungen (without supersedes) -> supersedes -> Aufgaben -> Analysen.
        var components = await createCollection.HandleAsync(new CreateCollectionRequest(tech.Value.Id, "Komponenten",
            "Die Bausteine des Systems: Services, Bibliotheken, Datenbanken, UI-Teile und Infrastruktur. Lege hier eine Komponente an, sobald sie geplant "
            + "oder gebaut wird, und pflege den Status. Entscheidungen verweisen über `affects` auf die Komponenten, die sie betreffen.",
            ComponentSchema()), cancellationToken).ConfigureAwait(false);
        if (components.IsFailure)
        {
            return components.Error;
        }

        var decisions = await createCollection.HandleAsync(new CreateCollectionRequest(tech.Value.Id, "Entscheidungen",
            "Architektur- und Produktentscheidungen (ADR). Lege hier jede Entscheidung ab, bevor du sie umsetzt, mit Kontext, Optionen und Konsequenzen. "
            + "Verweise unter `affects` auf betroffene Komponenten. Ersetzt eine Entscheidung eine ältere, setze `supersedes` und setze den `status` der alten auf `superseded` (Ersetzt).",
            DecisionSchema(components.Value.Id)), cancellationToken).ConfigureAwait(false);
        if (decisions.IsFailure)
        {
            return decisions.Error;
        }

        // A relation to the collection itself needs the collection's id, so it is added in a second step.
        var supersedes = await updateSchema.HandleAsync(new UpdateSchemaRequest(decisions.Value.Id, Add:
        [
            new PropertyDefinitionInput(
                "supersedes",
                "Ersetzt",
                "relation",
                "Ältere Entscheidungen, die diese Entscheidung ersetzt. Setze den `status` der älteren Entscheidungen danach auf `superseded` (Ersetzt).",
                TargetCollectionId: decisions.Value.Id,
                Multiple: true),
        ]), cancellationToken).ConfigureAwait(false);
        if (supersedes.IsFailure)
        {
            return supersedes.Error;
        }

        var tasks = await createCollection.HandleAsync(new CreateCollectionRequest(product.Value.Id, "Aufgaben",
            "Arbeitspakete und To-dos. Lege hier eine Aufgabe an, sobald klar ist, was zu tun ist, mit Ziel und Akzeptanzkriterien im Text. "
            + "Setze `status` laufend aktuell und verweise unter `related_decision` auf die Entscheidung, aus der die Aufgabe folgt.",
            TaskSchema(decisions.Value.Id)), cancellationToken).ConfigureAwait(false);
        if (tasks.IsFailure)
        {
            return tasks.Error;
        }

        var analyses = await createCollection.HandleAsync(new CreateCollectionRequest(product.Value.Id, "Analysen",
            "Recherchen und Untersuchungen zu Markt, Technik, Wettbewerb oder Nutzern. Lege hier eine Analyse an, wenn du eine Frage untersucht hast, "
            + "und halte Befunde, Quellen und eine Empfehlung fest. Die `summary` fasst das Ergebnis in wenigen Sätzen zusammen.",
            AnalysisSchema()), cancellationToken).ConfigureAwait(false);
        if (analyses.IsFailure)
        {
            return analyses.Error;
        }

        // Additional views (architecture §2); the standard table and raw views come from the collection-created hook.
        var createView = services.GetRequiredService<CreateView>();
        var board = await createView.HandleAsync(new CreateViewRequest(tasks.Value.Id, "Nach Status", "board", GroupBy: "status"), cancellationToken).ConfigureAwait(false);
        if (board.IsFailure)
        {
            return board.Error;
        }

        var gallery = await createView.HandleAsync(new CreateViewRequest(analyses.Value.Id, "Galerie", "gallery"), cancellationToken).ConfigureAwait(false);
        if (gallery.IsFailure)
        {
            return gallery.Error;
        }

        var decisionViews = await services.GetRequiredService<ListViews>().HandleAsync(new ListViewsRequest(decisions.Value.Id), cancellationToken).ConfigureAwait(false);
        if (decisionViews.IsFailure)
        {
            return decisionViews.Error;
        }

        var decisionTable = decisionViews.Value.First(v => v.IsSystem && v.Type == "table");
        var sorted = await services.GetRequiredService<UpdateView>().HandleAsync(
            new UpdateViewRequest(decisionTable.Id, Sorts: [new ViewSortInput("date", "desc")]), cancellationToken).ConfigureAwait(false);
        if (sorted.IsFailure)
        {
            return sorted.Error;
        }

        // Default record templates.
        (Guid CollectionId, string Body)[] bodies =
        [
            (decisions.Value.Id, "## Kontext\n\n## Optionen\n\n## Entscheidung\n\n## Konsequenzen\n"),
            (tasks.Value.Id, "## Ziel\n\n## Akzeptanzkriterien\n"),
            (analyses.Value.Id, "## Fragestellung\n\n## Befunde\n\n## Empfehlung\n"),
            (components.Value.Id, "## Zweck\n\n## Schnittstellen\n"),
        ];
        foreach (var (collectionId, body) in bodies)
        {
            var template = await createTemplate.HandleAsync(new CreateTemplateRequest(
                collectionId, "Standard", "Gliederung für neue Einträge dieser Collection.", BodyMarkdown: body, IsDefault: true), cancellationToken).ConfigureAwait(false);
            if (template.IsFailure)
            {
                return template.Error;
            }
        }

        return Result.Success();
    }

    private static PropertyDefinitionInput[] ComponentSchema() =>
    [
        Select("kind", "Art", "Art der Komponente.", ("service", "Service", "blue"), ("library", "Bibliothek", "purple"), ("database", "Datenbank", "orange"),
            ("ui", "UI", "pink"), ("infrastructure", "Infrastruktur", "gray")),
        new("owner", "Verantwortlich", "actor", "Wer die Komponente verantwortet (Mensch oder Agent)."),
        new("repo_url", "Repository", "url", "Link zum Quellcode-Repository der Komponente."),
        Status("status", "Status", "Lebenszyklus der Komponente.", ("planned", "Geplant", "gray", "todo"), ("active", "Aktiv", "green", "in_progress"), ("deprecated", "Veraltet", "red", "done")),
    ];

    private static PropertyDefinitionInput[] DecisionSchema(Guid componentsId) =>
    [
        Status("status", "Status", "Stand der Entscheidung.", ("proposed", "Vorgeschlagen", "yellow", "todo"), ("accepted", "Akzeptiert", "green", "done"),
            ("rejected", "Verworfen", "red", "done"), ("superseded", "Ersetzt", "gray", "done")),
        new("decided_by", "Entschieden von", "actor", "Wer die Entscheidung getroffen hat."),
        new("date", "Datum", "date", "Tag der Entscheidung."),
        new("affects", "Betrifft", "relation", "Komponenten, die von dieser Entscheidung betroffen sind.", TargetCollectionId: componentsId, Multiple: true),
    ];

    private static PropertyDefinitionInput[] TaskSchema(Guid decisionsId) =>
    [
        Status("status", "Status", "Bearbeitungsstand der Aufgabe.", ("open", "Offen", "gray", "todo"), ("in_progress", "In Arbeit", "blue", "in_progress"),
            ("done", "Erledigt", "green", "done"), ("dropped", "Verworfen", "red", "done")),
        new("owner", "Verantwortlich", "actor", "Wer die Aufgabe bearbeitet (Mensch oder Agent)."),
        Select("priority", "Priorität", "Wie dringend die Aufgabe ist.", ("low", "Niedrig", "gray"), ("medium", "Mittel", "yellow"), ("high", "Hoch", "orange"), ("critical", "Kritisch", "red")),
        new("sprint", "Sprint", "select", "Sprint oder Iteration, in der die Aufgabe bearbeitet wird. Neue Sprints als Option per Schemaänderung (`PATCH …/schema`, Scope `schema`) anlegen.", Options: []),
        new("related_decision", "Zugehörige Entscheidung", "relation", "Die Entscheidung, aus der diese Aufgabe folgt.", TargetCollectionId: decisionsId),
        new("due", "Fällig", "date", "Fälligkeitsdatum."),
        new("estimate", "Schätzung", "number", "Geschätzter Aufwand in Stunden.", Unit: "h"),
    ];

    private static PropertyDefinitionInput[] AnalysisSchema() =>
    [
        Select("kind", "Art", "Worum es in der Analyse geht.", ("market", "Markt", "blue"), ("technology", "Technik", "purple"), ("competition", "Wettbewerb", "orange"), ("users", "Nutzer", "green")),
        new("author", "Autor", "actor", "Wer die Analyse erstellt hat (Mensch oder Agent)."),
        new("sources", "Quellen", "markdown", "Quellen und Belege als Markdown-Liste mit Links."),
        Select("confidence", "Belastbarkeit", "Wie belastbar die Befunde sind.", ("low", "Niedrig", "red"), ("medium", "Mittel", "yellow"), ("high", "Hoch", "green")),
        new("summary", "Zusammenfassung", "text", "Das Ergebnis in zwei bis drei Sätzen.", Required: true),
    ];

    private static PropertyDefinitionInput Select(string key, string name, string description, params (string Value, string Label, string Color)[] options) =>
        new(key, name, "select", description, Options: [.. options.Select(o => new PropertyOptionInput(o.Value, o.Label, o.Color))]);

    private static PropertyDefinitionInput Status(string key, string name, string description, params (string Value, string Label, string Color, string Group)[] options) =>
        new(key, name, "status", description, Options: [.. options.Select(o => new PropertyOptionInput(o.Value, o.Label, o.Color, o.Group))]);
}
