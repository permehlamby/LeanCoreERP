using FinanceCore.Core.Commands;
using FinanceCore.Core.Masters;

namespace FinanceCore.Data.Tests;

/// <summary>exchange-rates:mark-used (23, 5; CT-10): the usage lock for the rate types AVERAGE and HISTORICAL that no posting uses.</summary>
[Collection("db")]
public class RateUsageTests
{
    private static string Describe<T>(CommandResult<T> r) => r.Status + ": " + string.Join(", ", r.Errors.Select(e => $"{e.Code} {e.Path}"));
    private static string[] Codes<T>(CommandResult<T> r) => r.Errors.Select(e => e.Code).ToArray();
    private static CommandContext Ctx(TestTenant t, string key) => t.Context(key, RateUsageCommands.Permission);

    private static async Task<Guid> Rate(TestTenant t, string type, string validFrom, decimal rate = 7.46m)
    {
        Guid id = Guid.NewGuid();
        await t.ExecuteAsync("insert into core.exchange_rate (tenant_id, rate_id, rate_type, from_currency, to_currency, valid_from, rate, source) values (@t, @id, @ty, 'DKK', 'EUR', @d::date, @r, 'test')",
            ("id", id), ("ty", type), ("d", validFrom), ("r", rate));
        return id;
    }

    private static Task<CommandResult<MarkedRatesResult>> Mark(TestTenant t, string key, params Guid[] ids) =>
        new RateUsageCommands(t.App).MarkUsedAsync(t.TenantId, Ctx(t, key), new(ids, "consolidation:2026-Q1"));

    [Fact]
    public async Task AMarkedRateCannotChangeAndTheCommandReplaysAndIsIdempotentPerRow()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid avg = await Rate(t, "AVERAGE", "2026-01-01"), hist = await Rate(t, "HISTORICAL", "2026-02-01", 7.5m);

        // before the mark the rate can change
        await t.ExecuteAsync("update core.exchange_rate set rate = 7.47 where tenant_id = @t and rate_id = @id", ("id", avg));

        var first = await Mark(t, "m1", avg, hist);
        Assert.True(first.Status == CommandStatus.Ok, Describe(first));
        Assert.Equal(2, first.Value!.Marked.Count);
        Assert.Empty(first.Value.AlreadyUsed);
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.exchange_rate where tenant_id = @t and first_used_at is not null"));
        Assert.Equal(2L, await t.ScalarAsync<long>("select count(*) from core.master_change_log where tenant_id = @t and entity_type = 'EXCHANGE_RATE' and field_name = 'first_used_at'"));
        await Assert.ThrowsAnyAsync<Exception>(() => t.ExecuteAsync("update core.exchange_rate set rate = 7.48 where tenant_id = @t and rate_id = @id", ("id", avg)));

        var replay = await Mark(t, "m1", avg, hist);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Value.Marked.Order(), replay.Value!.Marked.Order());
        Assert.Equal(["IDEMPOTENCY.KEY_REUSED_DIFFERENT_REQUEST"], Codes(await Mark(t, "m1", avg)));

        // a second run over the same rows and a new one: the used ones stay, the new one is marked
        Guid third = await Rate(t, "AVERAGE", "2026-03-01");
        var second = await Mark(t, "m2", avg, third);
        Assert.True(second.Status == CommandStatus.Ok, Describe(second));
        Assert.Equal([third], second.Value!.Marked);
        Assert.Equal([avg], second.Value.AlreadyUsed);
    }

    [Fact]
    public async Task AnUnknownIdRejectsTheWholeCommandAndTheInputIsChecked()
    {
        var t = await TestTenant.CreateReadyAsync();
        Guid avg = await Rate(t, "AVERAGE", "2026-01-01");
        var r = await Mark(t, "u", avg, Guid.NewGuid());
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(r));
        Assert.Equal(0L, await t.ScalarAsync<long>("select count(*) from core.exchange_rate where tenant_id = @t and first_used_at is not null"));

        Assert.Equal(["SCHEMA.REQUIRED_FIELD_MISSING"], Codes(await Mark(t, "e")));
        var blank = await new RateUsageCommands(t.App).MarkUsedAsync(t.TenantId, Ctx(t, "p"), new([avg], " "));
        Assert.Equal(["SCHEMA.INVALID_VALUE"], Codes(blank));
        var denied = await new RateUsageCommands(t.App).MarkUsedAsync(t.TenantId, t.Context("d", "journal.post"), new([avg], "x"));
        Assert.Equal(["AUTH.PERMISSION_MISSING"], Codes(denied));
    }
}
