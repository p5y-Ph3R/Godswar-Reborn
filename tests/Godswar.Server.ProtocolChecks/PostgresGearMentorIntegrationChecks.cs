using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresGearMentorIntegrationChecks
{
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const int FillerSlotA = 0;
    private const int FillerSlotB = 1;
    private const int PreservedGearSlot = 2;
    private const int RecipeSlot = 3;
    private const int PiecesSlot = 4;
    private const int SingleConnectionRecipeSlot = 5;

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL Gear Mentor integration ({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"gear_mentor_{token}";
        var characterName = $"Mentor{token}";
        int? accountId = null;

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
            character = await storeA.MoveEquipmentToKitBagAsync(
                    account.Id,
                    character.Id,
                    EquipmentSlots.Weapon,
                    PreservedGearSlot)
                ?? throw new InvalidOperationException(
                    "Could not move the PostgreSQL Gear Mentor test weapon into the bag.");
            Check.Equal(
                1000u,
                KitBagSlots.GetItem(character.KitBag, PreservedGearSlot).Id,
                "PostgreSQL Gear Mentor test weapon moved to the bag");

            await StageAuthoritativeRowsAsync(connectionString, character.Id);
            character = (await storeA.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            var gearBefore = KitBagSlots.GetItem(character.KitBag, PreservedGearSlot);
            var gearRowBefore = await ReadItemRowAsync(
                connectionString,
                character.Id,
                PreservedGearSlot);
            var dustBefore = KitBagSlots.GetItem(character.KitBag, RecipeSlot);
            Check.Equal(9900u, dustBefore.Id, "PostgreSQL Gear Mentor test Strength Dust staged");
            Check.Equal((short)99, dustBefore.Stack, "PostgreSQL Gear Mentor recipe starts with 99 Dust");
            Check.Equal((short)1, dustBefore.Bound, "PostgreSQL Gear Mentor test Dust is bound");

            var request = new GearMentorRequest(
                GearMentorOperation.MakeAttributeStone,
                [GearMentorSlotSelection.Capture(character.KitBag, RecipeSlot)]);

            var wrongOwner = await storeA.ProcessGearMentorAsync(
                account.Id + 1,
                character.Id,
                request);
            Check.True(
                !wrongOwner.CharacterFound && wrongOwner.Result is null,
                "PostgreSQL Gear Mentor binds character ownership to the account");

            var raced = await Task.WhenAll(
                storeA.ProcessGearMentorAsync(account.Id, character.Id, request),
                storeB.ProcessGearMentorAsync(account.Id, character.Id, request));
            Check.Equal(
                1,
                raced.Count(static result => result.Committed),
                "only one concurrent PostgreSQL Gear Mentor recipe commits");
            Check.Equal(
                1,
                raced.Count(static result => !result.Committed),
                "duplicate PostgreSQL Gear Mentor recipe is rejected");

            var committed = raced.Single(static result => result.Committed).Result
                ?? throw new InvalidOperationException(
                    "Committed PostgreSQL Gear Mentor transaction omitted its result.");
            Check.Equal(
                (int)GearMentorStatus.Succeeded,
                (int)committed.Status,
                "PostgreSQL Dust-to-Stone recipe succeeds");
            Check.Equal(
                new GearMentorOutput(9930, 1, 1),
                committed.Outputs.Single(),
                "99 bound Strength Dust produce one bound Strength Stone");

            var rejection = raced.Single(static result => !result.Committed).Result
                ?? throw new InvalidOperationException(
                    "Rejected PostgreSQL Gear Mentor transaction omitted its result.");
            Check.Equal(
                (int)GearMentorStatus.StaleSelection,
                (int)rejection.Status,
                "PostgreSQL duplicate fails authoritative snapshot revalidation");
            Check.Equal(
                0,
                rejection.Mutations.Count,
                "PostgreSQL stale Gear Mentor duplicate emits no mutations");

            var persisted = (await storeA.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.Equal(
                CompactItemEntry.Empty with
                {
                    Id = 9930,
                    Quality = 1,
                    Grade = 1,
                    Bound = 1,
                    Stack = 1
                },
                KitBagSlots.GetItem(persisted.KitBag, RecipeSlot),
                "PostgreSQL Gear Mentor race consumes Dust once and persists one Strength Stone");
            Check.Equal(
                gearBefore,
                KitBagSlots.GetItem(persisted.KitBag, PreservedGearSlot),
                "PostgreSQL Gear Mentor recipe preserves unrelated Q20/G25 gear metadata");
            Check.Equal(
                gearRowBefore,
                await ReadItemRowAsync(connectionString, character.Id, PreservedGearSlot),
                "PostgreSQL Gear Mentor recipe leaves the unrelated authoritative gear row byte-for-byte stable");

            await StageMaterialAsync(
                connectionString,
                character.Id,
                PiecesSlot,
                itemId: 4216,
                stack: 99,
                bound: 1);
            persisted = (await storeA.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            var combine = await storeA.ProcessGearMentorAsync(
                account.Id,
                character.Id,
                new GearMentorRequest(
                    GearMentorOperation.CombineGemPieces,
                    [GearMentorSlotSelection.Capture(persisted.KitBag, PiecesSlot)]));
            Check.True(combine.Committed, "PostgreSQL Level-5 gem-piece combination commits");
            Check.Equal(
                new GearMentorOutput(4215, 1, 1),
                combine.Result!.Outputs.Single(),
                "99 bound Level-5 Sapphire Pieces produce one bound Level-5 Sapphire");
            Check.Equal(
                4215u,
                KitBagSlots.GetItem(combine.Character!.KitBag, PiecesSlot).Id,
                "PostgreSQL Level-5 Sapphire output replaces its consumed piece stack");
            Check.Equal(
                gearRowBefore,
                await ReadItemRowAsync(connectionString, character.Id, PreservedGearSlot),
                "a second Gear Mentor recipe still preserves unrelated high-ceiling gear metadata");

            await StageMaterialAsync(
                connectionString,
                character.Id,
                SingleConnectionRecipeSlot,
                itemId: 9901,
                stack: 99,
                bound: 0);
            await using (var singleConnectionStore = new PostgresGameStore(
                             CreateSingleConnectionPoolString(connectionString)))
            {
                // A pool capped at one connection deterministically catches a
                // post-commit readback that tries to lease a second connection
                // while the transaction connection is still owned by this call.
                var singleConnectionCharacter =
                    (await singleConnectionStore.GetCharactersAsync(account.Id))
                    .Single(candidate => candidate.Id == character.Id);
                var singleConnectionResult = await singleConnectionStore.ProcessGearMentorAsync(
                    account.Id,
                    character.Id,
                    new GearMentorRequest(
                        GearMentorOperation.MakeAttributeStone,
                        [GearMentorSlotSelection.Capture(
                            singleConnectionCharacter.KitBag,
                            SingleConnectionRecipeSlot)]));

                Check.True(
                    singleConnectionResult.Committed,
                    "single-connection PostgreSQL Gear Mentor recipe commits");
                Check.Equal(
                    9931u,
                    KitBagSlots.GetItem(
                        singleConnectionResult.Character!.KitBag,
                        SingleConnectionRecipeSlot).Id,
                    "single-connection transaction returns the committed Shield Stone bag refresh");
            }

            await using var reopenedStore = new PostgresGameStore(connectionString);
            await reopenedStore.EnsureSeedDataAsync();
            var reopened = (await reopenedStore.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.Equal(
                9930u,
                KitBagSlots.GetItem(reopened.KitBag, RecipeSlot).Id,
                "PostgreSQL Strength Stone survives a store reopen and reseed");
            Check.Equal(
                4215u,
                KitBagSlots.GetItem(reopened.KitBag, PiecesSlot).Id,
                "PostgreSQL Level-5 Sapphire survives a store reopen and reseed");
            Check.Equal(
                9931u,
                KitBagSlots.GetItem(reopened.KitBag, SingleConnectionRecipeSlot).Id,
                "single-connection PostgreSQL Shield Stone survives a store reopen");
            Check.Equal(
                gearBefore,
                KitBagSlots.GetItem(reopened.KitBag, PreservedGearSlot),
                "Q20/G25 gear metadata survives Gear Mentor operations and a store reopen");
        }
        finally
        {
            if (accountId.HasValue)
            {
                await DeleteTestAccountAsync(connectionString, accountId.Value, username);
            }
        }
    }

    private static string CreateSingleConnectionPoolString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 1,
            Timeout = 2,
            ApplicationName = $"gear-mentor-readback-{Guid.NewGuid():N}"
        };
        return builder.ConnectionString;
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
            SET attribute1 = 0,
                attribute2 = 10,
                attribute3 = 20,
                attribute4 = 18,
                attribute5 = 16,
                attribute_level1 = 25,
                attribute_level2 = 24,
                attribute_level3 = 23,
                attribute_level4 = 22,
                attribute_level5 = 21,
                item_quality = 20,
                item_grade = 25,
                bound = 1,
                stack = 1,
                item_exp = 2147000000,
                holy_suit_code = 710,
                holy_socket_count = 4,
                holy_socket1_effect_id = 7,
                holy_socket1_level = 5,
                holy_socket2_effect_id = 8,
                holy_socket2_level = 4,
                holy_socket3_effect_id = 9,
                holy_socket3_level = 3,
                holy_socket4_effect_id = 10,
                holy_socket4_level = 2,
                updated_at = now()
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @gearSlot
              AND prop_id = 1000;
            """, connection, transaction))
        {
            updateGear.Parameters.AddWithValue("characterId", characterId);
            updateGear.Parameters.AddWithValue("gearSlot", checked((short)PreservedGearSlot));
            Check.Equal(
                1,
                await updateGear.ExecuteNonQueryAsync(),
                "PostgreSQL Gear Mentor preservation fixture seeded");
        }

        await UpsertMaterialAsync(
            connection,
            transaction,
            characterId,
            FillerSlotA,
            itemId: 4230,
            stack: 1,
            bound: 0);
        await UpsertMaterialAsync(
            connection,
            transaction,
            characterId,
            FillerSlotB,
            itemId: 4231,
            stack: 1,
            bound: 0);
        await UpsertMaterialAsync(
            connection,
            transaction,
            characterId,
            RecipeSlot,
            itemId: 9900,
            stack: 99,
            bound: 1);
        await transaction.CommitAsync();
    }

    private static async Task StageMaterialAsync(
        string connectionString,
        int characterId,
        int slot,
        int itemId,
        short stack,
        short bound)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await UpsertMaterialAsync(
            connection,
            transaction,
            characterId,
            slot,
            itemId,
            stack,
            bound);
        await transaction.CommitAsync();
    }

    private static async Task UpsertMaterialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int slot,
        int itemId,
        short stack,
        short bound)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code
            )
            VALUES (
                @characterId, 1, @slotIndex, @itemId,
                1, 1, @bound, @stack, 0, 0
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
        command.Parameters.AddWithValue("bound", bound);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"PostgreSQL Gear Mentor material {itemId} staged");
    }

    private static async Task<string> ReadItemRowAsync(
        string connectionString,
        int characterId,
        int slot)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT (to_jsonb(items) - 'id' - 'user_id')::text
            FROM character_items AS items
            WHERE items.user_id = @characterId
              AND items.item_location = 1
              AND items.slot_index = @slotIndex;
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", checked((short)slot));
        return (string)(await command.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException(
                            $"PostgreSQL item row was missing from bag slot {slot}."));
    }

    private static async Task DeleteTestAccountAsync(
        string connectionString,
        int accountId,
        string username)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            DELETE FROM accounts
            WHERE id = @accountId AND username = @username;
            """, connection);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("username", username);
        await command.ExecuteNonQueryAsync();
    }
}
