using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolyStoneCommandIntegrationChecks
{
    private static async Task AssertUpgradeTransactionsAsync(
        string connectionString)
    {
        await AssertGoddessFailureAndReplayAsync(connectionString);
        await AssertSignetProtectionAsync(connectionString);
        await AssertWrongSignetRejectsBeforeRollAsync(connectionString);
        await AssertHighLevelSignetRejectsBeforeRollAsync(
            connectionString);
        await AssertMissingEclipseResultAsync(connectionString);
        await AssertWrongEclipseTierResultsAsync(connectionString);
        await AssertUpgradeAtomicRollbackAsync(connectionString);
    }

    private static async Task AssertGoddessFailureAndReplayAsync(
        string connectionString)
    {
        const short catalystSlot = 11;
        var goddess = SimpleItem(9050, stack: 2);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "upgodd",
            target: SimpleItem(9031, grade: 8),
            stone: SimpleItem(9042, stack: 2),
            additionalBagItems: [(catalystSlot, goddess)]);
        var operationId = Guid.NewGuid();
        var random = new FixedUpgradeRandomSource(20);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(
            dataSource,
            upgradeRandomSource: random);
        var committed = RequireReceipt(
            await ExecuteUpgradeAsync(
                executor,
                fixture,
                operationId,
                catalystSlot,
                goddess.ToCompactString()),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.UpgradeFailedDowngraded,
            "Goddess failure");
        var replay = RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                PlayerOwnershipTestFences.ForCharacter(
                    fixture.Subject.CharacterId),
                HolyStoneCommandOperation.Upgrade,
                operationId),
            HolyStoneExecutionDisposition.Duplicate,
            HolyStoneCommandResultStatus.UpgradeFailedDowngraded,
            "Goddess failure replay");
        Check.Equal(committed, replay, "upgrade replay returns stored outcome");
        Check.Equal(1, random.CallCount, "upgrade replay never resamples RNG");

        var target = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.TargetSlot))!.Value.Item;
        var eclipse = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.StoneSlot))!.Value.Item;
        var catalyst = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            catalystSlot))!.Value.Item;
        Check.True(
            target.Grade == 7 &&
            eclipse.Stack == 1 &&
            catalyst.Stack == 1,
            "failed Goddess attempt atomically downgrades and consumes once");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Upgrade);
        Check.True(
            state.InventoryRevision == 1 &&
            state.LedgerCount == 3 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.OutboxCount == 1 &&
            state.DuplicateCount == 1,
            "upgrade persists one audit/inbox/ledger/outbox transition");
        var audit = await ReadUpgradeAuditAsync(
            connectionString,
            fixture);
        Check.True(
            audit.Roll == 20 &&
            audit.Rate == 20 &&
            audit.CatalystSlot == catalystSlot,
            "upgrade audit persists roll, effective rate, and catalyst");
    }

    private static async Task AssertSignetProtectionAsync(
        string connectionString)
    {
        const short catalystSlot = 12;
        var fixture = await CreateFixtureAsync(
            connectionString,
            "upsign",
            target: SimpleItem(9030, grade: 6),
            stone: SimpleItem(9041),
            additionalBagItems:
            [
                (catalystSlot, SimpleItem(9053))
            ]);
        var random = new FixedUpgradeRandomSource(35);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteUpgradeAsync(
                CreateExecutor(
                    dataSource,
                    upgradeRandomSource: random),
                fixture,
                Guid.NewGuid(),
                catalystSlot,
                SimpleItem(9053).ToCompactString()),
            HolyStoneExecutionDisposition.Committed,
            HolyStoneCommandResultStatus.UpgradeFailedProtected,
            "exact signet failure");
        var target = (await ReadItemAsync(
            connectionString,
            fixture.CharacterId,
            1,
            fixture.TargetSlot))!.Value.Item;
        Check.Equal(6, target.Grade, "exact signet prevents downgrade");
        Check.True(
            await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.StoneSlot) is null &&
            await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                catalystSlot) is null,
            "protected failure consumes Eclipse and signet atomically");
        AssertCommittedEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Upgrade),
            expectedLedger: 3,
            "protected upgrade failure");
    }

    private static async Task AssertWrongSignetRejectsBeforeRollAsync(
        string connectionString)
    {
        const short catalystSlot = 13;
        var catalyst = SimpleItem(9054);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "upwrong",
            target: SimpleItem(9030, grade: 6),
            stone: SimpleItem(9041),
            additionalBagItems: [(catalystSlot, catalyst)]);
        var random = new FixedUpgradeRandomSource(0);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteUpgradeAsync(
                CreateExecutor(
                    dataSource,
                    upgradeRandomSource: random),
                fixture,
                Guid.NewGuid(),
                catalystSlot,
                catalyst.ToCompactString()),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.SignetMismatch,
            "wrong transition signet");
        Check.Equal(0, random.CallCount, "invalid signet consumes no RNG");
        Check.Equal(
            fixture.TargetState,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.TargetSlot))!.Value.Item.ToCompactString(),
            "wrong signet leaves target unchanged");
        AssertTerminalEvidence(
            await ReadStateAsync(
                connectionString,
                fixture,
                HolyStoneCommandOperation.Upgrade),
            "wrong transition signet");
    }

    private static async Task AssertMissingEclipseResultAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "upmiss",
            target: SimpleItem(9030, grade: 5),
            stone: null);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteUpgradeAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid(),
                HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                "[]",
                eclipseSlot: fixture.StoneSlot,
                expectedEclipse: "[]"),
            HolyStoneExecutionDisposition.TerminalRejected,
            HolyStoneCommandResultStatus.EclipseLevel2Missing,
            "missing Level 2 Eclipse Stone");
        Check.Equal(
            HolyStoneNativeResults.EclipseLevel2MissingSubId,
            receipt.NativeResultSubId,
            "missing Eclipse response identifies the required tier");
    }

    private static async Task AssertWrongEclipseTierResultsAsync(
        string connectionString)
    {
        var cases = new[]
        {
            (
                Name: "upel1",
                TargetGrade: (short)2,
                SuppliedEclipse: 9041u,
                ExpectedStatus:
                    HolyStoneCommandResultStatus.EclipseLevel1Missing,
                ExpectedSubId:
                    HolyStoneNativeResults.EclipseLevel1MissingSubId),
            (
                Name: "upel2",
                TargetGrade: (short)4,
                SuppliedEclipse: 9040u,
                ExpectedStatus:
                    HolyStoneCommandResultStatus.EclipseLevel2Missing,
                ExpectedSubId:
                    HolyStoneNativeResults.EclipseLevel2MissingSubId),
            (
                Name: "upel3",
                TargetGrade: (short)7,
                SuppliedEclipse: 9041u,
                ExpectedStatus:
                    HolyStoneCommandResultStatus.EclipseLevel3Missing,
                ExpectedSubId:
                    HolyStoneNativeResults.EclipseLevel3MissingSubId)
        };

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        foreach (var testCase in cases)
        {
            var fixture = await CreateFixtureAsync(
                connectionString,
                testCase.Name,
                target: SimpleItem(
                    9030,
                    grade: testCase.TargetGrade),
                stone: SimpleItem(testCase.SuppliedEclipse));
            var random = new FixedUpgradeRandomSource(0);
            var receipt = RequireReceipt(
                await ExecuteUpgradeAsync(
                    CreateExecutor(
                        dataSource,
                        upgradeRandomSource: random),
                    fixture,
                    Guid.NewGuid(),
                    HolyStoneCommandEnvelope.NoStoneKitBagSlot,
                    "[]"),
                HolyStoneExecutionDisposition.TerminalRejected,
                testCase.ExpectedStatus,
                $"wrong Eclipse tier for level {testCase.TargetGrade}");

            Check.Equal(
                testCase.ExpectedSubId,
                receipt.NativeResultSubId,
                "wrong Eclipse tier identifies the required level");
            Check.Equal(
                0,
                random.CallCount,
                "wrong Eclipse tier consumes no RNG");
            Check.Equal(
                fixture.TargetState,
                (await ReadItemAsync(
                    connectionString,
                    fixture.CharacterId,
                    1,
                    fixture.TargetSlot))!.Value.Item.ToCompactString(),
                "wrong Eclipse tier leaves target unchanged");
            Check.Equal(
                fixture.StoneState,
                (await ReadItemAsync(
                    connectionString,
                    fixture.CharacterId,
                    1,
                    fixture.StoneSlot))!.Value.Item.ToCompactString(),
                "wrong Eclipse tier leaves material unchanged");
            AssertTerminalEvidence(
                await ReadStateAsync(
                    connectionString,
                    fixture,
                    HolyStoneCommandOperation.Upgrade),
                "wrong Eclipse tier");
        }
    }

    private static async Task AssertUpgradeAtomicRollbackAsync(
        string connectionString)
    {
        const short catalystSlot = 14;
        var catalyst = SimpleItem(9050, stack: 2);
        var fixture = await CreateFixtureAsync(
            connectionString,
            "uproll",
            target: SimpleItem(9030, grade: 4),
            stone: SimpleItem(9041, stack: 2),
            additionalBagItems: [(catalystSlot, catalyst)]);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await ExecuteUpgradeAsync(
                CreateExecutor(
                    dataSource,
                    new OrdinalThrowingProbe(
                        PostgresHolyStoneCommandStage.StoneMutated,
                        2),
                    new FixedUpgradeRandomSource(0)),
                fixture,
                Guid.NewGuid(),
                catalystSlot,
                catalyst.ToCompactString());
            throw new InvalidOperationException(
                "Upgrade catalyst fault was not injected.");
        }
        catch (InjectedUpgradeFault)
        {
            // The transaction owns all three item changes and durable rows.
        }

        Check.Equal(
            fixture.TargetState,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.TargetSlot))!.Value.Item.ToCompactString(),
            "rollback restores the target level");
        Check.Equal(
            fixture.StoneState,
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                fixture.StoneSlot))!.Value.Item.ToCompactString(),
            "rollback restores the Eclipse stack");
        Check.Equal(
            catalyst.ToCompactString(),
            (await ReadItemAsync(
                connectionString,
                fixture.CharacterId,
                1,
                catalystSlot))!.Value.Item.ToCompactString(),
            "rollback restores the catalyst stack");
        var state = await ReadStateAsync(
            connectionString,
            fixture,
            HolyStoneCommandOperation.Upgrade);
        Check.True(
            state.InventoryRevision == 0 &&
            state.AuditCount == 0 &&
            state.InboxCount == 0 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0,
            "rollback leaves no partial durable Upgrade evidence");
    }

    private static async Task<HolyStoneExecutionResult> ExecuteUpgradeAsync(
        PostgresHolyStoneCommandExecutor executor,
        HolyFixture fixture,
        Guid operationId,
        int catalystSlot,
        string expectedCatalyst,
        int? eclipseSlot = null,
        string? expectedEclipse = null)
    {
        Check.True(
            HolyStoneCommandEnvelope.TryCreateCommand(
                operationId,
                HolyStoneCommandOperation.Upgrade,
                HolyStoneCommandEnvelope.SpartaNpcId,
                HolyStoneCommandEnvelope.DialogIndex,
                HolyStoneTargetLocation.KitBag,
                fixture.TargetSlot,
                fixture.TargetState,
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
                eclipseSlot ?? fixture.StoneSlot,
                expectedEclipse ?? fixture.StoneState,
                catalystSlot,
                expectedCatalyst,
                out var command),
            "upgrade fixture creates a bounded canonical command");
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

    private static async Task<(int Roll, int Rate, int CatalystSlot)>
        ReadUpgradeAuditAsync(
            string connectionString,
            HolyFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (detail_payload->>'upgradeRoll')::integer,
                (detail_payload->>'upgradeSuccessRate')::integer,
                (detail_payload->>'catalystSlot')::integer
            FROM public.command_audit
            WHERE principal_key = @principalKey
              AND aggregate_key = @aggregateKey
              AND command_family = 'holy_stone_upgrade';
            """, connection);
        command.Parameters.AddWithValue(
            "principalKey",
            fixture.AccountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            HolyStonePersistenceCodec.AggregateKey(fixture.CharacterId));
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "upgrade audit row exists");
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private sealed class FixedUpgradeRandomSource(int roll) :
        IHolyStoneUpgradeRandomSource
    {
        public int CallCount { get; private set; }

        public int NextRoll()
        {
            CallCount++;
            return roll;
        }
    }

    private sealed class OrdinalThrowingProbe(
        PostgresHolyStoneCommandStage stage,
        int ordinal) : IPostgresHolyStoneCommandProbe
    {
        public ValueTask ReachedAsync(
            PostgresHolyStoneCommandStage reachedStage,
            int reachedOrdinal,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachedStage == stage && reachedOrdinal == ordinal)
            {
                throw new InjectedUpgradeFault();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InjectedUpgradeFault : Exception
    {
    }
}
