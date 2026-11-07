using Agcowork.Application.Common.Results;
using Agcowork.Application.Schema;
using Agcowork.Domain.Collections.Schema;
using static Agcowork.Application.Tests.Schema.SchemaFixtures;

namespace Agcowork.Application.Tests.Schema;

public sealed class PropertyValueValidatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<IReadOnlyList<FieldError>> ValidateAsync(string json, PropertyValidationMode mode = PropertyValidationMode.Partial, string? prefix = null) =>
        Validator().ValidateAsync(AllTypes, Props(json), mode, prefix, Ct);

    private static async Task<FieldError> SingleErrorAsync(string json, string field)
    {
        var errors = await ValidateAsync(json);
        return Assert.Single(errors, e => e.Field == field);
    }

    [Fact]
    public async Task ValidateAsync_ValidValueForEveryType_NoErrors()
    {
        var errors = await ValidateAsync($$"""
            {
              "title": "Use PostgreSQL",
              "notes": "## Context\n- a",
              "estimate": 2.5,
              "count": 3,
              "done": true,
              "priority": "high",
              "tags": ["a", "b"],
              "status": "doing",
              "due": "2026-11-07",
              "at": "2026-11-07T14:30:00+01:00",
              "link": "https://example.com/a?b=c",
              "mail": "someone@example.com",
              "affects": "{{ExistingRecord}}",
              "related": ["{{ExistingRecord}}"],
              "owner": "{{Member}}",
              "payload": { "nested": [1, 2, { "x": null }] }
            }
            """, PropertyValidationMode.Full);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task ValidateAsync_UnknownKey_UnknownPropertyWithWritableKeys()
    {
        var error = await SingleErrorAsync("""{ "prio": "high" }""", "prio");

        Assert.Equal(ErrorCodes.UnknownProperty, error.Code);
        Assert.Contains("priority", error.Allowed!);
        Assert.Contains("title", error.Allowed!);
        Assert.DoesNotContain("created", error.Allowed!);
    }

    [Fact]
    public async Task ValidateAsync_FullModeMissingRequired_Required()
    {
        var errors = await ValidateAsync("""{ "title": "x" }""", PropertyValidationMode.Full);

        var error = Assert.Single(errors);
        Assert.Equal("status", error.Field);
        Assert.Equal(ErrorCodes.Required, error.Code);
    }

    [Fact]
    public async Task ValidateAsync_PartialModeMissingRequired_NoError_ButNullRequiredFails()
    {
        Assert.Empty(await ValidateAsync("""{ "priority": null }"""));
        Assert.Equal(ErrorCodes.Required, (await SingleErrorAsync("""{ "status": null }""", "status")).Code);
    }

    [Fact]
    public async Task ValidateAsync_Prefix_AppliedToFields()
    {
        var errors = await ValidateAsync("""{ "nope": 1 }""", prefix: "propertiesPreset");

        Assert.Equal("propertiesPreset.nope", Assert.Single(errors).Field);
    }

    [Theory]
    [InlineData("""{ "title": 5 }""", "title", "invalid_type")]
    [InlineData("""{ "notes": ["x"] }""", "notes", "invalid_type")]
    [InlineData("""{ "estimate": "2" }""", "estimate", "invalid_type")]
    [InlineData("""{ "estimate": 2.55 }""", "estimate", "invalid_format")]
    [InlineData("""{ "count": 1.5 }""", "count", "invalid_format")]
    [InlineData("""{ "done": "yes" }""", "done", "invalid_type")]
    [InlineData("""{ "priority": 1 }""", "priority", "invalid_type")]
    [InlineData("""{ "tags": "a" }""", "tags", "invalid_type")]
    [InlineData("""{ "tags": ["a", 1] }""", "tags[1]", "invalid_type")]
    [InlineData("""{ "tags": ["a", "a"] }""", "tags[1]", "duplicate")]
    [InlineData("""{ "due": "07.11.2026" }""", "due", "invalid_format")]
    [InlineData("""{ "due": "2026-02-30" }""", "due", "invalid_format")]
    [InlineData("""{ "at": "2026-11-07T14:30:00" }""", "at", "invalid_format")]
    [InlineData("""{ "at": "2026-11-07" }""", "at", "invalid_format")]
    [InlineData("""{ "link": "example.com" }""", "link", "invalid_format")]
    [InlineData("""{ "link": "ftp://example.com" }""", "link", "invalid_format")]
    [InlineData("""{ "link": "javascript:alert(1)" }""", "link", "invalid_format")]
    [InlineData("""{ "mail": "no-at-sign" }""", "mail", "invalid_format")]
    [InlineData("""{ "affects": "not-a-uuid" }""", "affects", "invalid_format")]
    [InlineData("""{ "affects": ["0190f5e2-7a4c-7000-8000-0000000000b1"] }""", "affects", "invalid_type")]
    [InlineData("""{ "related": "0190f5e2-7a4c-7000-8000-0000000000b1" }""", "related", "invalid_type")]
    [InlineData("""{ "owner": 42 }""", "owner", "invalid_type")]
    [InlineData("""{ "created": "2026-11-07T00:00:00Z" }""", "created", "read_only")]
    public async Task ValidateAsync_InvalidValue_ExpectedCode(string json, string field, string code)
    {
        var error = await SingleErrorAsync(json, field);

        Assert.Equal(code, error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task ValidateAsync_SelectNotInOptions_NotInOptionsWithAllowed()
    {
        var error = await SingleErrorAsync("""{ "priority": "High" }""", "priority");

        Assert.Equal(ErrorCodes.NotInOptions, error.Code);
        Assert.Equal(["high", "low"], error.Allowed);
    }

    [Fact]
    public async Task ValidateAsync_StatusNotInOptions_NotInOptionsWithAllowed()
    {
        var error = await SingleErrorAsync("""{ "status": "done" }""", "status");

        Assert.Equal(["open", "doing", "closed"], error.Allowed);
    }

    [Fact]
    public async Task ValidateAsync_MultiSelectItemNotInOptions_IndexedField()
    {
        var error = await SingleErrorAsync("""{ "tags": ["a", "c"] }""", "tags[1]");

        Assert.Equal(ErrorCodes.NotInOptions, error.Code);
        Assert.Equal(["a", "b"], error.Allowed);
    }

    [Fact]
    public async Task ValidateAsync_TextTooLong_TooLong()
    {
        var error = await SingleErrorAsync($$"""{ "title": "{{new string('x', PropertyTypeRegistry.TextMaxLength + 1)}}" }""", "title");

        Assert.Equal(ErrorCodes.TooLong, error.Code);
    }

    [Fact]
    public async Task ValidateAsync_UnknownRecordOrNonMember_NotFound()
    {
        var unknown = Guid.CreateVersion7();
        var errors = await ValidateAsync($$"""{ "affects": "{{unknown}}", "related": ["{{ExistingRecord}}", "{{unknown}}"], "owner": "{{unknown}}" }""");

        Assert.Equal(["affects", "owner", "related[1]"], errors.Select(e => e.Field).Order(StringComparer.Ordinal));
        Assert.All(errors, e => Assert.Equal(ErrorCodes.NotFound, e.Code));
    }

    [Fact]
    public async Task ValidateAsync_InactiveMember_ActorInactive()
    {
        var error = await SingleErrorAsync($$"""{ "owner": "{{InactiveMember}}" }""", "owner");

        Assert.Equal(ErrorCodes.ActorInactive, error.Code);
    }

    private static readonly IReadOnlyList<PropertyDefinition> RequiredOfEachShape =
    [
        PropertyDefinition.Title(),
        new() { Key = "notes", Name = "Notes", Type = PropertyType.Markdown, Required = true },
        new() { Key = "priority", Name = "Priority", Type = PropertyType.Select, Required = true, Options = [new("high", "High")] },
        new() { Key = "tags", Name = "Tags", Type = PropertyType.MultiSelect, Required = true, Options = [new("a", "A")] },
        new() { Key = "due", Name = "Due", Type = PropertyType.Date, Required = true },
        new() { Key = "link", Name = "Link", Type = PropertyType.Url, Required = true },
        new() { Key = "mail", Name = "Mail", Type = PropertyType.Email, Required = true },
        new() { Key = "related", Name = "Related", Type = PropertyType.Relation, Required = true, Multiple = true, TargetCollectionId = TargetCollection },
        new() { Key = "payload", Name = "Payload", Type = PropertyType.Json, Required = true },
    ];

    [Theory]
    [InlineData("title", "\"\"")]
    [InlineData("title", "\"   \"")]
    [InlineData("notes", "\" \\n \"")]
    [InlineData("priority", "\"\"")]
    [InlineData("tags", "[]")]
    [InlineData("due", "\" \"")]
    [InlineData("link", "\"\"")]
    [InlineData("mail", "\"\"")]
    [InlineData("related", "[]")]
    [InlineData("payload", "[]")]
    public async Task ValidateAsync_RequiredButEmpty_Required(string key, string emptyJson)
    {
        var errors = await Validator().ValidateAsync(RequiredOfEachShape, Props($$"""{ "{{key}}": {{emptyJson}} }"""), PropertyValidationMode.Partial, null, Ct);

        var error = Assert.Single(errors);
        Assert.Equal((key, ErrorCodes.Required), (error.Field, error.Code));
    }

    [Fact]
    public async Task ValidateAsync_OptionalEmpty_Accepted()
    {
        Assert.Empty(await ValidateAsync("""{ "notes": "", "tags": [], "related": [] }"""));
    }

    [Fact]
    public async Task ValidateAsync_NullOptional_Accepted()
    {
        Assert.Empty(await ValidateAsync("""{ "priority": null, "affects": null, "payload": null }"""));
    }

    [Fact]
    public async Task ValidateAsync_NoReferences_CheckersNotNeeded()
    {
        // Values without relation/actor never reach the checkers; the default registration rejects every record id.
        Assert.Empty(await new PropertyValueValidator(new ThrowingRelations(), new ThrowingMembers())
            .ValidateAsync(AllTypes, Props("""{ "title": "x" }"""), PropertyValidationMode.Partial, null, Ct));
    }

    private sealed class ThrowingRelations : IRelationTargetChecker
    {
        public Task<IReadOnlySet<Guid>> FindExistingAsync(Guid targetCollectionId, IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not expected");
    }

    private sealed class ThrowingMembers : IActorMembershipChecker
    {
        public Task<IReadOnlyDictionary<Guid, bool>> FindMembersAsync(IReadOnlyCollection<Guid> actorIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not expected");
    }
}
