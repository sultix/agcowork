using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Domain.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Agcowork.Application.Tests.Records;

public sealed class IdempotencyTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    [Fact]
    public async Task Create_SameKeyTwice_OneRecord_SameResponse()
    {
        var a = await ArrangeAsync();
        var request = new CreateRecordRequest(a.Collection.Id, Title: "Einmal", Properties: Props("""{ "status": "doing", "tags": ["db"] }"""), Body: "Text", IdempotencyKey: "key-1");

        var first = await TryCreateAsync(a, request);
        var second = await TryCreateAsync(a, request);

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value, second.Value with { Properties = first.Value.Properties });
        Assert.Equal(first.Value.Properties.Keys, second.Value.Properties.Keys);
        Assert.Equal(1, await Db.Records.CountAsync(r => r.CollectionId == a.Collection.Id, Ct));
        Assert.Equal(1, await Db.IdempotencyKeys.CountAsync(Ct));
        Assert.Equal(IdempotencyFingerprint.Of(request), (await Db.IdempotencyKeys.SingleAsync(Ct)).RequestHash);
    }

    [Fact]
    public async Task Create_SameKeySameMeaning_PropertyOrderAndFormatting_Replay()
    {
        var a = await ArrangeAsync();
        var first = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Einmal", Properties: Props("""{ "status": "doing", "tags": ["db", "api"] }"""), IdempotencyKey: "k"));

        var reordered = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Einmal", Properties: Props("""{"tags":["db","api"],"status":"doing"}"""), IdempotencyKey: "k"));
        var noPropertiesVsEmpty = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Leer", IdempotencyKey: "k-empty"));
        var emptyProperties = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Leer", Properties: Props("{}"), IdempotencyKey: "k-empty"));

        Assert.Equal(first.Value.Id, reordered.Value.Id);
        Assert.Equal(noPropertiesVsEmpty.Value.Id, emptyProperties.Value.Id);
        Assert.Equal(2, await Db.Records.CountAsync(r => r.CollectionId == a.Collection.Id, Ct));
    }

    [Fact]
    public async Task Create_SameKey_OtherEscapingAndNumberSpelling_Replay()
    {
        var a = await ArrangeAsync();
        var first = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Einmal", Properties: Props("""{ "notes": "Café", "estimate": 1 }"""), IdempotencyKey: "k"));

        var retry = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Einmal", Properties: Props("""{ "notes": "Caf\u00e9", "estimate": 1.0 }"""), IdempotencyKey: "k"));

        Assert.True(first.IsSuccess, Describe(first.Error));
        Assert.Equal(first.Value.Id, retry.Value.Id);
    }

    [Fact]
    public async Task Create_ConcurrentSameKeyDifferentContent_OneCreated_OtherConflict()
    {
        var a = await ArrangeAsync();
        Result<RecordDto>? concurrent = null;
        Db.ChangeTracker.Clear();
        var racing = new Collections.RacingDbContext(Db, async () =>
        {
            // A second request with the same key and another title commits while the first one is about to save.
            var options = (DbContextOptions<Infrastructure.Persistence.AgcoworkDbContext>)Db.GetService<IDbContextOptions>();
            await using var other = new Infrastructure.Persistence.AgcoworkDbContext(options, new TestTenantAccessor { Current = a.Tenant });
            var actor = TestActor.Agent(a.Seed.AlphaOwner.Id);
            concurrent = await new CreateRecord(other, actor, a.Tenant, Validator(), new Agcowork.Application.Activity.ActivityRecorder(other, actor, a.Tenant, Clock), Clock)
                .HandleAsync(new CreateRecordRequest(a.Collection.Id, Title: "Zweiter", IdempotencyKey: "race"), Ct);
        });

        var first = await new CreateRecord(racing, TestActor.Agent(a.Seed.AlphaOwner.Id), a.Tenant, Validator(), Activity, Clock)
            .HandleAsync(new CreateRecordRequest(a.Collection.Id, Title: "Erster", IdempotencyKey: "race"), Ct);

        Assert.True(concurrent!.IsSuccess, Describe(concurrent.Error));
        var conflict = Assert.IsType<ConflictError>(first.Error);
        Assert.Equal(("idempotency_key_reused", "Idempotency-Key"), (conflict.Code, conflict.Field));
        Db.ChangeTracker.Clear();
        Assert.Equal("Zweiter", Assert.Single(await Db.Records.AsNoTracking().Where(r => r.CollectionId == a.Collection.Id).ToListAsync(Ct)).Title);
        Assert.Equal(1, await Db.IdempotencyKeys.CountAsync(Ct));
    }

    [Fact]
    public async Task Create_SameKeyDifferentRequest_Conflict_NothingStored()
    {
        var a = await ArrangeAsync();
        var template = await CreateTemplateAsync(a, """{ "status": "doing" }""");
        var parent = await CreateRecordAsync(a, "Eltern");
        var request = new CreateRecordRequest(a.Collection.Id, Title: "Einmal", Properties: Props("""{ "status": "doing" }"""), Body: "Text", IdempotencyKey: "key-1");
        Assert.True((await TryCreateAsync(a, request)).IsSuccess);

        CreateRecordRequest[] different =
        [
            request with { Title = "Anderer Titel" },
            request with { Title = "Einmal " },
            request with { Properties = Props("""{ "status": "done" }""") },
            request with { Properties = Props("""{ "status": "doing", "tags": null }""") },
            request with { Properties = null },
            request with { Body = "Text\n" },
            request with { Body = null },
            request with { SpaceId = a.SpaceId },
            request with { TemplateId = template.Id },
            request with { SkipTemplate = true },
            request with { ParentRecordId = parent.Id },
            request with { CollectionId = null, SpaceId = a.SpaceId },
        ];
        foreach (var other in different)
        {
            var result = await TryCreateAsync(a, other);

            var conflict = Assert.IsType<ConflictError>(result.Error);
            Assert.Equal(("idempotency_key_reused", "Idempotency-Key"), (conflict.Code, conflict.Field));
            Assert.Contains("Same key, different request", conflict.Message, StringComparison.Ordinal);
        }

        Assert.Equal(2, await Db.Records.CountAsync(r => r.CollectionId == a.Collection.Id, Ct));
        Assert.Equal(1, await Db.IdempotencyKeys.CountAsync(Ct));
    }

    [Fact]
    public async Task Create_KeyStoredWithoutHash_ReplaysLikeBefore()
    {
        var a = await ArrangeAsync();
        var first = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Einmal", IdempotencyKey: "alt"));
        await Db.Database.ExecuteSqlAsync($"UPDATE idempotency_keys SET request_hash = NULL", Ct);

        var second = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Anderer Titel", IdempotencyKey: "alt"));

        Assert.Equal(first.Value.Id, second.Value.Id);
        Assert.Equal(1, await Db.Records.CountAsync(r => r.CollectionId == a.Collection.Id, Ct));
    }

    [Fact]
    public async Task Create_SameKeyOtherActor_SecondRecord()
    {
        var a = await ArrangeAsync();
        var request = new CreateRecordRequest(a.Collection.Id, Title: "Einmal", IdempotencyKey: "shared");

        var mine = await TryCreateAsync(a, request);
        Db.ChangeTracker.Clear();
        var other = await CreateUseCase(Enter(a.Seed.Alpha, a.Seed.MemberOfBoth), a.Seed.MemberOfBoth).HandleAsync(request, Ct);

        Assert.NotEqual(mine.Value.Id, other.Value.Id);
    }

    [Fact]
    public async Task Create_KeyOlderThan24Hours_NewRecord()
    {
        var a = await ArrangeAsync();
        var request = new CreateRecordRequest(a.Collection.Id, Title: "Einmal", IdempotencyKey: "old");

        var first = await TryCreateAsync(a, request);
        Clock.Advance(IdempotencyKey.Lifetime);
        var second = await TryCreateAsync(a, request);
        var third = await TryCreateAsync(a, request);

        Assert.NotEqual(first.Value.Id, second.Value.Id);
        Assert.Equal(second.Value.Id, third.Value.Id);
        Assert.Equal(1, await Db.IdempotencyKeys.CountAsync(Ct));
    }

    [Fact]
    public async Task Create_InvalidKey_ValidationError()
    {
        var a = await ArrangeAsync();

        var result = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "T", IdempotencyKey: new string('k', IdempotencyKey.KeyMaxLength + 1)));

        Assert.Equal(ErrorCodes.InvalidFormat, Field(Invalid(result), "Idempotency-Key").Code);
    }

    [Fact]
    public async Task Create_KeyFailedValidationFirst_NotStored_RetryCreates()
    {
        var a = await ArrangeAsync();
        var invalid = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, IdempotencyKey: "retry"));

        var fixedRequest = await TryCreateAsync(a, new CreateRecordRequest(a.Collection.Id, Title: "Jetzt", IdempotencyKey: "retry"));

        Assert.IsType<ValidationError>(invalid.Error);
        Assert.True(fixedRequest.IsSuccess);
    }
}
