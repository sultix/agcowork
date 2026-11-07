using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Domain.Records;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Records;

public sealed class IdempotencyTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    [Fact]
    public async Task Create_SameKeyTwice_OneRecord_SameResponse()
    {
        var a = await ArrangeAsync();
        var request = new CreateRecordRequest(a.Collection.Id, Title: "Einmal", IdempotencyKey: "key-1");

        var first = await TryCreateAsync(a, request);
        var second = await TryCreateAsync(a, request with { Title = "Anderer Titel" });

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(first.Value, second.Value with { Properties = first.Value.Properties });
        Assert.Equal(first.Value.Properties.Keys, second.Value.Properties.Keys);
        Assert.Equal(1, await Db.Records.CountAsync(r => r.CollectionId == a.Collection.Id, Ct));
        Assert.Equal(1, await Db.IdempotencyKeys.CountAsync(Ct));
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
