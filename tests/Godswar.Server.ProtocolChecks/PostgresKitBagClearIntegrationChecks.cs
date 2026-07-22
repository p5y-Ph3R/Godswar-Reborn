using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresKitBagClearIntegrationChecks
{
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const short EquipmentLocation = 0;
    private const short KitBagLocation = 1;
    private const short TransientLocation = 2;

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL kit-bag clear integration ({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"clear_bag_{token}";
        var characterName = $"Clear{token}";
        int? accountId = null;
        int? characterId = null;

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
                    Name = characterName,
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 0,
                    Silver = 123_456,
                    Gold = 77
                });
            characterId = character.Id;

            var grant = await store.AddForgingMaterialAsync(
                account.Id,
                character.Id,
                itemId: 4234,
                quantity: 7);
            Check.True(grant.Added, "PostgreSQL clear-bag test material granted");

            await StageTransientItemAsync(connectionString, character.Id);
            character = (await store.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);

            var equipmentProjectionBefore = character.Equipment;
            var silverBefore = character.Silver;
            var goldBefore = character.Gold;
            var bagBefore = await ReadLocationSnapshotAsync(
                connectionString,
                character.Id,
                KitBagLocation);
            var equipmentBefore = await ReadLocationSnapshotAsync(
                connectionString,
                character.Id,
                EquipmentLocation);
            var transientBefore = await ReadLocationSnapshotAsync(
                connectionString,
                character.Id,
                TransientLocation);
            var bagRowCount = await CountLocationRowsAsync(
                connectionString,
                character.Id,
                KitBagLocation);

            Check.True(bagRowCount >= 3, "PostgreSQL clear-bag fixture contains starter and granted items");
            Check.True(
                await CountLocationRowsAsync(connectionString, character.Id, EquipmentLocation) > 0,
                "PostgreSQL clear-bag fixture contains equipped items");
            Check.Equal(
                1L,
                await CountLocationRowsAsync(connectionString, character.Id, TransientLocation),
                "PostgreSQL clear-bag fixture contains a transient-location item");
            Check.Equal(
                0L,
                await CountClearBagAuditRowsAsync(connectionString, character.Id),
                "PostgreSQL clear-bag fixture starts without clear audit rows");

            var wrongOwner = await store.ClearKitBagAsync(account.Id + 1, character.Id);
            Check.True(wrongOwner is null, "PostgreSQL clear bag binds character ownership to the account");
            Check.Equal(
                bagBefore,
                await ReadLocationSnapshotAsync(connectionString, character.Id, KitBagLocation),
                "wrong-owner clear leaves all kit-bag rows unchanged");
            Check.Equal(
                equipmentBefore,
                await ReadLocationSnapshotAsync(connectionString, character.Id, EquipmentLocation),
                "wrong-owner clear leaves all equipment rows unchanged");
            Check.Equal(
                transientBefore,
                await ReadLocationSnapshotAsync(connectionString, character.Id, TransientLocation),
                "wrong-owner clear leaves transient rows unchanged");
            Check.Equal(
                0L,
                await CountClearBagAuditRowsAsync(connectionString, character.Id),
                "wrong-owner clear creates no audit rows");

            var cleared = await store.ClearKitBagAsync(account.Id, character.Id)
                ?? throw new InvalidOperationException("Owned PostgreSQL kit-bag clear returned no character.");
            Check.True(
                Enumerable.Range(0, 96)
                    .All(slot => KitBagSlots.GetItem(cleared.KitBag, slot).IsEmpty),
                "owned PostgreSQL clear returns an empty 96-slot kit bag");
            Check.Equal(
                equipmentProjectionBefore,
                cleared.Equipment,
                "owned PostgreSQL clear preserves the projected equipment loadout");
            Check.Equal(silverBefore, cleared.Silver, "owned PostgreSQL clear preserves silver");
            Check.Equal(goldBefore, cleared.Gold, "owned PostgreSQL clear preserves gold");
            Check.Equal(
                0L,
                await CountLocationRowsAsync(connectionString, character.Id, KitBagLocation),
                "owned PostgreSQL clear deletes every and only location-1 bag row");
            Check.Equal(
                equipmentBefore,
                await ReadLocationSnapshotAsync(connectionString, character.Id, EquipmentLocation),
                "owned PostgreSQL clear preserves location-0 equipment rows exactly");
            Check.Equal(
                transientBefore,
                await ReadLocationSnapshotAsync(connectionString, character.Id, TransientLocation),
                "owned PostgreSQL clear preserves location-2 transient rows exactly");
            Check.Equal(
                bagRowCount,
                await CountClearBagAuditRowsAsync(connectionString, character.Id),
                "owned PostgreSQL clear audits every deleted bag row exactly once");
            Check.Equal(
                bagBefore,
                await ReadClearBagAuditPayloadSnapshotAsync(connectionString, character.Id),
                "PostgreSQL clear-bag audit payloads contain the complete deleted rows");
            Check.Equal(
                0L,
                await CountInvalidClearBagAuditRowsAsync(connectionString, character.Id),
                "PostgreSQL clear-bag audit columns agree with their old-item payloads");

            await using var reopenedStore = new PostgresGameStore(connectionString);
            await reopenedStore.EnsureSeedDataAsync();
            var reopened = (await reopenedStore.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.True(
                Enumerable.Range(0, 96)
                    .All(slot => KitBagSlots.GetItem(reopened.KitBag, slot).IsEmpty),
                "cleared PostgreSQL kit bag remains empty after reopening and reseeding the store");
            Check.Equal(
                equipmentProjectionBefore,
                reopened.Equipment,
                "equipment remains unchanged after reopening the cleared PostgreSQL character");
            Check.Equal(silverBefore, reopened.Silver, "silver survives a clear and PostgreSQL store reopen");
            Check.Equal(goldBefore, reopened.Gold, "gold survives a clear and PostgreSQL store reopen");
            Check.Equal(
                transientBefore,
                await ReadLocationSnapshotAsync(connectionString, character.Id, TransientLocation),
                "location-2 row survives a clear and PostgreSQL store reopen");

            var auditCountBeforeNoOp = await CountClearBagAuditRowsAsync(
                connectionString,
                character.Id);
            var clearedAgain = await reopenedStore.ClearKitBagAsync(account.Id, character.Id)
                ?? throw new InvalidOperationException("Idempotent PostgreSQL kit-bag clear returned no character.");
            Check.True(
                Enumerable.Range(0, 96)
                    .All(slot => KitBagSlots.GetItem(clearedAgain.KitBag, slot).IsEmpty),
                "clearing an already-empty PostgreSQL kit bag succeeds as an idempotent no-op");
            Check.Equal(
                auditCountBeforeNoOp,
                await CountClearBagAuditRowsAsync(connectionString, character.Id),
                "idempotent PostgreSQL clear does not manufacture audit rows");
            Check.Equal(
                equipmentBefore,
                await ReadLocationSnapshotAsync(connectionString, character.Id, EquipmentLocation),
                "idempotent PostgreSQL clear still preserves equipment rows");
            Check.Equal(
                transientBefore,
                await ReadLocationSnapshotAsync(connectionString, character.Id, TransientLocation),
                "idempotent PostgreSQL clear still preserves transient rows");
        }
        finally
        {
            if (accountId.HasValue)
            {
                await DeleteTestAccountAsync(
                    connectionString,
                    accountId.Value,
                    username,
                    characterId);
            }
        }
    }

    private static async Task StageTransientItemAsync(string connectionString, int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp, holy_suit_code
            )
            VALUES (
                @characterId, @itemLocation, 0, 9930,
                3, 4, 0, 7, 111, 222
            );
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemLocation", TransientLocation);
        Check.Equal(1, await command.ExecuteNonQueryAsync(), "transient-location test item staged");
    }

    private static async Task<long> CountLocationRowsAsync(
        string connectionString,
        int characterId,
        short itemLocation)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM character_items
            WHERE user_id = @characterId
              AND item_location = @itemLocation;
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemLocation", itemLocation);
        return (long)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException("PostgreSQL item-row count returned null."));
    }

    private static async Task<string> ReadLocationSnapshotAsync(
        string connectionString,
        int characterId,
        short itemLocation)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(
                jsonb_agg(to_jsonb(items) ORDER BY items.slot_index, items.id),
                '[]'::jsonb
            )::text
            FROM character_items AS items
            WHERE items.user_id = @characterId
              AND items.item_location = @itemLocation;
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemLocation", itemLocation);
        return (string)(await command.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException("PostgreSQL item snapshot returned null."));
    }

    private static async Task<long> CountClearBagAuditRowsAsync(
        string connectionString,
        int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM character_item_audit
            WHERE source = 'developer-clearbag'
              AND user_id = @characterId;
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        return (long)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException("PostgreSQL clear-bag audit count returned null."));
    }

    private static async Task<string> ReadClearBagAuditPayloadSnapshotAsync(
        string connectionString,
        int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(
                jsonb_agg(
                    audit.old_item
                    ORDER BY audit.slot_index, (audit.old_item ->> 'id')::bigint
                ),
                '[]'::jsonb
            )::text
            FROM character_item_audit AS audit
            WHERE audit.source = 'developer-clearbag'
              AND audit.user_id = @characterId;
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        return (string)(await command.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException(
                            "PostgreSQL clear-bag audit snapshot returned null."));
    }

    private static async Task<long> CountInvalidClearBagAuditRowsAsync(
        string connectionString,
        int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM character_item_audit AS audit
            WHERE audit.source = 'developer-clearbag'
              AND audit.user_id = @characterId
              AND (
                  audit.action IS DISTINCT FROM 'delete'
                  OR audit.item_location IS DISTINCT FROM 1
                  OR audit.old_item IS NULL
                  OR audit.user_id IS DISTINCT FROM (audit.old_item ->> 'user_id')::integer
                  OR audit.item_location IS DISTINCT FROM (audit.old_item ->> 'item_location')::smallint
                  OR audit.slot_index IS DISTINCT FROM (audit.old_item ->> 'slot_index')::smallint
                  OR audit.prop_id IS DISTINCT FROM (audit.old_item ->> 'prop_id')::integer
                  OR audit.item_quality IS DISTINCT FROM (audit.old_item ->> 'item_quality')::smallint
                  OR audit.item_grade IS DISTINCT FROM (audit.old_item ->> 'item_grade')::smallint
                  OR audit.item_exp IS DISTINCT FROM (audit.old_item ->> 'item_exp')::integer
                  OR audit.old_item ->> 'id' IS NULL
              );
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        return (long)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException(
                          "PostgreSQL invalid clear-bag audit count returned null."));
    }

    private static async Task DeleteTestAccountAsync(
        string connectionString,
        int accountId,
        string username,
        int? characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        if (characterId.HasValue)
        {
            await using var deleteAudit = new NpgsqlCommand("""
                DELETE FROM character_item_audit
                WHERE user_id = @characterId;
                """, connection, transaction);
            deleteAudit.Parameters.AddWithValue("characterId", characterId.Value);
            await deleteAudit.ExecuteNonQueryAsync();
        }

        await using (var deleteAccount = new NpgsqlCommand("""
            DELETE FROM accounts
            WHERE id = @accountId AND username = @username;
            """, connection, transaction))
        {
            deleteAccount.Parameters.AddWithValue("accountId", accountId);
            deleteAccount.Parameters.AddWithValue("username", username);
            await deleteAccount.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
