using Agcowork.Application.Collections;
using Agcowork.Application.Collections.Templates;
using Agcowork.Application.Common.Results;
using Agcowork.Application.Records;
using Agcowork.Application.Tests.Records;
using Microsoft.EntityFrameworkCore;

namespace Agcowork.Application.Tests.Schema;

/// <summary>Precision of a number property (AP-042): loss only for stored values with more decimals; removal with clearPrecision.</summary>
public sealed class PrecisionChangeTests(PostgresFixture postgres) : RecordTestBase(postgres)
{
    private async Task<Result<CollectionSchemaDto>> UpdateSchemaAsync(Arranged a, UpdateSchemaRequest request, params ISchemaChangedHook[] extraHooks)
    {
        Db.ChangeTracker.Clear();
        var result = await new UpdateSchema(Db, a.Tenant, [new RecordValuesSchemaHook(Db), .. extraHooks], Activity).HandleAsync(request, Ct);
        Db.ChangeTracker.Clear();
        return result;
    }

    private async Task<decimal?> EstimateAsync(Guid recordId) =>
        (await Db.Records.AsNoTracking().SingleAsync(r => r.Id == recordId, Ct)).Properties.TryGetValue("estimate", out var value) ? value.GetDecimal() : null;

    [Fact]
    public async Task UpdateSchema_LowerPrecision_ValuesWithMoreDecimals_NeedsConfirmation_NamesRecordCount()
    {
        var a = await ArrangeAsync();
        var many = await CreateRecordAsync(a, "A", """{ "estimate": 2.5 }""");
        await CreateRecordAsync(a, "B", """{ "estimate": 1.5 }""");
        var fits = await CreateRecordAsync(a, "C", """{ "estimate": 3 }""");
        await CreateRecordAsync(a, "D");

        var unconfirmed = await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", Precision: 0)]));
        var confirmed = await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", Precision: 0)], ConfirmRemoval: true));

        var error = Field(Invalid(unconfirmed), "confirmRemoval");
        Assert.Equal(ErrorCodes.RemovalNotConfirmed, error.Code);
        Assert.Contains("'estimate': precision lowered to 0 decimal places, 2 records have values", error.Message, StringComparison.Ordinal);
        Assert.True(confirmed.IsSuccess);
        Assert.Equal(3m, await EstimateAsync(many.Id));
        Assert.Equal(3m, await EstimateAsync(fits.Id));
    }

    [Fact]
    public async Task UpdateSchema_LowerPrecision_AllValuesFit_NoConfirmationNeeded()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, "A", """{ "estimate": 2.0 }""");
        await CreateRecordAsync(a, "B");

        var result = await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", Precision: 0)]));

        Assert.True(result.IsSuccess, Describe(result.Error));
        Assert.Equal(2m, await EstimateAsync(record.Id));
    }

    [Fact]
    public async Task UpdateSchema_FirstPrecision_OnPropertyWithoutOne_LossOnlyWithMoreDecimals()
    {
        var a = await ArrangeAsync();
        Assert.True((await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", ClearPrecision: true)]))).IsSuccess);
        var empty = await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", Precision: 2)]));
        Assert.True((await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", ClearPrecision: true)]))).IsSuccess);
        await CreateRecordAsync(a, "A", """{ "estimate": 1.234 }""");
        Assert.True((await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", ClearPrecision: true)]))).IsSuccess);

        var unconfirmed = await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", Precision: 2)]));
        var fits = await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", Precision: 3)]));

        Assert.True(empty.IsSuccess, Describe(empty.Error));
        var message = Field(Invalid(unconfirmed), "confirmRemoval");
        Assert.Equal(ErrorCodes.RemovalNotConfirmed, message.Code);
        Assert.Contains("precision set to 2 decimal places, 1 record has a value", message.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("lowered", message.Message, StringComparison.Ordinal);
        Assert.True(fits.IsSuccess, Describe(fits.Error));
    }

    [Fact]
    public async Task UpdateSchema_LowerPrecision_RecordsAndTemplatePreset_BothReported_BothRounded()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, "A", """{ "estimate": 2.5 }""");
        var template = await CreateTemplateAsync(a, """{ "estimate": 1.5 }""");

        var unconfirmed = await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", Precision: 0)]));
        var confirmed = await UpdateSchemaAsync(
            a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", Precision: 0)], ConfirmRemoval: true), new TemplatePresetSchemaHook(Db));

        var message = Field(Invalid(unconfirmed), "confirmRemoval").Message;
        Assert.Contains("precision lowered to 0 decimal places, 1 record has a value", message, StringComparison.Ordinal);
        Assert.Contains("template 'Standard': preset 'estimate' would be rounded to 0 decimal places", message, StringComparison.Ordinal);
        Assert.True(confirmed.IsSuccess, Describe(confirmed.Error));
        Assert.Equal(3m, await EstimateAsync(record.Id));
        Assert.Equal(2m, (await Db.Templates.AsNoTracking().SingleAsync(t => t.Id == template.Id, Ct)).PropertiesPreset["estimate"].GetDecimal());
    }

    [Fact]
    public async Task UpdateSchema_ClearPrecision_RemovesLimit_ValuesStayAndMoreDecimalsAccepted()
    {
        var a = await ArrangeAsync();
        var record = await CreateRecordAsync(a, "A", """{ "estimate": 2.5 }""");

        var cleared = await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", ClearPrecision: true)]));
        var again = await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", ClearPrecision: true)]));
        var precise = await UpdateAsync(a, new UpdateRecordRequest(record.Id, Properties: Props("""{ "estimate": 1.23456 }""")));

        Assert.True(cleared.IsSuccess, Describe(cleared.Error));
        Assert.Null(cleared.Value.Properties.Single(p => p.Key == "estimate").Precision);
        Assert.Equal(cleared.Value.SchemaVersion, again.Value.SchemaVersion);
        Assert.True(precise.IsSuccess, Describe(precise.Error));
        Assert.Equal(1.23456m, await EstimateAsync(record.Id));
    }

    [Fact]
    public async Task UpdateSchema_ClearPrecisionWithPrecision_ConflictOnClearPrecision()
    {
        var a = await ArrangeAsync();

        var result = await UpdateSchemaAsync(a, new UpdateSchemaRequest(a.Collection.Id, Update: [new("estimate", Precision: 2, ClearPrecision: true)]));

        Assert.Equal(ErrorCodes.Conflict, Field(Invalid(result), "update[0].clearPrecision").Code);
    }
}
