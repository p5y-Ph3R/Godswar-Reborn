using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresForgeIntegrationChecks
{
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine($"SKIP PostgreSQL forge integration ({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"forge_sec_{token}";
        var characterName = $"Forge{token}";
        int? accountId = null;
        int? characterId = null;

        try
        {
            await using var storeA = new PostgresGameStore(connectionString);
            await using var storeB = new PostgresGameStore(connectionString);
            await storeA.EnsureSeedDataAsync();
            await storeB.EnsureSeedDataAsync();

            var account = await storeA.LoginOrCreateAccountAsync(username, string.Empty);
            accountId = account.Id;
            var character = await storeA.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = characterName,
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 0,
                    Silver = 1_000
                });
            characterId = character.Id;

            character = await storeA.MoveEquipmentToKitBagAsync(
                    account.Id,
                    character.Id,
                    EquipmentSlots.Weapon,
                    kitBagSlot: 2)
                ?? throw new InvalidOperationException("Could not move integration-test weapon into the bag.");
            Check.Equal(1000u, KitBagSlots.GetItem(character.KitBag, 2).Id, "PostgreSQL test weapon moved to bag");

            await SetTestEquipmentMetadataAsync(connectionString, character.Id, kitBagSlot: 2);
            var primaryGrant = await storeA.AddForgingMaterialAsync(account.Id, character.Id, 4212, 1);
            Check.True(primaryGrant.Added, "PostgreSQL test Sapphire granted");
            var oddsGrant = await storeA.AddForgingMaterialAsync(account.Id, character.Id, 4232, 5);
            Check.True(oddsGrant.Added, "PostgreSQL test Crystals granted");

            character = (await storeA.GetCharactersAsync(account.Id)).Single(candidate => candidate.Id == character.Id);
            var primarySlot = FindSlot(character.KitBag, 4212);
            var oddsSlot = FindSlot(character.KitBag, 4232);
            var equipmentBefore = KitBagSlots.GetItem(character.KitBag, 2);
            var request = new ForgeTransactionRequest(
                new ForgeSlotSelection(2, equipmentBefore, 1),
                ForgeSlotSelection.Capture(character.KitBag, primarySlot),
                ForgeSlotSelection.Capture(character.KitBag, oddsSlot, 5));

            var wrongOwner = await storeA.ForgeEquipmentAsync(account.Id + 1, character.Id, request);
            Check.Equal(
                (int)ForgeTransactionStatus.CharacterNotFound,
                (int)wrongOwner.Status,
                "PostgreSQL forge binds character ownership to the account");

            var raced = await Task.WhenAll(
                storeA.ForgeEquipmentAsync(account.Id, character.Id, request),
                storeB.ForgeEquipmentAsync(account.Id, character.Id, request));
            Check.Equal(1, raced.Count(result => result.Committed), "only one concurrent PostgreSQL forge commits");
            Check.Equal(1, raced.Count(result => !result.Committed), "duplicate PostgreSQL forge is rejected");
            Check.Equal(
                (int)ForgeTransactionStatus.StaleSelection,
                (int)raced.Single(result => !result.Committed).Status,
                "concurrent PostgreSQL duplicate fails authoritative snapshot revalidation");

            var persisted = (await storeA.GetCharactersAsync(account.Id)).Single(candidate => candidate.Id == character.Id);
            Check.Equal(999, persisted.Silver, "PostgreSQL race deducts silver exactly once");
            Check.Equal(
                equipmentBefore with { Quality = 2 },
                KitBagSlots.GetItem(persisted.KitBag, 2),
                "PostgreSQL forge preserves all metadata while changing only quality");
            Check.True(KitBagSlots.GetItem(persisted.KitBag, primarySlot).IsEmpty, "exact Sapphire stack is deleted");
            Check.True(KitBagSlots.GetItem(persisted.KitBag, oddsSlot).IsEmpty, "exact Crystal stack is deleted");

            await SetTestEquipmentProgressionAsync(
                connectionString,
                character.Id,
                kitBagSlot: 2,
                quality: 19,
                grade: 25);
            var qualityBoundaryBefore = equipmentBefore with { Quality = 19, Grade = 25 };
            var qualityBoundaryAfter = qualityBoundaryBefore with { Quality = 20 };

            await using (var qualityForgeStore = new PostgresGameStore(connectionString))
            {
                await qualityForgeStore.EnsureSeedDataAsync();
                var levelFiveSapphireGrant = await qualityForgeStore.AddForgingMaterialAsync(
                    account.Id,
                    character.Id,
                    4215,
                    1);
                Check.True(levelFiveSapphireGrant.Added, "PostgreSQL Q19 boundary Level-5 Sapphire granted");
                var levelFiveCrystalGrant = await qualityForgeStore.AddForgingMaterialAsync(
                    account.Id,
                    character.Id,
                    4234,
                    25);
                Check.True(levelFiveCrystalGrant.Added, "PostgreSQL Q19 boundary Level-5 Crystals granted");

                var qualityCharacter = (await qualityForgeStore.GetCharactersAsync(account.Id))
                    .Single(candidate => candidate.Id == character.Id);
                var qualityPrimarySlot = FindSlot(qualityCharacter.KitBag, 4215);
                var qualityOddsSlot = FindSlot(qualityCharacter.KitBag, 4234);
                Check.Equal(
                    qualityBoundaryBefore,
                    KitBagSlots.GetItem(qualityCharacter.KitBag, 2),
                    "PostgreSQL Q19/G25 input survives the compatibility projection without clamping");

                var qualityResult = await qualityForgeStore.ForgeEquipmentAsync(
                    account.Id,
                    character.Id,
                    new ForgeTransactionRequest(
                        ForgeSlotSelection.Capture(qualityCharacter.KitBag, 2),
                        ForgeSlotSelection.Capture(qualityCharacter.KitBag, qualityPrimarySlot),
                        ForgeSlotSelection.Capture(qualityCharacter.KitBag, qualityOddsSlot, 25)));
                Check.Equal(
                    (int)ForgeTransactionStatus.Succeeded,
                    (int)qualityResult.Status,
                    "PostgreSQL Q19/G25 boundary forge succeeds");
                Check.Equal(
                    (int)EquipmentForgeOperation.Sapphire,
                    qualityResult.MaterialType,
                    "PostgreSQL Q19/G25 boundary uses Sapphire forging");
                Check.Equal(100, qualityResult.Probability, "25 Level-5 Crystals guarantee the PostgreSQL Q19 boundary");
                Check.Equal(65, qualityResult.SilverSpent, "PostgreSQL Q19 boundary uses the Q19 silver cost");
                Check.Equal(
                    qualityBoundaryAfter,
                    qualityResult.EquipmentAfter,
                    "PostgreSQL Q19/G25 forge reaches Q20/G25 and preserves every metadata field");
            }

            await using (var reopenedAfterQuality = new PostgresGameStore(connectionString))
            {
                await reopenedAfterQuality.EnsureSeedDataAsync();
                var qualityPersisted = (await reopenedAfterQuality.GetCharactersAsync(account.Id))
                    .Single(candidate => candidate.Id == character.Id);
                Check.Equal(934, qualityPersisted.Silver, "Q20/G25 silver survives a PostgreSQL store reopen");
                Check.Equal(
                    qualityBoundaryAfter,
                    KitBagSlots.GetItem(qualityPersisted.KitBag, 2),
                    "Q20/G25 and all equipment metadata survive a PostgreSQL store reopen and reseed");
            }

            await SetTestEquipmentProgressionAsync(
                connectionString,
                character.Id,
                kitBagSlot: 2,
                quality: 20,
                grade: 24);
            var gradeBoundaryBefore = qualityBoundaryAfter with { Grade = 24 };
            var gradeBoundaryAfter = gradeBoundaryBefore with { Grade = 25 };

            await using (var gradeForgeStore = new PostgresGameStore(connectionString))
            {
                await gradeForgeStore.EnsureSeedDataAsync();
                var levelFiveEmeraldGrant = await gradeForgeStore.AddForgingMaterialAsync(
                    account.Id,
                    character.Id,
                    4225,
                    1);
                Check.True(levelFiveEmeraldGrant.Added, "PostgreSQL G24 boundary Level-5 Emerald granted");
                var levelFiveCrystalGrant = await gradeForgeStore.AddForgingMaterialAsync(
                    account.Id,
                    character.Id,
                    4234,
                    25);
                Check.True(levelFiveCrystalGrant.Added, "PostgreSQL G24 boundary Level-5 Crystals granted");

                var gradeCharacter = (await gradeForgeStore.GetCharactersAsync(account.Id))
                    .Single(candidate => candidate.Id == character.Id);
                var gradePrimarySlot = FindSlot(gradeCharacter.KitBag, 4225);
                var gradeOddsSlot = FindSlot(gradeCharacter.KitBag, 4234);
                Check.Equal(
                    gradeBoundaryBefore,
                    KitBagSlots.GetItem(gradeCharacter.KitBag, 2),
                    "PostgreSQL Q20/G24 input survives the compatibility projection without clamping");

                var gradeResult = await gradeForgeStore.ForgeEquipmentAsync(
                    account.Id,
                    character.Id,
                    new ForgeTransactionRequest(
                        ForgeSlotSelection.Capture(gradeCharacter.KitBag, 2),
                        ForgeSlotSelection.Capture(gradeCharacter.KitBag, gradePrimarySlot),
                        ForgeSlotSelection.Capture(gradeCharacter.KitBag, gradeOddsSlot, 25)));
                Check.Equal(
                    (int)ForgeTransactionStatus.Succeeded,
                    (int)gradeResult.Status,
                    "PostgreSQL Q20/G24 boundary forge succeeds");
                Check.Equal(
                    (int)EquipmentForgeOperation.Emerald,
                    gradeResult.MaterialType,
                    "PostgreSQL Q20/G24 boundary uses Emerald forging");
                Check.Equal(100, gradeResult.Probability, "25 Level-5 Crystals guarantee the PostgreSQL G24 boundary");
                Check.Equal(85, gradeResult.SilverSpent, "PostgreSQL G24 boundary uses the G24 silver cost");
                Check.Equal(
                    gradeBoundaryAfter,
                    gradeResult.EquipmentAfter,
                    "PostgreSQL Q20/G24 forge reaches Q20/G25 and preserves every metadata field");
            }

            await using (var reopenedAfterGrade = new PostgresGameStore(connectionString))
            {
                await reopenedAfterGrade.EnsureSeedDataAsync();
                var gradePersisted = (await reopenedAfterGrade.GetCharactersAsync(account.Id))
                    .Single(candidate => candidate.Id == character.Id);
                Check.Equal(849, gradePersisted.Silver, "Q20/G25 grade-forge silver survives a PostgreSQL store reopen");
                Check.Equal(
                    gradeBoundaryAfter,
                    KitBagSlots.GetItem(gradePersisted.KitBag, 2),
                    "grade-forged Q20/G25 and all equipment metadata survive a PostgreSQL store reopen and reseed");
            }
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
                    "forge-consume");
            }
        }
    }

    private static int FindSlot(string kitBag, uint itemId)
    {
        for (var slot = 0; slot < 96; slot++)
        {
            if (KitBagSlots.GetItemId(kitBag, slot) == itemId)
            {
                return slot;
            }
        }

        throw new InvalidOperationException($"Could not find test item {itemId} in the authoritative bag.");
    }

    private static async Task SetTestEquipmentMetadataAsync(
        string connectionString,
        int characterId,
        int kitBagSlot)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE character_items
            SET attribute1 = 0,
                attribute_level1 = 7,
                item_exp = 123,
                holy_suit_code = 305,
                holy_socket_count = 2,
                holy_socket1_effect_id = 7,
                holy_socket1_level = 1,
                holy_socket2_effect_id = 8,
                holy_socket2_level = 2
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @slotIndex
              AND prop_id = 1000;
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", (short)kitBagSlot);
        Check.Equal(1, await command.ExecuteNonQueryAsync(), "test equipment metadata seeded");
    }

    private static async Task SetTestEquipmentProgressionAsync(
        string connectionString,
        int characterId,
        int kitBagSlot,
        short quality,
        short grade)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE character_items
            SET item_quality = @quality,
                item_grade = @grade,
                updated_at = now()
            WHERE user_id = @characterId
              AND item_location = 1
              AND slot_index = @slotIndex
              AND prop_id = 1000;
            """, connection);
        command.Parameters.AddWithValue("quality", quality);
        command.Parameters.AddWithValue("grade", grade);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slotIndex", checked((short)kitBagSlot));
        Check.Equal(1, await command.ExecuteNonQueryAsync(), "test equipment quality/grade boundary seeded");
    }
}
