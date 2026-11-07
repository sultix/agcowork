using System.Text.Json;
using Agcowork.Application.Schema;
using Agcowork.Domain.Collections.Schema;

namespace Agcowork.Application.Tests.Schema;

/// <summary>A schema with one property per type, and fakes for the reference checkers.</summary>
internal static class SchemaFixtures
{
    public static readonly Guid TargetCollection = Guid.Parse("0190f5e2-7a4c-7000-8000-0000000000aa");
    public static readonly Guid ExistingRecord = Guid.Parse("0190f5e2-7a4c-7000-8000-0000000000b1");
    public static readonly Guid Member = Guid.Parse("0190f5e2-7a4c-7000-8000-0000000000c1");
    public static readonly Guid InactiveMember = Guid.Parse("0190f5e2-7a4c-7000-8000-0000000000c2");

    public static IReadOnlyList<PropertyDefinition> AllTypes { get; } =
    [
        PropertyDefinition.Title(),
        new() { Key = "notes", Name = "Notes", Type = PropertyType.Markdown },
        new() { Key = "estimate", Name = "Estimate", Type = PropertyType.Number, Precision = 1, Unit = "h" },
        new() { Key = "count", Name = "Count", Type = PropertyType.Number, Precision = 0 },
        new() { Key = "done", Name = "Done", Type = PropertyType.Checkbox },
        new() { Key = "priority", Name = "Priority", Type = PropertyType.Select, Description = "How urgent.", Options = [new("high", "High", "red"), new("low", "Low")] },
        new() { Key = "tags", Name = "Tags", Type = PropertyType.MultiSelect, Options = [new("a", "A"), new("b", "B")] },
        new()
        {
            Key = "status", Name = "Status", Type = PropertyType.Status, Required = true,
            Options = [new("open", "Open", Group: StatusGroup.Todo), new("doing", "Doing", Group: StatusGroup.InProgress), new("closed", "Closed", Group: StatusGroup.Done)],
        },
        new() { Key = "due", Name = "Due", Type = PropertyType.Date },
        new() { Key = "at", Name = "At", Type = PropertyType.DateTime },
        new() { Key = "link", Name = "Link", Type = PropertyType.Url },
        new() { Key = "mail", Name = "Mail", Type = PropertyType.Email },
        new() { Key = "affects", Name = "Affects", Type = PropertyType.Relation, TargetCollectionId = TargetCollection },
        new() { Key = "related", Name = "Related", Type = PropertyType.Relation, TargetCollectionId = TargetCollection, Multiple = true },
        new() { Key = "owner", Name = "Owner", Type = PropertyType.Actor },
        new() { Key = "payload", Name = "Payload", Type = PropertyType.Json },
        new() { Key = "created", Name = "Created", Type = PropertyType.CreatedAt },
    ];

    public static PropertyValueValidator Validator() => new(new FakeRelations(), new FakeMembers());

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Properties from a JSON object literal.</summary>
    public static Dictionary<string, JsonElement> Props(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private sealed class FakeRelations : IRelationTargetChecker
    {
        public Task<IReadOnlySet<Guid>> FindExistingAsync(Guid targetCollectionId, IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<Guid>>(targetCollectionId == TargetCollection ? recordIds.Where(id => id == ExistingRecord).ToHashSet() : []);
    }

    private sealed class FakeMembers : IActorMembershipChecker
    {
        public Task<IReadOnlyDictionary<Guid, bool>> FindMembersAsync(IReadOnlyCollection<Guid> actorIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, bool>>(actorIds.Where(id => id == Member || id == InactiveMember).ToDictionary(id => id, id => id == Member));
    }
}
