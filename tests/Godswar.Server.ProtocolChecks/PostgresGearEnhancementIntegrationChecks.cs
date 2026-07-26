using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresGearEnhancementIntegrationChecks
{
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const int GearSlot = 2;
    private const int StoneSlot = 3;
    private const int FlameSparkSlot = 4;

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL gear-enhancement integration ({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"gear_enh_{token}";
        var characterName = $"Enh{token}";
        int? accountId = null;
        int? characterId = null;

        try
        {
            await using var storeA = new PostgresGameStore(connectionString);
            await using var storeB = new PostgresGameStore(connectionString);
            await storeA.EnsureSeedDataAsync();

            var account = await storeA.LoginOrCreateAccountAsync(username, string.Empty);
            accountId = account.Id;
            var character = await storeA.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = characterName,
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 0
                });
            characterId = character.Id;
            character = await storeA.MoveEquipmentToKitBagAsync(
                    account.Id,
                    character.Id,
                    EquipmentSlots.Weapon,
                    GearSlot)
                ?? throw new InvalidOperationException(
                    "Could not move the PostgreSQL enhancement-test weapon into the bag.");
            Check.Equal(
                1000u,
                KitBagSlots.GetItem(character.KitBag, GearSlot).Id,
                "PostgreSQL enhancement-test weapon moved to the bag");

            await StageAuthoritativeRowsAsync(connectionString, character.Id);
            character = (await storeA.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            var gearBefore = KitBagSlots.GetItem(character.KitBag, GearSlot);
            var stoneBefore = KitBagSlots.GetItem(character.KitBag, StoneSlot);
            var flameBefore = KitBagSlots.GetItem(character.KitBag, FlameSparkSlot);
            var request = new GearEnhancementRequest(
                GearEnhancementOperation.Add,
                GearEnhancementSlotSelection.Capture(character.KitBag, GearSlot),
                GearEnhancementSlotSelection.Capture(character.KitBag, StoneSlot),
                GearEnhancementSlotSelection.Capture(character.KitBag, FlameSparkSlot));

            var wrongOwner = await storeA.EnhanceGearAsync(account.Id + 1, character.Id, request);
            Check.True(
                !wrongOwner.CharacterFound && wrongOwner.Enhancement is null,
                "PostgreSQL gear enhancement binds character ownership to the account");

            var raced = await Task.WhenAll(
                storeA.EnhanceGearAsync(account.Id, character.Id, request),
                storeB.EnhanceGearAsync(account.Id, character.Id, request));
            Check.Equal(
                1,
                raced.Count(static result => result.Committed),
                "only one concurrent PostgreSQL gear enhancement commits");
            Check.Equal(
                1,
                raced.Count(static result => !result.Committed),
                "duplicate PostgreSQL gear enhancement is rejected");
            var rejection = raced.Single(static result => !result.Committed);
            Check.Equal(
                (int)GearEnhancementStatus.StaleSelection,
                (int)(rejection.Enhancement?.Status
                      ?? throw new InvalidOperationException(
                          "PostgreSQL concurrent rejection omitted its result.")),
                "PostgreSQL duplicate fails authoritative snapshot revalidation");
            Check.Equal(
                0,
                rejection.Enhancement!.Mutations.Count,
                "PostgreSQL stale duplicate emits no mutations");

            var persisted = (await storeA.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.Equal(
                gearBefore with { Attribute1 = 0, AttributeLevel1 = 1 },
                KitBagSlots.GetItem(persisted.KitBag, GearSlot),
                "PostgreSQL enhancement persists the attribute while preserving gear metadata");
            Check.Equal(
                stoneBefore with { Stack = 1 },
                KitBagSlots.GetItem(persisted.KitBag, StoneSlot),
                "PostgreSQL enhancement race consumes one Attribute Stone");
            Check.Equal(
                flameBefore with { Stack = 1 },
                KitBagSlots.GetItem(persisted.KitBag, FlameSparkSlot),
                "PostgreSQL enhancement race consumes one Flame Spark");

            await using var reopenedStore = new PostgresGameStore(connectionString);
            await reopenedStore.EnsureSeedDataAsync();
            var reopened = (await reopenedStore.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.Equal(
                persisted.KitBag,
                reopened.KitBag,
                "PostgreSQL gear-enhancement mutations survive a store reopen and reseed");
        }
        finally
        {
            if (accountId.HasValue)
            {
                await PostgresIntegrationFixtureCleanup.DeleteAccountAndAuditsAsync(
                    connectionString,
                    accountId.Value,
                    username,
                    characterId,
                    "gear-enhancement-consume");
            }
        }
    }

    private static async Task StageAuthoritativeRowsAsync(
        string connectionString,
        int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var updateGear = new NpgsqlCommand("""
            UPDATE character_items
            SET attribute1 = NULL,
                attribute2 = NULL,
                attribute3 = NULL,
                attribute4 = NULL,
                attribute5 = NULL,
                attribute_level1 = NULL,
                attribute_level2 = NULL,
                attribute_level3 = NULL,
                attribute_level4 = NULL,
                attribute_level5 = NULL,
                item_quality = 20,
                item_grade = 25,
                bound = 0,
                stack = 1,
                item_exp = 321,
                holy_suit_code = 205,
                holy_socket_count = 1,
                holy_socket1_effect_id = 7,
                holy_socket1_level = 3,
                updated_at = now()
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @gearSlot
              AND prop_id = 1000;
            """, connection, transaction))
        {
            updateGear.Parameters.AddWithValue("characterId", characterId);
            updateGear.Parameters.AddWithValue("gearSlot", checked((short)GearSlot));
            Check.Equal(
                1,
                await updateGear.ExecuteNonQueryAsync(),
                "PostgreSQL enhancement-test gear metadata seeded");
        }

        await UpsertMaterialAsync(
            connection,
            transaction,
            characterId,
            StoneSlot,
            itemId: 9930,
            stack: 2);
        await UpsertMaterialAsync(
            connection,
            transaction,
            characterId,
            FlameSparkSlot,
            itemId: 9990,
            stack: 2);
        await transaction.CommitAsync();
    }

    private static async Task UpsertMaterialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int slot,
        int itemId,
        short stack)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @slotIndex, @itemId,
                1, 1, 0, @stack, 0, 0
            )
            ON CONFLICT (user_id, item_location, slot_index) DO UPDATE
            SET prop_id = EXCLUDED.prop_id,
                attribute1 = NULL,
                attribute2 = NULL,
                attribute3 = NULL,
                attribute4 = NULL,
                attribute5 = NULL,
                attribute_level1 = NULL,
                attribute_level2 = NULL,
                attribute_level3 = NULL,
                attribute_level4 = NULL,
                attribute_level5 = NULL,
                item_quality = EXCLUDED.item_quality,
                item_grade = EXCLUDED.item_grade,
                bound = EXCLUDED.bound,
                stack = EXCLUDED.stack,
                item_exp = EXCLUDED.item_exp,
                holy_suit_code = EXCLUDED.holy_suit_code,
                holy_socket_count = 0,
                holy_socket1_effect_id = NULL,
                holy_socket1_level = NULL,
                holy_socket2_effect_id = NULL,
                holy_socket2_level = NULL,
                holy_socket3_effect_id = NULL,
                holy_socket3_level = NULL,
                holy_socket4_effect_id = NULL,
                holy_socket4_level = NULL,
                holy_socket5_effect_id = NULL,
                holy_socket5_level = NULL,
                holy_socket6_effect_id = NULL,
                holy_socket6_level = NULL,
                updated_at = now();
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", checked((short)slot));
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("stack", stack);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"PostgreSQL enhancement material {itemId} staged");
    }
}
