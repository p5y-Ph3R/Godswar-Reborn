using Godswar.Server.Application.Zodiac;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresZodiacSkillGridUpgradeCommandIntegrationChecks
{
    private static async Task AssertReplayRejectionAndConflictAsync(
        string connectionString)
    {
        await AssertExactReplayAsync(connectionString);
        await AssertSequentialFreshCommandsAsync(connectionString);
        await AssertTerminalRejectionIsStableAsync(connectionString);
        await AssertUuidGridConflictAsync(connectionString);
    }

    private static async Task AssertExactReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "replay",
            energy: 20,
            energyRemainderX100: 51,
            talentPoints: 30);
        var operationId = Guid.NewGuid();
        var first = CreateEnvelope(fixture, 0, operationId);
        var retry = CreateEnvelope(
            fixture,
            0,
            operationId,
            connectionId: Guid.NewGuid());
        Check.True(
            string.Equals(
                first.OperationId,
                retry.OperationId,
                StringComparison.Ordinal) &&
            string.Equals(
                first.RequestHash,
                retry.RequestHash,
                StringComparison.Ordinal),
            "reconnect preserves Zodiac upgrade UUID identity");

        await using var firstSource =
            NpgsqlDataSource.Create(connectionString);
        var committed = RequireReceipt(
            await CreateExecutor(firstSource).ExecuteAsync(first),
            ZodiacSkillGridUpgradeExecutionDisposition.Committed,
            "first Zodiac upgrade");
        await using var retrySource =
            NpgsqlDataSource.Create(connectionString);
        var duplicateResult =
            await CreateExecutor(retrySource).ExecuteAsync(retry);
        var duplicate = RequireReceipt(
            duplicateResult,
            ZodiacSkillGridUpgradeExecutionDisposition.Duplicate,
            "exact Zodiac upgrade retry");
        Check.Equal(
            committed,
            duplicate,
            "exact retry returns the original Zodiac receipt");
        Check.True(
            duplicateResult.CurrentEnergy == 15 &&
            duplicateResult.CurrentEnergyRemainderX100 == 51 &&
            duplicateResult.CurrentTalentPoints == 23 &&
            duplicateResult.CurrentLevel == 2,
            "exact retry returns the current authoritative projection");

        var state = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex: 0);
        Check.True(
            state.Energy == 15 &&
            state.EnergyRemainderX100 == 51 &&
            state.TalentPoints == 23 &&
            state.Level == 2 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1 &&
            state.CurrencyLedgerCount == 0,
            "exact replay neither respends resources nor republishes");
    }

    private static async Task AssertSequentialFreshCommandsAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "series",
            energy: 1_000,
            energyRemainderX100: 75,
            talentPoints: 1_000,
            zodiacLevel: 30);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);

        var first = RequireReceipt(
            await executor.ExecuteAsync(
                CreateEnvelope(
                    fixture,
                    gridIndex: 0,
                    Guid.NewGuid())),
            ZodiacSkillGridUpgradeExecutionDisposition.Committed,
            "first fresh sequential Zodiac upgrade");
        var second = RequireReceipt(
            await executor.ExecuteAsync(
                CreateEnvelope(
                    fixture,
                    gridIndex: 0,
                    Guid.NewGuid())),
            ZodiacSkillGridUpgradeExecutionDisposition.Committed,
            "second fresh sequential Zodiac upgrade");

        Check.True(
            first.PreviousLevel == 1 &&
            first.CurrentLevel == 2 &&
            first.AggregateRevision == 2 &&
            first.EnergyCost == 5 &&
            first.EnergyBefore == 1_000 &&
            first.EnergyRemainderBeforeX100 == 75 &&
            first.EnergyAfter == 995 &&
            first.EnergyRemainderAfterX100 == 75 &&
            first.TalentPointCost == 7 &&
            first.TalentPointsBefore == 1_000 &&
            first.TalentPointsAfter == 993,
            "first fresh UUID records the exact level-one spend");
        Check.True(
            second.PreviousLevel == 2 &&
            second.CurrentLevel == 3 &&
            second.AggregateRevision == 3 &&
            second.EnergyCost == 12 &&
            second.EnergyBefore == 995 &&
            second.EnergyRemainderBeforeX100 == 75 &&
            second.EnergyAfter == 983 &&
            second.EnergyRemainderAfterX100 == 75 &&
            second.TalentPointCost == 15 &&
            second.TalentPointsBefore == 993 &&
            second.TalentPointsAfter == 978,
            "second fresh UUID records the exact level-two spend");
        Check.True(
            first.OutboxEventId is { } firstEvent &&
            firstEvent != Guid.Empty &&
            second.OutboxEventId is { } secondEvent &&
            secondEvent != Guid.Empty &&
            firstEvent != secondEvent,
            "sequential upgrades publish distinct immutable events");

        var state = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex: 0);
        Check.True(
            state.Energy == 983 &&
            state.EnergyRemainderX100 == 75 &&
            state.TalentPoints == 978 &&
            state.Level == 3 &&
            state.SelectedSkillId == -1 &&
            state.AuditCount == 2 &&
            state.InboxCount == 2 &&
            state.DuplicateCount == 0 &&
            state.ConflictCount == 0 &&
            state.OutboxCount == 2 &&
            state.HasLatestWinsEvidence &&
            state.HasAuditResourceEvidence &&
            state.TerminalRejectedCount == 0 &&
            state.CurrencyLedgerCount == 0,
            "two fresh UUIDs advance 1-to-2-to-3 with durable evidence");
    }

    private static async Task AssertTerminalRejectionIsStableAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "reject",
            energy: 4,
            energyRemainderX100: 99,
            talentPoints: 20,
            zodiacLevel: 30);
        var operationId = Guid.NewGuid();
        var envelope = CreateEnvelope(fixture, 0, operationId);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var firstResult =
            await CreateExecutor(dataSource).ExecuteAsync(envelope);
        var first = RequireReceipt(
            firstResult,
            ZodiacSkillGridUpgradeExecutionDisposition.TerminalRejected,
            "insufficient-energy Zodiac upgrade");
        Check.True(
            first.Status ==
                ZodiacSkillGridUpgradeReceiptStatus.InsufficientEnergy &&
            first.EnergyBefore == 4 &&
            first.EnergyRemainderBeforeX100 == 99 &&
            first.EnergyAfter == 4 &&
            first.EnergyRemainderAfterX100 == 99 &&
            first.TalentPointsBefore == 20 &&
            first.TalentPointsAfter == 20 &&
            first.OutboxEventId is null,
            "terminal rejection records immutable unchanged resources");
        var rejectedState = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex: 0);
        Check.True(
            rejectedState.Energy == 4 &&
            rejectedState.EnergyRemainderX100 == 99 &&
            rejectedState.TalentPoints == 20 &&
            rejectedState.Level == 1 &&
            rejectedState.AuditCount == 1 &&
            rejectedState.InboxCount == 1 &&
            rejectedState.TerminalRejectedCount == 1 &&
            rejectedState.OutboxCount == 0 &&
            rejectedState.HasAuditResourceEvidence &&
            rejectedState.CurrencyLedgerCount == 0,
            "terminal rejection persists audit and inbox without mutation");

        await SetResourcesAsync(
            connectionString,
            fixture,
            energy: 100,
            remainder: 25,
            talentPoints: 100);
        var replayResult = await CreateExecutor(dataSource).ExecuteAsync(
            CreateEnvelope(
                fixture,
                0,
                operationId,
                connectionId: Guid.NewGuid()));
        var replay = RequireReceipt(
            replayResult,
            ZodiacSkillGridUpgradeExecutionDisposition.Duplicate,
            "terminal Zodiac replay after top-up");
        Check.Equal(
            first,
            replay,
            "terminal replay preserves its original immutable receipt");
        Check.True(
            !replayResult.IsSuccess &&
            replayResult.CurrentEnergy == 100 &&
            replayResult.CurrentEnergyRemainderX100 == 25 &&
            replayResult.CurrentTalentPoints == 100 &&
            replayResult.CurrentLevel == 1,
            "terminal replay returns original outcome plus current state");
        var replayedState = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex: 0);
        Check.True(
            replayedState.Level == 1 &&
            replayedState.OutboxCount == 0 &&
            replayedState.DuplicateCount == 1 &&
            replayedState.InboxCount == 1,
            "resource top-up cannot turn a settled UUID into success");

        var freshResult = await CreateExecutor(dataSource).ExecuteAsync(
            CreateEnvelope(
                fixture,
                0,
                Guid.NewGuid(),
                connectionId: Guid.NewGuid()));
        var freshReceipt = RequireReceipt(
            freshResult,
            ZodiacSkillGridUpgradeExecutionDisposition.Committed,
            "fresh Zodiac UUID after terminal rejection");
        Check.True(
            freshReceipt.PreviousLevel == 1 &&
            freshReceipt.CurrentLevel == 2 &&
            freshReceipt.EnergyBefore == 100 &&
            freshReceipt.EnergyRemainderBeforeX100 == 25 &&
            freshReceipt.EnergyAfter == 95 &&
            freshReceipt.EnergyRemainderAfterX100 == 25 &&
            freshReceipt.TalentPointsBefore == 100 &&
            freshReceipt.TalentPointsAfter == 93,
            "fresh UUID evaluates current resources and succeeds");
        var freshState = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex: 0);
        Check.True(
            freshState.Energy == 95 &&
            freshState.EnergyRemainderX100 == 25 &&
            freshState.TalentPoints == 93 &&
            freshState.Level == 2 &&
            freshState.AuditCount == 2 &&
            freshState.InboxCount == 2 &&
            freshState.TerminalRejectedCount == 1 &&
            freshState.OutboxCount == 1 &&
            freshState.HasLatestWinsEvidence &&
            freshState.CurrencyLedgerCount == 0,
            "terminal rejection is per UUID, not an aggregate lockout");
    }

    private static async Task AssertUuidGridConflictAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "scope",
            energy: 100,
            energyRemainderX100: 0,
            talentPoints: 100,
            secondGridIndex: 1);
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await CreateExecutor(dataSource).ExecuteAsync(
                CreateEnvelope(fixture, 0, operationId)),
            ZodiacSkillGridUpgradeExecutionDisposition.Committed,
            "grid-zero Zodiac upgrade");
        var conflict = await CreateExecutor(dataSource).ExecuteAsync(
            CreateEnvelope(
                fixture,
                1,
                operationId,
                connectionId: Guid.NewGuid()));
        Check.True(
            conflict.Disposition ==
                ZodiacSkillGridUpgradeExecutionDisposition
                    .RequestHashConflict &&
            conflict.Receipt is null,
            "one UUID cannot authorize a different Zodiac grid");
        var gridZero = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex: 0);
        var gridOne = await ReadStateAsync(
            connectionString,
            fixture,
            gridIndex: 1);
        Check.True(
            gridZero.Level == 2 &&
            gridOne.Level == 1 &&
            gridZero.InboxCount == 1 &&
            gridOne.InboxCount == 1 &&
            gridOne.ConflictCount == 1 &&
            gridOne.OutboxCount == 0,
            "character-scoped inbox detects changed-grid UUID reuse");
    }
}
