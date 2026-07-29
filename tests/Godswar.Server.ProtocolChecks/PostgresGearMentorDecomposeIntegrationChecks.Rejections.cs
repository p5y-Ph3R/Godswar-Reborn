using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGearMentorDecomposeIntegrationChecks
{
    private static async Task AssertTerminalRejectionsAsync(
        string connectionString)
    {
        var cases =
            new (string Name, GearSpec Gear, int Level,
                GearMentorDecomposeGearResultStatus Status)[]
            {
                (
                    "level",
                    new GearSpec(4, 1004),
                    29,
                    GearMentorDecomposeGearResultStatus.PlayerLevelTooLow),
                (
                    "item",
                    new GearSpec(
                        4,
                        9900,
                        Attribute1: null),
                    80,
                    GearMentorDecomposeGearResultStatus.InvalidEquipment),
                (
                    "stack",
                    new GearSpec(4, 1004, Stack: 2),
                    80,
                    GearMentorDecomposeGearResultStatus.InvalidEquipment),
                (
                    "gearlv",
                    new GearSpec(4, 1003),
                    80,
                    GearMentorDecomposeGearResultStatus
                        .EquipmentLevelTooLow),
                (
                    "quality",
                    new GearSpec(4, 1004, Quality: 1, Grade: 1),
                    80,
                    GearMentorDecomposeGearResultStatus
                        .InsufficientEquipmentQuality),
                (
                    "suit",
                    new GearSpec(4, 1032),
                    80,
                    GearMentorDecomposeGearResultStatus.ClassSuit)
            };
        foreach (var testCase in cases)
        {
            var fixture = await CreateFixtureAsync(
                connectionString,
                testCase.Name,
                [testCase.Gear],
                testCase.Level);
            var random = new CountingRandomSource();
            await using var source =
                NpgsqlDataSource.Create(connectionString);
            var executor = CreateExecutor(source, random);
            var operationId = Guid.NewGuid();
            var rejected = RequireReceipt(
                await ExecuteAsync(executor, fixture, operationId),
                GearMentorDecomposeGearExecutionDisposition
                    .TerminalRejected,
                $"{testCase.Name} rejection");
            Check.Equal(
                (int)testCase.Status,
                (int)rejected.Status,
                $"{testCase.Name} exact terminal status");
            Check.True(
                rejected.DustOutcomes.IsEmpty &&
                rejected.InventoryRevision == 0 &&
                !rejected.OutboxEventId.HasValue,
                $"{testCase.Name} rejection carries no player value");
            Check.Equal(
                0,
                random.CallCount,
                $"{testCase.Name} rejection does not consume randomness");
            var state = await ReadStateAsync(
                connectionString,
                fixture);
            Check.True(
                state.InventoryRevision == 0 &&
                state.AuditCount == 1 &&
                state.InboxCount == 1 &&
                state.LedgerCount == 0 &&
                state.OutboxCount == 0 &&
                state.RejectedInboxCount == 1,
                $"{testCase.Name} rejection durably records only result");
            var replay = RequireReceipt(
                await executor.TryReplayAsync(
                    fixture.Subject,
                    operationId),
                GearMentorDecomposeGearExecutionDisposition.Duplicate,
                $"{testCase.Name} rejection replay");
            AssertReceiptsEqual(
                rejected,
                replay,
                $"{testCase.Name} rejection replays exactly");
            Check.Equal(
                0,
                random.CallCount,
                $"{testCase.Name} rejection replay never rerolls");
        }

        await AssertMissingAndStaleSelectionsAsync(connectionString);
    }

    private static async Task AssertMissingAndStaleSelectionsAsync(
        string connectionString)
    {
        var missing = await CreateFixtureAsync(
            connectionString,
            "missing",
            [new GearSpec(4, 1004)]);
        await ExecuteNonQueryAsync(
            connectionString,
            """
            DELETE FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = 4;
            """,
            missing.CharacterId);
        await AssertSelectionRejectionAsync(
            connectionString,
            missing,
            GearMentorDecomposeGearResultStatus.SelectionMissing,
            "missing selection");

        var stale = await CreateFixtureAsync(
            connectionString,
            "stale",
            [new GearSpec(4, 1004)]);
        await ExecuteNonQueryAsync(
            connectionString,
            """
            UPDATE public.character_items
            SET item_grade = 3
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = 4;
            """,
            stale.CharacterId);
        await AssertSelectionRejectionAsync(
            connectionString,
            stale,
            GearMentorDecomposeGearResultStatus.StaleSelection,
            "stale selection");
    }

    private static async Task AssertSelectionRejectionAsync(
        string connectionString,
        DecomposeFixture fixture,
        GearMentorDecomposeGearResultStatus expectedStatus,
        string description)
    {
        var random = new CountingRandomSource();
        await using var source =
            NpgsqlDataSource.Create(connectionString);
        var receipt = RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(source, random),
                fixture,
                Guid.NewGuid()),
            GearMentorDecomposeGearExecutionDisposition.TerminalRejected,
            description);
        Check.Equal(
            (int)expectedStatus,
            (int)receipt.Status,
            $"{description} exact status");
        Check.Equal(
            0,
            random.CallCount,
            $"{description} does not consume randomness");
        var state = await ReadStateAsync(connectionString, fixture);
        Check.True(
            state.InventoryRevision == 0 &&
            state.AuditCount == 1 &&
            state.InboxCount == 1 &&
            state.LedgerCount == 0 &&
            state.OutboxCount == 0,
            $"{description} changes no player value");
    }

    private static async Task ExecuteNonQueryAsync(
        string connectionString,
        string sql,
        int characterId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "fixture mutation affects exactly one item");
    }
}
