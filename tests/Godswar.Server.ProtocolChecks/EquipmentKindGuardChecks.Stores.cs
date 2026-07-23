using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentKindGuardChecks
{
    private static async Task CheckJsonStoreAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-equipment-kind-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync(
                $"equipment-kind-{Guid.NewGuid():N}",
                string.Empty);
            var material = CompactItemEntry.Parse("[9930,,,,,,1,1,0,1,0]");
            var mount = CompactItemEntry.Parse("[14220,,,,,,1,1,0,1,0]");
            var mountHead = CompactItemEntry.Parse("[14500,,,,,,1,1,0,1,0]");
            var kitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                0,
                material.ToCompactString());
            kitBag = KitBagSlots.SetSlot(kitBag, 1, mount.ToCompactString());
            kitBag = KitBagSlots.SetSlot(kitBag, 2, mountHead.ToCompactString());
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = $"Kind{Guid.NewGuid():N}"[..16],
                    Profession = 0,
                    Level = 40,
                    Equipment = GameDefaults.DefaultEquipment(0),
                    KitBag = kitBag
                });
            var equipmentBefore = character.Equipment;

            var rejected = await store.MoveKitBagToEquipmentAsync(
                account.Id,
                character.Id,
                kitBagSlot: 0,
                requestedEquipmentSlot: -1);
            Check.True(rejected is null, "JSON store rejects Strength Stone equip");

            var persisted = (await store.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.Equal(
                StrengthStoneItemId,
                KitBagSlots.GetItemId(persisted.KitBag, 0),
                "JSON rejection preserves Strength Stone in the bag");
            Check.Equal(
                equipmentBefore,
                persisted.Equipment,
                "JSON rejection preserves every equipment slot");

            var gearBeforeMount = await store.MoveKitBagToEquipmentAsync(
                account.Id,
                character.Id,
                kitBagSlot: 2,
                requestedEquipmentSlot: EquipmentSlots.MountHead);
            Check.True(gearBeforeMount is not null, "JSON mount-gear rejection returns authoritative character");
            Check.Equal(
                14500u,
                KitBagSlots.GetItemId(gearBeforeMount!.KitBag, 2),
                "JSON store preserves mount gear when no mount is equipped");

            var mounted = await store.MoveKitBagToEquipmentAsync(
                account.Id,
                character.Id,
                kitBagSlot: 1,
                requestedEquipmentSlot: EquipmentSlots.Mount)
                ?? throw new InvalidOperationException("JSON mount equip returned null.");
            Check.Equal(
                14220u,
                EquipmentSlots.GetItemId(mounted.Equipment, mounted.Profession, EquipmentSlots.Mount),
                "JSON store equips the level-matched mount");

            var geared = await store.MoveKitBagToEquipmentAsync(
                account.Id,
                character.Id,
                kitBagSlot: 2,
                requestedEquipmentSlot: EquipmentSlots.MountHead)
                ?? throw new InvalidOperationException("JSON mount-head equip returned null.");
            Check.Equal(
                14500u,
                EquipmentSlots.GetItemId(geared.Equipment, geared.Profession, EquipmentSlots.MountHead),
                "JSON store equips level-matched mount gear");

            var blockedMountRemoval = await store.MoveEquipmentToKitBagAsync(
                account.Id,
                character.Id,
                EquipmentSlots.Mount,
                kitBagSlot: 3)
                ?? throw new InvalidOperationException("JSON guarded mount removal returned null.");
            Check.Equal(
                14220u,
                EquipmentSlots.GetItemId(
                    blockedMountRemoval.Equipment,
                    blockedMountRemoval.Profession,
                    EquipmentSlots.Mount),
                "JSON store refuses to remove a mount while mount gear remains equipped");
            Check.True(
                KitBagSlots.GetItem(blockedMountRemoval.KitBag, 3).IsEmpty,
                "JSON rejected mount removal leaves the requested bag slot empty");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    private static async Task CheckPostgresStoreAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL equipment-kind guard ({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"equip_kind_{token}";
        int? accountId = null;

        try
        {
            await using var store = new PostgresGameStore(connectionString);
            await store.EnsureSeedDataAsync();
            var account = await store.LoginOrCreateAccountAsync(username, string.Empty);
            accountId = account.Id;
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = $"Kind{token}",
                    Profession = 0
                });
            var equipmentBefore = character.Equipment;

            var grant = await store.AddForgingMaterialAsync(
                account.Id,
                character.Id,
                StrengthStoneItemId,
                quantity: 1);
            Check.True(grant.Added, "PostgreSQL Strength Stone fixture is granted");
            var granted = grant.Character
                ?? throw new InvalidOperationException(
                    "PostgreSQL Strength Stone grant omitted the character.");
            var materialSlot = Enumerable.Range(0, 96)
                .Single(slot => KitBagSlots.GetItemId(granted.KitBag, slot) == StrengthStoneItemId);

            var rejected = await store.MoveKitBagToEquipmentAsync(
                account.Id,
                character.Id,
                materialSlot,
                requestedEquipmentSlot: -1);
            Check.True(rejected is null, "PostgreSQL store rejects Strength Stone equip");

            var persisted = (await store.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.Equal(
                StrengthStoneItemId,
                KitBagSlots.GetItemId(persisted.KitBag, materialSlot),
                "PostgreSQL rejection preserves Strength Stone in the bag");
            Check.Equal(
                equipmentBefore,
                persisted.Equipment,
                "PostgreSQL rejection preserves every equipment slot");

            await SeedKitBagItemAsync(connectionString, character.Id, slot: 80, itemId: 14220);
            await SeedKitBagItemAsync(connectionString, character.Id, slot: 81, itemId: 14500);

            var lowLevelMount = await store.MoveKitBagToEquipmentAsync(
                account.Id,
                character.Id,
                kitBagSlot: 80,
                requestedEquipmentSlot: EquipmentSlots.Mount)
                ?? throw new InvalidOperationException("PostgreSQL low-level mount rejection returned null.");
            Check.Equal(
                14220u,
                KitBagSlots.GetItemId(lowLevelMount.KitBag, 80),
                "PostgreSQL store enforces mount player-level requirements");

            await SetCharacterLevelAsync(connectionString, character.Id, 40);
            var baselineStats = await store.GetCharacterStatsAsync(account.Id, character.Id)
                ?? throw new InvalidOperationException("PostgreSQL mount stat baseline is missing.");

            var gearBeforeMount = await store.MoveKitBagToEquipmentAsync(
                account.Id,
                character.Id,
                kitBagSlot: 81,
                requestedEquipmentSlot: EquipmentSlots.MountHead)
                ?? throw new InvalidOperationException("PostgreSQL mount-gear rejection returned null.");
            Check.Equal(
                14500u,
                KitBagSlots.GetItemId(gearBeforeMount.KitBag, 81),
                "PostgreSQL store preserves mount gear when no mount is equipped");

            var mounted = await store.MoveKitBagToEquipmentAsync(
                account.Id,
                character.Id,
                kitBagSlot: 80,
                requestedEquipmentSlot: EquipmentSlots.Mount)
                ?? throw new InvalidOperationException("PostgreSQL mount equip returned null.");
            Check.Equal(
                14220u,
                EquipmentSlots.GetItemId(mounted.Equipment, mounted.Profession, EquipmentSlots.Mount),
                "PostgreSQL store equips the level-matched mount");

            var geared = await store.MoveKitBagToEquipmentAsync(
                account.Id,
                character.Id,
                kitBagSlot: 81,
                requestedEquipmentSlot: EquipmentSlots.MountHead)
                ?? throw new InvalidOperationException("PostgreSQL mount-head equip returned null.");
            Check.Equal(
                14500u,
                EquipmentSlots.GetItemId(geared.Equipment, geared.Profession, EquipmentSlots.MountHead),
                "PostgreSQL store equips level-matched mount gear");
            var mountedStats = await store.GetCharacterStatsAsync(account.Id, character.Id)
                ?? throw new InvalidOperationException("PostgreSQL mounted stats are missing.");
            Check.Equal(
                baselineStats.MaxHp + 2_500,
                mountedStats.MaxHp,
                "equipped Greek Steed contributes its authored maximum HP");
            Check.Equal(
                baselineStats.Hit + 9,
                mountedStats.Hit,
                "equipped Boorish Coronet contributes its authored Hit stat");

            await SetEquippedMountProgressionAsync(
                connectionString,
                character.Id,
                EquipmentSlots.MountHead);
            var progressedStats = await store.GetCharacterStatsAsync(account.Id, character.Id)
                ?? throw new InvalidOperationException("PostgreSQL progressed mount stats are missing.");
            Check.Equal(
                baselineStats.MaxHp + 2_800,
                progressedStats.MaxHp,
                "Q20 mount base HP uses the family-tier quality extension");
            Check.Equal(
                baselineStats.Hit + 28,
                progressedStats.Hit,
                "Q20 mount-head base Hit uses the extended quality vector");
            Check.Equal(
                baselineStats.PhysicalAttack + 128,
                progressedStats.PhysicalAttack,
                "G25 mount-head attack contributes through slot 15");
            Check.Equal(
                baselineStats.PhysicalDamageBonus + 240,
                progressedStats.PhysicalDamageBonus,
                "G25 mount-head physical damage contributes through slot 15");

            var blockedMountRemoval = await store.MoveEquipmentToKitBagAsync(
                account.Id,
                character.Id,
                EquipmentSlots.Mount,
                kitBagSlot: 82)
                ?? throw new InvalidOperationException("PostgreSQL guarded mount removal returned null.");
            Check.Equal(
                14220u,
                EquipmentSlots.GetItemId(
                    blockedMountRemoval.Equipment,
                    blockedMountRemoval.Profession,
                    EquipmentSlots.Mount),
                "PostgreSQL store refuses to remove a mount while mount gear remains equipped");
            Check.True(
                KitBagSlots.GetItem(blockedMountRemoval.KitBag, 82).IsEmpty,
                "PostgreSQL rejected mount removal leaves the requested bag slot empty");
        }
        finally
        {
            if (accountId.HasValue)
            {
                await DeleteTestAccountAsync(
                    connectionString,
                    accountId.Value,
                    username);
            }
        }
    }

    private static async Task SetCharacterLevelAsync(
        string connectionString,
        int characterId,
        int level)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE character_base
            SET fighter_job_lv = @level
            WHERE id = @characterId;
            """, connection);
        command.Parameters.AddWithValue("level", level);
        command.Parameters.AddWithValue("characterId", characterId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetEquippedMountProgressionAsync(
        string connectionString,
        int characterId,
        int equipmentSlot)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE character_items
            SET item_quality = 20
            WHERE user_id = @characterId
              AND item_location = 0
              AND slot_index = 20
              AND prop_id = 14220;

            UPDATE character_items
            SET item_quality = 20,
                item_grade = 25,
                attribute1 = 340,
                attribute2 = 360,
                attribute3 = NULL,
                attribute4 = NULL,
                attribute5 = NULL,
                attribute_level1 = NULL,
                attribute_level2 = NULL,
                attribute_level3 = NULL,
                attribute_level4 = NULL,
                attribute_level5 = NULL
            WHERE user_id = @characterId
              AND item_location = 0
              AND slot_index = @equipmentSlot
              AND prop_id = 14500;
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("equipmentSlot", (short)equipmentSlot);
        Check.Equal(
            2,
            await command.ExecuteNonQueryAsync(),
            "mount and mount-head progression fixture updates exactly two rows");
    }

    private static async Task SeedKitBagItemAsync(
        string connectionString,
        int characterId,
        int slot,
        int itemId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO character_items (
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                bound,
                stack
            )
            VALUES (@characterId, 1, @slot, @itemId, 1, 1, 1, 1);
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", (short)slot);
        command.Parameters.AddWithValue("itemId", itemId);
        await command.ExecuteNonQueryAsync();
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
