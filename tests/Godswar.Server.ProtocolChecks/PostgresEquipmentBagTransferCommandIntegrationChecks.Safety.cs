using Godswar.Server.Application.Items;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresEquipmentBagTransferCommandIntegrationChecks
{
    private static async Task
        AssertEligibilityAndTerminalSafetyAsync(
            string connectionString)
    {
        await AssertBothOccupiedAsync(connectionString);
        await AssertStaleStatesAsync(connectionString);
        await AssertAuthoritativeEligibilityAsync(connectionString);
        await AssertMountDependencyAsync(connectionString);
        await AssertRideRuntimeBlockedAsync(connectionString);
        await AssertInvalidRideObservationAsync(connectionString);
        await AssertUnsupportedMountAsync(connectionString);
        await AssertReservedSlotReplayAsync(connectionString);
        await AssertPhysicalEmptyRowFailsClosedAsync(connectionString);
    }

    private static async Task AssertBothOccupiedAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "occupied",
            equipmentItem: Item(1007),
            kitBagItem: Item(1006));
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid()),
            EquipmentBagTransferDisposition.TerminalRejected,
            EquipmentBagTransferResultStatus.BothOccupied,
            "both occupied");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        AssertUnchangedTerminalState(
            state,
            fixture.EquipmentItemId!.Value,
            fixture.KitBagItemId!.Value,
            "both occupied");
    }

    private static async Task AssertAuthoritativeEligibilityAsync(
        string connectionString)
    {
        await AssertRejectedEquipAsync(
            connectionString,
            "kind",
            Item(4212),
            equipmentSlot: 10,
            characterLevel: 80,
            EquipmentBagTransferResultStatus.ItemNotEquipment);
        await AssertRejectedEquipAsync(
            connectionString,
            "slot",
            Item(1007),
            equipmentSlot: 3,
            characterLevel: 80,
            EquipmentBagTransferResultStatus.WrongEquipmentSlot);
        await AssertRejectedEquipAsync(
            connectionString,
            "class",
            Item(1400),
            equipmentSlot: 10,
            characterLevel: 80,
            EquipmentBagTransferResultStatus.ProfessionRestricted);
        await AssertRejectedEquipAsync(
            connectionString,
            "level",
            Item(1008),
            equipmentSlot: 10,
            characterLevel: 80,
            EquipmentBagTransferResultStatus.LevelRestricted);
    }

    private static async Task AssertStaleStatesAsync(
        string connectionString)
    {
        var equipmentStale = await CreateFixtureAsync(
            connectionString,
            "staleq",
            kitBagItem: Item(1007));
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        var equipmentReceipt = RequireReceipt(
            await ExecuteAsync(
                executor,
                equipmentStale,
                Guid.NewGuid(),
                expectedEquipment: Item(1006).ToCompactString(),
                expectedKitBag: "[]"),
            EquipmentBagTransferDisposition.TerminalRejected,
            EquipmentBagTransferResultStatus.StaleEquipment,
            "stale equipment");
        Check.True(
            equipmentReceipt.AuthoritativeEquipmentCompactItemState ==
                "[]" &&
            equipmentReceipt.AuthoritativeKitBagCompactItemState ==
                equipmentStale.KitBagState,
            "equipment mismatch wins and returns both locked states");
        AssertUnchangedTerminalState(
            await ReadStateAsync(connectionString, equipmentStale),
            0,
            equipmentStale.KitBagItemId!.Value,
            "stale equipment");

        var kitBagStale = await CreateFixtureAsync(
            connectionString,
            "staleb",
            kitBagItem: Item(1007));
        var kitBagReceipt = RequireReceipt(
            await ExecuteAsync(
                executor,
                kitBagStale,
                Guid.NewGuid(),
                expectedKitBag: "[]"),
            EquipmentBagTransferDisposition.TerminalRejected,
            EquipmentBagTransferResultStatus.StaleKitBag,
            "stale kit bag");
        Check.True(
            kitBagReceipt.AuthoritativeKitBagCompactItemState ==
                kitBagStale.KitBagState,
            "stale bag receipt returns authoritative bag state");
        AssertUnchangedTerminalState(
            await ReadStateAsync(connectionString, kitBagStale),
            0,
            kitBagStale.KitBagItemId!.Value,
            "stale kit bag");
    }

    private static async Task AssertRejectedEquipAsync(
        string connectionString,
        string scenario,
        Godswar.Server.State.CompactItemEntry item,
        short equipmentSlot,
        int characterLevel,
        EquipmentBagTransferResultStatus status)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            scenario,
            kitBagItem: item,
            equipmentSlot: equipmentSlot,
            characterLevel: characterLevel);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid()),
            EquipmentBagTransferDisposition.TerminalRejected,
            status,
            scenario);
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        AssertUnchangedTerminalState(
            state,
            0,
            fixture.KitBagItemId!.Value,
            scenario);
    }

    private static async Task AssertMountDependencyAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "mountdep",
            equipmentItem: Item(6000),
            equipmentSlot: 20,
            additionalEquipment:
            [
                (15, Item(14500))
            ]);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                fixture,
                Guid.NewGuid()),
            EquipmentBagTransferDisposition.TerminalRejected,
            EquipmentBagTransferResultStatus.MountDependencyBlocked,
            "mount dependency");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        AssertUnchangedTerminalState(
            state,
            fixture.EquipmentItemId!.Value,
            0,
            "mount dependency");

        var noMount = await CreateFixtureAsync(
            connectionString,
            "nogear",
            kitBagItem: Item(14500),
            equipmentSlot: 15);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                noMount,
                Guid.NewGuid()),
            EquipmentBagTransferDisposition.TerminalRejected,
            EquipmentBagTransferResultStatus.MountDependencyBlocked,
            "mount gear without mount");

        var insufficient = await CreateFixtureAsync(
            connectionString,
            "mountlv",
            kitBagItem: Item(14501),
            equipmentSlot: 15,
            additionalEquipment:
            [
                (20, Item(6000))
            ]);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(dataSource),
                insufficient,
                Guid.NewGuid()),
            EquipmentBagTransferDisposition.TerminalRejected,
            EquipmentBagTransferResultStatus.MountDependencyBlocked,
            "mount level is insufficient for gear");
    }

    private static async Task AssertUnsupportedMountAsync(
        string connectionString)
    {
        const int unsupportedMountId = 199_990;
        await using (var connection =
                     new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO public.item_templates (
                    id,
                    kind,
                    name_key,
                    display_name,
                    equipment_slot,
                    class_ids,
                    min_level,
                    max_level,
                    stats
                )
                VALUES (
                    @id,
                    'mount',
                    'UnsupportedTestMount',
                    'Unsupported Test Mount',
                    20,
                    ARRAY[0, 1, 2, 3]::smallint[],
                    1,
                    200,
                    '{}'::jsonb
                )
                ON CONFLICT (id) DO NOTHING;
                """,
                connection);
            command.Parameters.AddWithValue("id", unsupportedMountId);
            await command.ExecuteNonQueryAsync();
        }

        var fixture = await CreateFixtureAsync(
            connectionString,
            "unsupp",
            kitBagItem: Item(unsupportedMountId),
            equipmentSlot: 20);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        RequireReceipt(
            await ExecuteAsync(
                CreateExecutor(
                    dataSource,
                    itemContent: CreateUnsupportedMountContent(
                        unsupportedMountId)),
                fixture,
                Guid.NewGuid()),
            EquipmentBagTransferDisposition.TerminalRejected,
            EquipmentBagTransferResultStatus.MountUnsupported,
            "unsupported mount");
        AssertUnchangedTerminalState(
            await ReadStateAsync(connectionString, fixture),
            0,
            fixture.KitBagItemId!.Value,
            "unsupported mount");
    }

    private static GameplayItemContent CreateUnsupportedMountContent(
        uint itemId)
    {
        var baseline = TestItemContent.Catalog;
        var materials = baseline.Materials;
        var definitions = baseline.All.Append(
            new ItemTemplateDefinition(
                itemId,
                "mount",
                "UnsupportedTestMount",
                "Unsupported Test Mount",
                20,
                Array.AsReadOnly<short>([0, 1, 2, 3]),
                1,
                200,
                null,
                null,
                string.Empty,
                string.Empty,
                "{}"))
            .ToArray();
        return new GameplayItemContent(
            PinnedItemTemplateCatalog.Create(
                "protocol-check-unsupported-mount",
                definitions,
                baseline.Attributes,
                baseline.EquipmentRanks,
                baseline.HolySuitEffects,
                materials.ForgingMaterials,
                materials.GearEnhancementMaterials,
                materials.AttributeDusts,
                materials.GearMentorRecipes));
    }

    private static async Task AssertReservedSlotReplayAsync(
        string connectionString)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            "reserve",
            kitBagItem: Item(1007),
            equipmentSlot: 13);
        var operationId = Guid.NewGuid();
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var executor = CreateExecutor(dataSource);
        RequireReceipt(
            await ExecuteAsync(executor, fixture, operationId),
            EquipmentBagTransferDisposition.TerminalRejected,
            EquipmentBagTransferResultStatus.WrongEquipmentSlot,
            "reserved slot");
        RequireReceipt(
            await executor.TryReplayAsync(
                fixture.Subject,
                PlayerOwnershipTestFences.ForCharacter(
                    fixture.Subject.CharacterId),
                operationId,
                fixture.EquipmentSlot,
                fixture.KitBagSlot),
            EquipmentBagTransferDisposition.Duplicate,
            EquipmentBagTransferResultStatus.WrongEquipmentSlot,
            "reserved slot replay");
        var state = await ReadStateAsync(
            connectionString,
            fixture);
        Check.Equal(
            1,
            state.DuplicateCount,
            "reserved-slot receipt is replayable");
    }

    private static void AssertUnchangedTerminalState(
        TransferDurableState state,
        long equipmentId,
        long kitBagId,
        string description)
    {
        Check.Equal(
            0L,
            state.InventoryRevision,
            $"{description} has no revision");
        Check.Equal(
            equipmentId,
            state.EquipmentItemId,
            $"{description} preserves equipment");
        Check.Equal(
            kitBagId,
            state.KitBagItemId,
            $"{description} preserves bag");
        Check.Equal(
            1L,
            state.AuditCount,
            $"{description} durable audit");
        Check.Equal(
            1L,
            state.InboxCount,
            $"{description} durable inbox");
        Check.Equal(
            0L,
            state.CompatibilityAuditCount,
            $"{description} no compatibility mutation audit");
        Check.Equal(
            0L,
            state.LedgerCount,
            $"{description} no ledger");
        Check.Equal(
            0L,
            state.OutboxCount,
            $"{description} no outbox");
    }
}
