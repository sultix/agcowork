using System.Text.Json;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Query;
using Agcowork.Application.Views;
using Agcowork.Domain.Views;
using Microsoft.Extensions.Time.Testing;

namespace Agcowork.Application.Tests.Query;

public sealed class ExecuteViewTests(PostgresFixture postgres) : QueryTestBase(postgres)
{
    private async Task<View> AddViewAsync(Seed seed, ViewFilterNode? filter, IEnumerable<ViewSort>? sorts = null, IEnumerable<string>? visible = null)
    {
        var view = View.Create(seed.A.Seed.Alpha.Id, seed.CollectionId, $"View {Guid.NewGuid():N}", ViewType.Table, filter, sorts, null, visible, null,
            isDefault: false, isSystem: false, sortOrder: 9, seed.A.Seed.AlphaOwner.Id, new FakeTimeProvider(Clock.GetUtcNow()));
        Db.Views.Add(view);
        await Db.SaveChangesAsync(Ct);
        Db.ChangeTracker.Clear();
        return view;
    }

    private ExecuteView UseCase(Seed seed) => new(Db, seed.A.Tenant, Schemas(), Translator);

    [Fact]
    public async Task ExecuteView_FilterSortsVisibleProperties_PlusAdditionalFilterByAnd()
    {
        var seed = await SeedAsync();
        var view = await AddViewAsync(seed, Filter("""{ "prop": "priority", "op": "eq", "value": "high" }"""), [new ViewSort("estimate", "desc")], ["estimate", "status"]);

        var all = await UseCase(seed).HandleAsync(new ExecuteViewRequest(view.Id, Limit: 500), Ct);
        var narrowed = await UseCase(seed).HandleAsync(new ExecuteViewRequest(view.Id, Filter("""{ "prop": "status", "op": "eq", "value": "done" }"""), Limit: 500), Ct);

        Assert.True(all.IsSuccess, Describe(all.Error));
        var expected = seed.Active.Where(r => r.Str("priority") == "high").ToList();
        Assert.Equal(expected.Select(r => r.Id).Order(), all.Value.Items.Select(r => r.Id).Order());
        var estimates = all.Value.Items.Select(r => r.Properties.TryGetValue("estimate", out var e) ? (decimal?)e.GetDecimal() : null).ToList();
        Assert.Equal(estimates.Where(e => e is not null).OrderDescending().Concat(estimates.Where(e => e is null)), estimates);
        Assert.All(all.Value.Items, r => Assert.Empty(r.Properties.Keys.Except(["estimate", "status"])));
        Assert.Equal(
            expected.Where(r => r.Str("status") == "done").Select(r => r.Id).Order(),
            narrowed.Value.Items.Select(r => r.Id).Order());
        Assert.NotEmpty(narrowed.Value.Items);
    }

    [Fact]
    public async Task ExecuteView_CursorAcrossPages()
    {
        var seed = await SeedAsync();
        var view = await AddViewAsync(seed, null, [new ViewSort("status", "asc")]);
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await UseCase(seed).HandleAsync(new ExecuteViewRequest(view.Id, Cursor: cursor, Limit: 10), Ct);
            Assert.True(page.IsSuccess, Describe(page.Error));
            seen.AddRange(page.Value.Items.Select(r => r.Id));
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(seed.Active.Count(), seen.Distinct().Count());
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task ExecuteView_ErrorsNameViewOrRequest()
    {
        var seed = await SeedAsync();
        // Stored before AP-009's matrix existed: only structurally valid.
        var view = await AddViewAsync(seed, new ViewFilterNode { Prop = "due", Op = "contains", Value = JsonSerializer.SerializeToElement("2026") });

        var result = await UseCase(seed).HandleAsync(new ExecuteViewRequest(view.Id, Filter("""{ "prop": "nope", "op": "eq", "value": 1 }"""), Limit: 0), Ct);

        var error = Invalid(result);
        Assert.Equal(
            [("view.filter.op", ErrorCodes.OperatorNotApplicable), ("filter.prop", ErrorCodes.UnknownProperty), ("limit", ErrorCodes.OutOfRange)],
            error.Errors.Select(e => (e.Field, e.Code)));
    }

    [Fact]
    public async Task ExecuteView_ViewAndAdditionalFilterTogether_AtMost150Conditions()
    {
        var seed = await SeedAsync();
        string Many(int n) => $$"""{ "or": [ {{string.Join(", ", Enumerable.Repeat("""{ "prop": "done", "op": "is_empty" }""", n))}} ] }""";
        var view = await AddViewAsync(seed, Filter(Many(QueryParser.MaxConditions)));

        var fits = await UseCase(seed).HandleAsync(new ExecuteViewRequest(view.Id, Filter(Many(QueryParser.MaxCombinedConditions - QueryParser.MaxConditions))), Ct);
        var tooMany = await UseCase(seed).HandleAsync(new ExecuteViewRequest(view.Id, Filter(Many(QueryParser.MaxCombinedConditions - QueryParser.MaxConditions + 1))), Ct);

        Assert.True(fits.IsSuccess, Describe(fits.Error));
        Assert.Equal(("filter", ErrorCodes.TooLong), (Field(Invalid(tooMany), "filter").Field, Field(Invalid(tooMany), "filter").Code));
    }

    [Fact]
    public async Task ExecuteView_UnknownMembers_UnknownPropertyWithAllowed()
    {
        var seed = await SeedAsync();
        var view = await AddViewAsync(seed, null);

        var result = await UseCase(seed).HandleAsync(new ExecuteViewRequest(view.Id, UnknownMembers: ["sorts"]), Ct);

        var error = Field(Invalid(result), "sorts");
        Assert.Equal(ErrorCodes.UnknownProperty, error.Code);
        Assert.Equal(ExecuteViewRequest.Keys, error.Allowed);
    }

    [Fact]
    public async Task ExecuteView_UnknownOrForeignView_NotFound()
    {
        var seed = await SeedAsync();
        var view = await AddViewAsync(seed, null);
        var beta = Enter(seed.A.Seed.Beta, seed.A.Seed.BetaOwner);

        var missing = await UseCase(seed).HandleAsync(new ExecuteViewRequest(Guid.CreateVersion7()), Ct);
        Db.ChangeTracker.Clear();
        var foreign = await new ExecuteView(Db, beta, Schemas(), Translator).HandleAsync(new ExecuteViewRequest(view.Id), Ct);

        Assert.IsType<NotFoundError>(missing.Error);
        Assert.IsType<NotFoundError>(foreign.Error);
    }
}
