using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class EquipmentKindGuardChecks
{
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const uint StrengthStoneItemId = 9930;

    public static async Task RunAsync()
    {
        CheckKindCatalog();
        await CheckJsonStoreAsync();
        await CheckPostgresStoreAsync();
    }

    private static void CheckKindCatalog()
    {
        string[] equipmentKinds =
        [
            "head",
            "amulet",
            "glove",
            "armor",
            "cloth",
            "cuff",
            "girdle",
            "shoes",
            "leggins",
            "ring",
            "weapon",
            "shield",
            "stylish"
        ];

        foreach (var kind in equipmentKinds)
        {
            Check.True(
                EquipmentSlots.IsEquipmentKind(kind),
                $"equipment kind '{kind}' is accepted");
        }

        Check.True(
            !EquipmentSlots.IsEquipmentKind("consume item"),
            "consume-item templates cannot use their placeholder slot as equipment");
        Check.True(
            !EquipmentSlots.IsEquipmentKind(string.Empty),
            "empty template kind is not equipment");
    }

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
            var character = await store.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = $"Kind{Guid.NewGuid():N}"[..16],
                    Profession = 0,
                    Equipment = GameDefaults.DefaultEquipment(0),
                    KitBag = KitBagSlots.SetSlot(
                        GameDefaults.EmptyKitBag,
                        0,
                        material.ToCompactString())
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
