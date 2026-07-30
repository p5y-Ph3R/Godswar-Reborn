using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private static async Task
        AssertAuthoritativeEquipmentEligibilityAsync(
            NpgsqlDataSource dataSource)
    {
        var executor = new PostgresPetDurableCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions());
        await AssertEquipmentRejectedAsync(
            dataSource,
            executor,
            "gear_without_mount",
            bagItemId: 14508,
            expectedEquipmentSlot: EquipmentSlots.MountHead);
        await AssertEquipmentRejectedAsync(
            dataSource,
            executor,
            "mount_downgrade",
            bagItemId: 14320,
            expectedEquipmentSlot: EquipmentSlots.Mount,
            equippedItemId: 14508,
            equippedSlot: EquipmentSlots.MountHead);
        await AssertEquipmentRejectedAsync(
            dataSource,
            executor,
            "disabled_mount",
            bagItemId: checked((int)DeveloperMountCatalog.OrphanedMountItemId),
            expectedEquipmentSlot: EquipmentSlots.Mount);
        await AssertOccupiedEquipmentSwapAsync(
            dataSource,
            executor);
        await AssertRideConstraintDurabilityAsync(
            dataSource,
            executor);
    }

    private static async Task AssertRideConstraintDurabilityAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor)
    {
        const int mountItemId = 14320;
        var fixture = await CreateEquipmentFixtureAsync(
            dataSource,
            "ride_constraint",
            mountItemId,
            equippedItemId: null,
            equippedSlot: null);
        var operationId = Guid.NewGuid();
        var subject = new CommandSubject(
            fixture.AccountId,
            fixture.CharacterId);
        var correlation = new CommandConnectionCorrelation(
            Guid.NewGuid(),
            CommandTransportKind.SecureTlsLegacy);
        var blocked = BagItemActivationCommandEnvelope.Create(
            subject,
            correlation,
            DateTimeOffset.UtcNow,
            new BagItemActivationCommand(
                operationId,
                fixture.BagSlot,
                BagItemActivationExecutionConstraint
                    .RideRuntimeBlocked));
        var clearedRetry = BagItemActivationCommandEnvelope.Create(
            subject,
            correlation,
            DateTimeOffset.UtcNow.AddSeconds(1),
            new BagItemActivationCommand(
                operationId,
                fixture.BagSlot,
                BagItemActivationExecutionConstraint.None));

        var rejected = await executor.ExecuteAsync(blocked);
        var replayed = await executor.ExecuteAsync(clearedRetry);
        Check.True(
            rejected.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            replayed.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            rejected.Receipt == replayed.Receipt &&
            rejected.Receipt is
            {
                Status:
                    PetDurableReceiptStatus.EquipmentRestricted,
                EquipmentSlot: EquipmentSlots.Mount
            },
            "lost Ride rejection replays after the runtime observation clears");

        await using var command = dataSource.CreateCommand(
            """
            SELECT
                count(*) FILTER (
                    WHERE item.item_location = 1
                      AND item.slot_index = @bagSlot
                      AND item.prop_id = @mountItemId
                ),
                character.inventory_revision,
                (
                    SELECT count(*)
                    FROM public.command_inbox inbox
                    WHERE inbox.aggregate_type =
                              'character_pet_value'
                      AND inbox.aggregate_key =
                          'character:' || character.id::text
                      AND inbox.command_family = 'bag_item_activation'
                )
            FROM public.character_base character
            LEFT JOIN public.character_items item
              ON item.user_id = character.id
            WHERE character.id = @characterId
            GROUP BY character.id, character.inventory_revision;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "bagSlot",
            checked((short)fixture.BagSlot));
        command.Parameters.AddWithValue("mountItemId", mountItemId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt64(0) == 1 &&
            reader.GetInt64(1) == 0 &&
            reader.GetInt64(2) == 1,
            "Ride rejection persists once without moving inventory");
    }

    private static async Task AssertOccupiedEquipmentSwapAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor)
    {
        const int incomingShieldId = 2001;
        const int displacedShieldId = 2000;
        var fixture = await CreateEquipmentFixtureAsync(
            dataSource,
            "occupied_swap",
            incomingShieldId,
            displacedShieldId,
            EquipmentSlots.Shield);
        var envelope = BagItemActivationCommandEnvelope.Create(
            new CommandSubject(
                fixture.AccountId,
                fixture.CharacterId),
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            new BagItemActivationCommand(
                Guid.NewGuid(),
                fixture.BagSlot));
        var committed = await executor.ExecuteAsync(envelope);
        var replayed = await executor.ExecuteAsync(envelope);
        Check.True(
            committed.Disposition ==
                PetDurableExecutionDisposition.Committed &&
            replayed.Disposition ==
                PetDurableExecutionDisposition.Duplicate &&
            committed.Receipt == replayed.Receipt &&
            committed.Receipt is
            {
                Status: PetDurableReceiptStatus.EquipmentEquipped,
                EquipmentSlot: EquipmentSlots.Shield
            },
            "right-click activation replaces occupied equipment once");

        await using var command = dataSource.CreateCommand(
            """
            SELECT
                count(*) FILTER (
                    WHERE item.item_location = 0
                      AND item.slot_index = @equipmentSlot
                      AND item.prop_id = @incomingItemId
                ),
                count(*) FILTER (
                    WHERE item.item_location = 1
                      AND item.slot_index = @bagSlot
                      AND item.prop_id = @displacedItemId
                ),
                character.inventory_revision,
                (
                    SELECT count(*)
                    FROM public.character_inventory_ledger ledger
                    WHERE ledger.character_id = character.id
                      AND ledger.inventory_revision =
                          character.inventory_revision
                ),
                reconciliation.is_reconciled
            FROM public.character_base character
            JOIN public.character_inventory_reconciliation reconciliation
              ON reconciliation.character_id = character.id
            LEFT JOIN public.character_items item
              ON item.user_id = character.id
            WHERE character.id = @characterId
            GROUP BY
                character.id,
                character.inventory_revision,
                reconciliation.is_reconciled;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "equipmentSlot",
            checked((short)EquipmentSlots.Shield));
        command.Parameters.AddWithValue(
            "bagSlot",
            checked((short)fixture.BagSlot));
        command.Parameters.AddWithValue(
            "incomingItemId",
            incomingShieldId);
        command.Parameters.AddWithValue(
            "displacedItemId",
            displacedShieldId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt64(0) == 1 &&
            reader.GetInt64(1) == 1 &&
            reader.GetInt64(2) == 1 &&
            reader.GetInt64(3) == 2 &&
            reader.GetBoolean(4),
            "occupied equipment swap is atomic and reconciles two entries");
    }

    private static async Task AssertEquipmentRejectedAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor,
        string scenario,
        int bagItemId,
        int expectedEquipmentSlot,
        int? equippedItemId = null,
        int? equippedSlot = null)
    {
        var fixture = await CreateEquipmentFixtureAsync(
            dataSource,
            scenario,
            bagItemId,
            equippedItemId,
            equippedSlot);
        var envelope = BagItemActivationCommandEnvelope.Create(
            new CommandSubject(
                fixture.AccountId,
                fixture.CharacterId),
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            new BagItemActivationCommand(
                Guid.NewGuid(),
                fixture.BagSlot));
        var result = await executor.ExecuteAsync(envelope);
        Check.True(
            result.Disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
            result.Receipt is
            {
                Status:
                    PetDurableReceiptStatus.EquipmentRestricted
            } &&
            result.Receipt.EquipmentSlot == expectedEquipmentSlot,
            $"{scenario} is rejected by the shared equipment policy");

        await using var command = dataSource.CreateCommand(
            """
            SELECT
                count(*) FILTER (
                    WHERE item_location = 1
                      AND slot_index = @bagSlot
                      AND prop_id = @bagItemId
                ),
                inventory_revision
            FROM public.character_base character
            LEFT JOIN public.character_items item
              ON item.user_id = character.id
            WHERE character.id = @characterId
            GROUP BY character.inventory_revision;
            """);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue(
            "bagSlot",
            checked((short)fixture.BagSlot));
        command.Parameters.AddWithValue("bagItemId", bagItemId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt64(0) == 1 &&
            reader.GetInt64(1) == 0,
            $"{scenario} leaves inventory and its revision unchanged");
    }

    private static async Task<EquipmentEligibilityFixture>
        CreateEquipmentFixtureAsync(
            NpgsqlDataSource dataSource,
            string scenario,
            int bagItemId,
            int? equippedItemId,
            int? equippedSlot)
    {
        const int bagSlot = 90;
        var token = Guid.NewGuid().ToString("N")[..10];
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue(
                "username",
                $"b12eq_{scenario[..Math.Min(6, scenario.Length)]}_{token}");
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync());
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, name, profession, fighter_job_lv
            )
            VALUES (@accountId, @name, 0, 120)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue(
                "name",
                $"B12Eq{token}");
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync());
        }

        await InsertEquipmentFixtureItemAsync(
            connection,
            transaction,
            characterId,
            bagItemId,
            itemLocation: 1,
            bagSlot);
        if (equippedItemId.HasValue && equippedSlot.HasValue)
        {
            await InsertEquipmentFixtureItemAsync(
                connection,
                transaction,
                characterId,
                equippedItemId.Value,
                itemLocation: 0,
                equippedSlot.Value);
        }

        await transaction.CommitAsync();
        return new EquipmentEligibilityFixture(
            accountId,
            characterId,
            bagSlot);
    }

    private static async Task InsertEquipmentFixtureItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int itemId,
        short itemLocation,
        int slot)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack,
                item_exp, holy_suit_code
            )
            VALUES (
                @characterId, @location, @slot, @itemId,
                1, 1, 1, 1, 0, 0
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "characterId",
            characterId);
        command.Parameters.AddWithValue("location", itemLocation);
        command.Parameters.AddWithValue("slot", checked((short)slot));
        command.Parameters.AddWithValue("itemId", itemId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "equipment eligibility fixture inserts one item");
    }

    private readonly record struct EquipmentEligibilityFixture(
        int AccountId,
        int CharacterId,
        int BagSlot);
}
