using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Application.Schema;

namespace Agcowork.Application.Tests.Records;

public sealed class ComputedPropertiesTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    private async Task<(Arranged A, Guid CollectionId)> ArrangeComputedAsync()
    {
        var a = await ArrangeAsync();
        var collection = await CreateCollectionAsync(a.Tenant, a.Seed.AlphaOwner, a.SpaceId, "Protokoll",
            new PropertyDefinitionInput("summary", "Summary", "text"),
            new PropertyDefinitionInput("created", "Created", "created_at"),
            new PropertyDefinitionInput("author", "Author", "created_by"),
            new PropertyDefinitionInput("updated_at", "Updated", "updated_at"),
            new PropertyDefinitionInput("editor", "Editor", "updated_by"));
        return (a, collection.Id);
    }

    [Fact]
    public async Task Dto_FillsComputedFromColumns_InSchemaOrder()
    {
        var (a, collectionId) = await ArrangeComputedAsync();
        var created = await TryCreateAsync(a, new CreateRecordRequest(collectionId, Title: "Eintrag", Properties: Props("""{ "summary": "kurz" }""")));
        Clock.Advance(TimeSpan.FromMinutes(5));
        Db.ChangeTracker.Clear();
        var updated = await UpdateUseCase(Enter(a.Seed.Alpha, a.Seed.MemberOfBoth), a.Seed.MemberOfBoth)
            .HandleAsync(new UpdateRecordRequest(created.Value.Id, Properties: Props("""{ "summary": "länger" }""")), Ct);

        var record = (await new GetRecord(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordRequest(created.Value.Id), Ct)).Value;

        Assert.True(updated.IsSuccess, Describe(updated.Error));
        Assert.Equal(["summary", "created", "author", "updated_at", "editor"], record.Properties.Keys);
        Assert.Equal("2026-11-07T12:00:00Z", record.Properties["created"].GetString());
        Assert.Equal(a.Seed.AlphaOwner.Id, record.Properties["author"].GetGuid());
        Assert.Equal("2026-11-07T12:05:00Z", record.Properties["updated_at"].GetString());
        Assert.Equal(a.Seed.MemberOfBoth.Id, record.Properties["editor"].GetGuid());
        Assert.Equal(record.Properties.Keys, updated.Value.Properties.Keys);
    }

    [Fact]
    public async Task Write_ComputedKey_ReadOnly_EvenNull()
    {
        var (a, collectionId) = await ArrangeComputedAsync();
        var created = await TryCreateAsync(a, new CreateRecordRequest(collectionId, Title: "Eintrag"));

        var onCreate = await TryCreateAsync(a, new CreateRecordRequest(collectionId, Title: "X", Properties: Props("""{ "created": "2020-01-01T00:00:00Z" }""")));
        var onPatch = await UpdateAsync(a, new UpdateRecordRequest(created.Value.Id, Properties: Props($$"""{ "author": "{{a.Seed.MemberOfBoth.Id}}", "editor": null }""")));

        Assert.Equal(ErrorCodes.ReadOnly, Field(Invalid(onCreate), "properties.created").Code);
        var error = Invalid(onPatch);
        Assert.Equal(ErrorCodes.ReadOnly, Field(error, "properties.author").Code);
        Assert.Equal(ErrorCodes.ReadOnly, Field(error, "properties.editor").Code);
    }

    [Fact]
    public async Task Markdown_ComputedUnderSchemaKey_NoDuplicateOfFixedField()
    {
        var (a, collectionId) = await ArrangeComputedAsync();
        var created = await TryCreateAsync(a, new CreateRecordRequest(collectionId, Title: "Eintrag"));

        var markdown = (await new GetRecordMarkdown(Db, a.Tenant, RecordTranslator).HandleAsync(new GetRecordMarkdownRequest(created.Value.Id), Ct)).Value;

        Assert.Contains("\ncreated: 2026-11-07T12:00:00Z\n", markdown, StringComparison.Ordinal);
        Assert.Contains($"\nauthor: {a.Seed.AlphaOwner.Id}\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("properties.updated_at", markdown, StringComparison.Ordinal);
        Assert.Single(markdown.Split('\n'), line => line.StartsWith("updated_at: ", StringComparison.Ordinal));
    }
}
