using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertCombinationTransactionsAsync(
        string connectionString)
    {
        await AssertCombinationCommitAndReplayAsync(connectionString);
        await AssertCombinationRejectsMismatchedLevelAsync(
            connectionString);
        await AssertCombinationThirdMaterialRejectionsAsync(
            connectionString);
        await AssertCombinationAtomicRollbackAsync(connectionString);
    }

    private static async Task AssertCombinationCommitAndReplayAsync(
        string connectionString)
    {
        const short secondMaterialSlot = 11;
        const short thirdMaterialSlot = 12;
        var firstMaterial = SimpleItem(9030, grade: 4, stack: 2) with
        {
            Bound = 0
        };
        var secondMaterial = SimpleItem(9030, grade: 4);
        var thirdMaterial = SimpleItem(9030, grade: 4, stack: 3) with
        {
            Bound = 0
        };
        var fixture = await CreateFixtureAsync(
            connectionString,
            "cmbok",
            target: SimpleItem(9030, grade: 4) with { Bound = 0 },
            stone: firstMaterial,
            additionalBagItems:
            [
                (secondMaterialSlot, secondMaterial),
                (thirdMaterialSlot, thirdMaterial)
            ]);
        var operationId = Guid.NewGuid();
        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        var executor = CreateExecutor(dataSource);

        var committed = RequireReceipt(
            await ExecuteCombinationAsync(
                executor,
                fixture,
                operationId,
                secondMaterialSlot,
                secondMaterial.ToCompactString(),
                thirdMaterialSlot,
                thirdMaterial.ToCompactString()),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.Combined,
            "same-level Holy Stone Combination");
        Check.Equal(
            HolyStoneNativeResults.CombinationSucceededSubId,
            committed.NativeResultSubId,
            "Combination returns stock success result");

        var duplicate = RequireReceipt(
            await ExecuteCombinationAsync(
                executor,
                fixture,
                operationId,
                secondMaterialSlot,
                secondMaterial.ToCompactString(),
                thirdMaterialSlot,
                thirdMaterial.ToCompactString()),
            HolyStoneExecutionDisposition.Duplicate,
            HolyStoneCommandResultStatus.Combined,
            "same Combination replay");
        Check.Equal(
            committed,
            duplicate,
            "Combination replay returns the exact stored receipt");

        var target = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.TargetSlot))!.Value;
        var firstAfter = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.StoneSlot))!.Value.Item;
        var secondAfter = await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            secondMaterialSlot);
        var thirdAfter = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            thirdMaterialSlot))!.Value.Item;
        Check.True(
            target.Id == fixture.TargetItemId &&
            target.Item.Id == 9030 &&
            target.Item.Grade == 5 &&
            target.Item.Bound == 1 &&
            target.Item.Stack == 1,
            "Combination upgrades the primary row in place");
        Check.True(
            firstAfter.Stack == 1 &&
            secondAfter is null &&
            thirdAfter.Stack == 2,
            "Combination consumes exactly one from all three fodder rows");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Combine);
        Check.True(
            state.InventoryRevision == 1 &&
            state.LedgerCount == 4 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1,
            "Combination commits four mutations and one durable command");
    }

    private static async Task AssertCombinationRejectsMismatchedLevelAsync(
        string connectionString)
    {
        const short secondMaterialSlot = 13;
        const short thirdMaterialSlot = 14;
        var secondMaterial = SimpleItem(9030, grade: 5);
        var thirdMaterial = SimpleItem(9030, grade: 4);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "cmbbad",
            target: SimpleItem(9030, grade: 4),
            stone: SimpleItem(9030, grade: 4),
            additionalBagItems:
            [
                (secondMaterialSlot, secondMaterial),
                (thirdMaterialSlot, thirdMaterial)
            ]);
        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        var receipt = RequireReceipt(
            await ExecuteCombinationAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                secondMaterialSlot,
                secondMaterial.ToCompactString(),
                thirdMaterialSlot,
                thirdMaterial.ToCompactString()),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.CombinationNotAllowed,
            "mismatched-level Holy Stone Combination");
        Check.Equal(
            HolyStoneNativeResults.CombinationNotAllowedSubId,
            receipt.NativeResultSubId,
            "mismatched Combination returns same-level guidance");
        Check.Equal(
            fixture.TargetState,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.TargetSlot))!.Value.Item.ToCompactString(),
            "rejected Combination leaves the primary unchanged");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Combine),
            "mismatched-level Combination");
    }

    private static async Task AssertCombinationAtomicRollbackAsync(
        string connectionString)
    {
        const short secondMaterialSlot = 15;
        const short thirdMaterialSlot = 17;
        var firstMaterial = SimpleItem(9031, grade: 7, stack: 2);
        var secondMaterial = SimpleItem(9031, grade: 7, stack: 2);
        var thirdMaterial = SimpleItem(9031, grade: 7, stack: 2);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "cmbroll",
            target: SimpleItem(9031, grade: 7),
            stone: firstMaterial,
            additionalBagItems:
            [
                (secondMaterialSlot, secondMaterial),
                (thirdMaterialSlot, thirdMaterial)
            ]);
        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        try
        {
            await ExecuteCombinationAsync(
                CreateExecutor(
                    dataSource,
                    new CombinationThrowingProbe()),
                fixture,
                Guid.NewGuid(),
                secondMaterialSlot,
                secondMaterial.ToCompactString(),
                thirdMaterialSlot,
                thirdMaterial.ToCompactString());
            throw new InvalidOperationException(
                "Combination fourth-mutation fault was not injected.");
        }
        catch (InjectedCombinationFault)
        {
            // The transaction owns the target and all three fodder changes.
        }

        Check.Equal(
            fixture.TargetState,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.TargetSlot))!.Value.Item.ToCompactString(),
            "rollback restores the Combination primary");
        Check.Equal(
            firstMaterial.ToCompactString(),
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.StoneSlot))!.Value.Item.ToCompactString(),
            "rollback restores first Combination fodder");
        Check.Equal(
            secondMaterial.ToCompactString(),
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                secondMaterialSlot))!.Value.Item.ToCompactString(),
            "rollback restores second Combination fodder");
        Check.Equal(
            thirdMaterial.ToCompactString(),
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                thirdMaterialSlot))!.Value.Item.ToCompactString(),
            "rollback restores third Combination fodder");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Combine);
        Check.True(
            state.InventoryRevision == 0 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0,
            "rollback leaves no partial durable Combination evidence");
    }

    private static async Task AssertCombinationThirdMaterialRejectionsAsync(
        string connectionString)
    {
        const short secondMaterialSlot = 18;
        const short thirdMaterialSlot = 19;
        var second = SimpleItem(9030, grade: 4);
        var third = SimpleItem(9030, grade: 4);
        var staleFixture = await CreateFixtureAsync(
            connectionString,
            "cmbstl",
            target: SimpleItem(9030, grade: 4),
            stone: SimpleItem(9030, grade: 4),
            additionalBagItems:
            [
                (secondMaterialSlot, second),
                (thirdMaterialSlot, third)
            ]);
        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        RequireReceipt(
            await ExecuteCombinationAsync(
                CreateExecutor(dataSource),
                staleFixture,
                Guid.NewGuid(),
                secondMaterialSlot,
                second.ToCompactString(),
                thirdMaterialSlot,
                (third with { Stack = 2 }).ToCompactString()),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.CombinationSelectionRequired,
            "stale third Combination material");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                staleFixture,
                HolyStoneCommandOperation.Combine),
            "stale third Combination material");

        var missingFixture = await CreateFixtureAsync(
            connectionString,
            "cmbmis",
            target: SimpleItem(9030, grade: 4),
            stone: SimpleItem(9030, grade: 4),
            additionalBagItems:
            [
                (secondMaterialSlot, second)
            ]);
        RequireReceipt(
            await ExecuteCombinationAsync(
                CreateExecutor(dataSource),
                missingFixture,
                Guid.NewGuid(),
                secondMaterialSlot,
                second.ToCompactString(),
                thirdMaterialSlot,
                third.ToCompactString()),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.CombinationSelectionRequired,
            "missing third Combination material");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                missingFixture,
                HolyStoneCommandOperation.Combine),
            "missing third Combination material");
    }

    private static async Task<HolyStoneExecutionResult>
        ExecuteCombinationAsync(
            PostgresHolyStoneCommandExecutor executor,
            HolyFixture fixture,
            Guid operationId,
            int secondMaterialSlot,
            string expectedSecondMaterial,
            int thirdMaterialSlot,
            string expectedThirdMaterial)
    {
        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                operationId,
                HolyStoneCommandOperation.Combine,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                fixture.TargetSlot,
                fixture.TargetState,
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                fixture.StoneSlot,
                fixture.StoneState,
                secondMaterialSlot,
                expectedSecondMaterial,
                thirdMaterialSlot,
                expectedThirdMaterial,
                out var command),
            "Combination fixture creates a four-role canonical command");
        return await executor.ExecuteAsync(
            PlayerOwnershipTestFences.Bind(
                HolyStoneCommandEnvelope.Create(
                    fixture.Subject,
                    new CommandConnectionCorrelation(
                        Guid.NewGuid(),
                        CommandTransportKind.SecureTlsLegacy),
                    DateTimeOffset.UtcNow,
                    command)));
    }

    private sealed class CombinationThrowingProbe :
        IPostgresHolyStoneCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresHolyStoneCommandStage stage,
            int ordinal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stage == PostgresHolyStoneCommandStage.StoneMutated &&
                ordinal == 3)
            {
                throw new InjectedCombinationFault();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedCombinationFault : Exception
    {
    }
}
