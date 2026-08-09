using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private const int PermanentCostumeItemId = 8068;

    private static async Task AssertStylishEquipmentAsync(
        NpgsqlDataSource dataSource,
        PostgresPetDurableCommandExecutor executor)
    {
        var fixture = await CreateEquipmentFixtureAsync(
            dataSource,
            "fashion",
            PermanentCostumeItemId,
            equippedItemId: null,
            equippedSlot: null);
        var originalItemInstanceId = await ReadFashionItemInstanceIdAsync(
            dataSource,
            fixture.CharacterId,
            itemLocation: 1,
            fixture.BagSlot);
        var envelope = PlayerOwnershipTestFences.Bind(
            BagItemActivationCommandEnvelope.Create(
                new CommandSubject(
                    fixture.AccountId,
                    fixture.CharacterId),
                new CommandConnectionCorrelation(
                    Guid.NewGuid(),
                    CommandTransportKind.SecureTlsLegacy),
                DateTimeOffset.UtcNow,
                new BagItemActivationCommand(
                    Guid.NewGuid(),
                    fixture.BagSlot)));

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
                EquipmentSlot: EquipmentSlots.Stylish
            },
            "right-click activation equips permanent Fashion once");

        var equippedItemInstanceId = await ReadFashionItemInstanceIdAsync(
            dataSource,
            fixture.CharacterId,
            itemLocation: 0,
            EquipmentSlots.Stylish);
        await using var state = dataSource.CreateCommand(
            """
            SELECT
                character.inventory_revision,
                count(item.id) FILTER (
                    WHERE item.prop_id = @itemId),
                count(item.id) FILTER (
                    WHERE item.item_location = 1
                      AND item.slot_index = @bagSlot
                      AND item.prop_id = @itemId),
                reconciliation.is_reconciled
            FROM public.character_base character
            JOIN public.character_inventory_reconciliation reconciliation
              ON reconciliation.character_id = character.id
            LEFT JOIN public.character_items item
              ON item.user_id = character.id
            WHERE character.id = @characterId
            GROUP BY
                character.inventory_revision,
                reconciliation.is_reconciled;
            """);
        state.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        state.Parameters.AddWithValue("itemId", PermanentCostumeItemId);
        state.Parameters.AddWithValue(
            "bagSlot",
            checked((short)fixture.BagSlot));
        await using var reader = await state.ExecuteReaderAsync();
        Check.True(
            originalItemInstanceId == equippedItemInstanceId &&
            await reader.ReadAsync() &&
            reader.GetInt64(0) == 1 &&
            reader.GetInt64(1) == 1 &&
            reader.GetInt64(2) == 0 &&
            reader.GetBoolean(3) &&
            !await reader.ReadAsync(),
            "right-click Fashion equip preserves identity, removes the bag " +
            "copy, advances one revision, and reconciles");
    }

    private static async Task<long> ReadFashionItemInstanceIdAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        short itemLocation,
        int slot)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = @itemLocation
              AND slot_index = @slot
              AND prop_id = @itemId;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemLocation", itemLocation);
        command.Parameters.AddWithValue("slot", checked((short)slot));
        command.Parameters.AddWithValue("itemId", PermanentCostumeItemId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync() ??
            throw new InvalidDataException(
                "The Fashion item instance was not found."));
    }
}
