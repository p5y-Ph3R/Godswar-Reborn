using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresDeveloperMountIntegrationChecks
{
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL developer mount integration ({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var ownerName = $"mount_owner_{token}";
        var otherName = $"mount_other_{token}";
        int? ownerId = null;
        int? otherId = null;
        int? characterId = null;

        try
        {
            await using var store = new PostgresGameStore(connectionString);
            await store.EnsureSeedDataAsync();
            var owner = await store.LoginOrCreateAccountAsync(ownerName, string.Empty);
            var other = await store.LoginOrCreateAccountAsync(otherName, string.Empty);
            ownerId = owner.Id;
            otherId = other.Id;
            var character = await store.CreateCharacterAsync(
                owner.Id,
                new GameCharacter { Name = $"Mount{token}" });
            characterId = character.Id;
            _ = await store.ClearKitBagAsync(owner.Id, character.Id)
                ?? throw new InvalidOperationException("PostgreSQL mount fixture bag could not be cleared.");

            var wrongOwner = await store.AddDeveloperMountAsync(other.Id, character.Id, 14224);
            Check.True(
                wrongOwner.Status == KitBagItemGrantStatus.CharacterNotFound,
                "PostgreSQL mount grant binds character ownership to the account");
            Check.Equal(
                0L,
                await CountMountAuditsAsync(connectionString, character.Id),
                "denied PostgreSQL mount grant creates no audit row");

            var rejectedOrphan = false;
            try
            {
                await store.AddDeveloperMountAsync(
                    owner.Id,
                    character.Id,
                    DeveloperMountCatalog.OrphanedMountItemId);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejectedOrphan = true;
            }

            Check.True(rejectedOrphan, "PostgreSQL store revalidates the mount allowlist");

            var granted = await store.AddDeveloperMountAsync(owner.Id, character.Id, 14224);
            Check.True(granted.Added && granted.Character is not null, "PostgreSQL mount grant succeeds");
            var grantedMount = KitBagSlots.GetItem(granted.Character!.KitBag, 0);
            Check.Equal(14224u, grantedMount.Id, "PostgreSQL mount uses the first empty bag slot");
            Check.Equal((short)1, grantedMount.Quality, "PostgreSQL mount quality");
            Check.Equal((short)1, grantedMount.Grade, "PostgreSQL mount grade");
            Check.Equal((short)1, grantedMount.Bound, "PostgreSQL mount binding");
            Check.Equal((short)1, grantedMount.Stack, "PostgreSQL mount stack");

            Check.Equal(
                "14224|1|1|1|1|0",
                await ReadMountRowAsync(connectionString, character.Id),
                "authoritative PostgreSQL mount row has the fixed developer grant shape");
            Check.Equal(
                1L,
                await CountMountAuditsAsync(connectionString, character.Id),
                "successful PostgreSQL mount grant is audited exactly once");
            Check.Equal(
                0L,
                await CountInvalidMountAuditsAsync(connectionString, character.Id),
                "mount audit columns and inserted-row snapshot agree");
        }
        finally
        {
            if (ownerId.HasValue || otherId.HasValue)
            {
                await DeleteTestAccountsAsync(
                    connectionString,
                    ownerId,
                    ownerName,
                    otherId,
                    otherName,
                    characterId);
            }
        }
    }

    private static async Task<string> ReadMountRowAsync(string connectionString, int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT concat_ws(
                '|', prop_id, item_quality, item_grade, bound, stack, slot_index
            )
            FROM character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = 14224;
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        return (string)(await command.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException("PostgreSQL mount row was not found."));
    }

    private static async Task<long> CountMountAuditsAsync(string connectionString, int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM character_item_audit
            WHERE source = 'developer-mount-grant'
              AND user_id = @characterId;
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        return (long)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException("PostgreSQL mount audit count returned null."));
    }

    private static async Task<long> CountInvalidMountAuditsAsync(
        string connectionString,
        int characterId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM character_item_audit audit
            WHERE source = 'developer-mount-grant'
              AND user_id = @characterId
              AND (
                  action IS DISTINCT FROM 'insert'
                  OR item_location IS DISTINCT FROM 1
                  OR slot_index IS DISTINCT FROM 0
                  OR prop_id IS DISTINCT FROM 14224
                  OR item_quality IS DISTINCT FROM 1
                  OR item_grade IS DISTINCT FROM 1
                  OR item_exp IS DISTINCT FROM 0
                  OR old_item IS NULL
                  OR (old_item ->> 'prop_id')::integer IS DISTINCT FROM prop_id
                  OR (old_item ->> 'item_quality')::smallint IS DISTINCT FROM item_quality
                  OR (old_item ->> 'item_grade')::smallint IS DISTINCT FROM item_grade
                  OR (old_item ->> 'bound')::smallint IS DISTINCT FROM 1
                  OR (old_item ->> 'stack')::smallint IS DISTINCT FROM 1
              );
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        return (long)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException("PostgreSQL invalid mount audit count returned null."));
    }

    private static async Task DeleteTestAccountsAsync(
        string connectionString,
        int? ownerId,
        string ownerName,
        int? otherId,
        string otherName,
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

        await DeleteAccountAsync(connection, transaction, ownerId, ownerName);
        await DeleteAccountAsync(connection, transaction, otherId, otherName);
        await transaction.CommitAsync();
    }

    private static async Task DeleteAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int? accountId,
        string username)
    {
        if (!accountId.HasValue)
        {
            return;
        }

        await using var command = new NpgsqlCommand("""
            DELETE FROM accounts
            WHERE id = @accountId AND username = @username;
            """, connection, transaction);
        command.Parameters.AddWithValue("accountId", accountId.Value);
        command.Parameters.AddWithValue("username", username);
        await command.ExecuteNonQueryAsync();
    }
}
