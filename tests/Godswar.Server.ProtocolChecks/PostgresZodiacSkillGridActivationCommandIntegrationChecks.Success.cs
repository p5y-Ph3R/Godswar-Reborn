using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridActivationCommandIntegrationChecks
{
    private static async Task AssertOwnershipAndSuccessAsync(
        string connectionString)
    {
        await AssertWrongOwnerAsync(connectionString);
        await AssertPaidActivationAsync(connectionString);
        await AssertFreeActivationAsync(connectionString);
    }

    private static async Task AssertWrongOwnerAsync(
        string connectionString)
    {
        const int gridIndex = 1;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "owner",
            gold: 5_000);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(dataSource).ExecuteAsync(
            CreateEnvelope(
                fixture,
                gridIndex,
                subject: new CommandSubject(
                    fixture.AccountId + 1,
                    fixture.CharacterId)));
        Check.True(
            result.Disposition ==
                ZodiacSkillGridActivationExecutionDisposition
                    .PreconditionFailed &&
            result.Receipt is null,
            "Zodiac activation rejects a character owned by another account");

        AssertUntouched(
            await ReadStateAsync(
                connectionString,
                fixture,
                gridIndex),
            expectedGold: 5_000,
            "wrong-owner activation");
    }

    private static async Task AssertPaidActivationAsync(
        string connectionString)
    {
        const int gridIndex = 1;
        const int cost = 2_300;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "paid",
            gold: 5_000);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var result = await CreateExecutor(dataSource).ExecuteAsync(
            CreateEnvelope(fixture, gridIndex));
        var receipt = RequireReceipt(
            result,
            ZodiacSkillGridActivationExecutionDisposition.Committed,
            "paid Zodiac activation");

        Check.True(
            receipt.CharacterId == fixture.CharacterId &&
            receipt.GridIndex == gridIndex &&
            receipt.GoldCost == cost &&
            receipt.GoldBefore == 5_000 &&
            receipt.GoldAfter == 2_700 &&
            receipt.CurrentLevel == 1 &&
            receipt.SelectedSkillId == -1 &&
            receipt.WalletRevision == 1 &&
            long.TryParse(receipt.AuditReference, out var auditId) &&
            auditId > 0 &&
            receipt.OutboxEventId != Guid.Empty,
            "paid activation returns canonical wallet and grid evidence");
        Check.True(
            result.HasAuthoritativeProjection &&
            result.CurrentGold == 2_700 &&
            result.CurrentLevel == 1 &&
            result.SelectedSkillId == -1 &&
            result.CurrentWalletRevision == 1,
            "paid activation returns its authoritative projection");

        var state = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex);
        AssertCommitted(
            state,
            expectedGold: 2_700,
            expectedWalletRevision: 1,
            expectedLedgerCount: 1,
            expectedLedgerDelta: -cost,
            "paid activation");

        await using var reopened =
            new PostgresGameStore(connectionString);
        await reopened.EnsureSeedDataAsync();
        var reloaded =
            (await reopened.GetCharactersAsync(fixture.AccountId))
            .Single(character =>
                character.Id == fixture.CharacterId);
        Check.True(
            reloaded.Gold == 2_700 &&
            reloaded.ZodiacSkillGridLevels[gridIndex] == 1 &&
            reloaded.ZodiacSkillGridSkillIds[gridIndex] == -1,
            "paid activation survives a fresh durable reload");
    }

    private static async Task AssertFreeActivationAsync(
        string connectionString)
    {
        const int gridIndex = 0;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "free",
            gold: 5_000);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await CreateExecutor(dataSource).ExecuteAsync(
                CreateEnvelope(fixture, gridIndex)),
            ZodiacSkillGridActivationExecutionDisposition.Committed,
            "free Zodiac activation");
        Check.True(
            receipt.GoldCost == 0 &&
            receipt.GoldBefore == 5_000 &&
            receipt.GoldAfter == 5_000 &&
            receipt.WalletRevision == 0,
            "free activation records no invented wallet transition");

        AssertCommitted(
            await ReadStateAsync(
                connectionString,
                fixture,
                gridIndex),
            expectedGold: 5_000,
            expectedWalletRevision: 0,
            expectedLedgerCount: 0,
            expectedLedgerDelta: 0,
            "free activation");
    }

    private static void AssertCommitted(
        ZodiacDurableState state,
        int expectedGold,
        long expectedWalletRevision,
        long expectedLedgerCount,
        long expectedLedgerDelta,
        string description)
    {
        Check.True(
            state.Gold == expectedGold &&
            state.WalletRevision == expectedWalletRevision &&
            state.Level == 1 &&
            state.SelectedSkillId == -1 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.CurrencyLedgerCount == expectedLedgerCount &&
            state.GoldLedgerDelta == expectedLedgerDelta &&
            state.OutboxCount == 1 &&
            state.HasStrictOutbox &&
            state.WalletReconciled,
            $"{description} atomically commits strict durable evidence");
    }

    private static void AssertUntouched(
        ZodiacDurableState state,
        int expectedGold,
        string description)
    {
        Check.True(
            state.Gold == expectedGold &&
            state.WalletRevision == 0 &&
            state.Level == 0 &&
            state.SelectedSkillId == -1 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.DuplicateCount == 0 &&
            state.ConflictCount == 0 &&
            state.CurrencyLedgerCount == 0 &&
            state.GoldLedgerDelta == 0 &&
            state.OutboxCount == 0 &&
            !state.HasStrictOutbox &&
            state.WalletReconciled,
            $"{description} leaves no durable side effect");
    }
}
