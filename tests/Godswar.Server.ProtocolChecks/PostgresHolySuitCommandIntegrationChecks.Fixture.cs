using Godswar.Server.Application.Commands;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolySuitCommandIntegrationChecks
{
    private static async Task<Fixture> CreateFixtureAsync(
        string connectionString)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var account = new NpgsqlCommand(
            "INSERT INTO accounts(username,password) VALUES(@name,'') RETURNING id;",
            connection,
            transaction);
        account.Parameters.AddWithValue("name", $"b09holy_{token}");
        var accountId = Convert.ToInt32(await account.ExecuteScalarAsync());
        var character = new NpgsqlCommand(
            """
            INSERT INTO character_base(
                account_id,name,camp,profession,fighter_job_lv,
                fighter_job_exp,"Money","Stone")
            VALUES(@accountId,@name,1,1,80,4000000000,0,0)
            RETURNING id;
            """,
            connection,
            transaction);
        character.Parameters.AddWithValue("accountId", accountId);
        character.Parameters.AddWithValue("name", $"HS{token}");
        var characterId = Convert.ToInt32(
            await character.ExecuteScalarAsync());
        await InsertItemAsync(connection, transaction, characterId, 0,
            Item(9023, bound: 1));
        await InsertItemAsync(connection, transaction, characterId, 1,
            Item(1007));
        await InsertItemAsync(connection, transaction, characterId, 2,
            Item(1007, suit: 501));
        await InsertItemAsync(connection, transaction, characterId, 3,
            Item(9010, stack: 99));
        await InsertItemAsync(connection, transaction, characterId, 4,
            Item(9014, stack: 99));
        await InsertItemAsync(connection, transaction, characterId, 5,
            Item(9023, bound: 1));
        await InsertItemAsync(connection, transaction, characterId, 6,
            Item(9025, bound: 1, stack: 20));
        await InsertItemAsync(connection, transaction, characterId, 7,
            Item(9024));
        Check.True(await PostgresCharacterEconomyBaseline.EnsureAsync(
            connection, transaction, accountId, characterId, 30,
            CancellationToken.None), "Holy Suit fixture baseline");
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new Fixture(
            accountId,
            characterId,
            new CommandSubject(accountId, characterId));
    }

    private static async Task InsertItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        short slot,
        CompactItemEntry item)
    {
        var command = new NpgsqlCommand(
            """
            INSERT INTO character_items(
                user_id,item_location,slot_index,prop_id,item_quality,
                item_grade,bound,stack,item_exp,holy_suit_code)
            VALUES(@characterId,1,@slot,@itemId,@quality,@grade,
                @bound,@stack,@itemExp,@holySuitCode);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("slot", slot);
        command.Parameters.AddWithValue("itemId", checked((int)item.Id));
        command.Parameters.AddWithValue("quality", item.Quality);
        command.Parameters.AddWithValue("grade", item.Grade);
        command.Parameters.AddWithValue("bound", item.Bound);
        command.Parameters.AddWithValue("stack", item.Stack);
        command.Parameters.AddWithValue("itemExp", item.Exp);
        command.Parameters.AddWithValue("holySuitCode", item.HolySuitCode);
        Check.Equal(1, await command.ExecuteNonQueryAsync(),
            $"insert Holy Suit fixture slot {slot}");
    }

    private static async Task AddBattlePassAsync(
        string connectionString,
        int accountId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = new NpgsqlCommand(
            """
            INSERT INTO account_entitlements(
                account_id,entitlement_key,scope_key,starts_at,source)
            VALUES(@accountId,'battle_pass','realm:1',now(),'test');
            """,
            connection);
        command.Parameters.AddWithValue("accountId", accountId);
        Check.Equal(1, await command.ExecuteNonQueryAsync(),
            "insert active battle pass");
    }

    private static async Task SetDailyStoredExperienceAsync(
        string connectionString,
        Fixture fixture,
        DateOnly usageDay,
        long storedExperience)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = new NpgsqlCommand(
            """
            INSERT INTO holy_suit_daily_exp_storage(
                account_id,realm_id,usage_day,stored_exp,operation_count)
            VALUES(@accountId,1,@usageDay,@storedExperience,0)
            ON CONFLICT(account_id,realm_id,usage_day) DO UPDATE
            SET stored_exp=EXCLUDED.stored_exp;
            """,
            connection);
        command.Parameters.AddWithValue("storedExperience", storedExperience);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue("usageDay", usageDay);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "position daily usage below fixed cap");
    }

    private static async Task<DurableState> ReadStateAsync(
        string connectionString,
        Fixture fixture)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = new NpgsqlCommand(
            """
            SELECT cb.fighter_job_exp,
                   cb.progression_reward_revision,
                   cb.inventory_revision,
                   hs.stored_exp,
                   (SELECT count(*) FROM outbox_events o
                    JOIN command_inbox i ON i.id=o.command_inbox_id
                    WHERE i.principal_key=@principal),
                   (SELECT count(*) FROM command_inbox
                    WHERE principal_key=@principal AND command_family LIKE 'holy_suit_%'),
                   (SELECT count(*) FROM command_audit
                    WHERE principal_key=@principal AND command_family LIKE 'holy_suit_%'),
                   (SELECT count(*) FROM character_inventory_ledger
                    WHERE character_id=@characterId AND reason_code LIKE 'holy_suit_%'),
                   (SELECT coalesce(sum(duplicate_count),0) FROM command_inbox
                    WHERE principal_key=@principal AND command_family LIKE 'holy_suit_%'),
                   (SELECT count(*) FROM character_items
                    WHERE user_id=@characterId AND prop_id=9023 AND slot_index=0),
                   (SELECT coalesce(sum(stack),0) FROM character_items
                    WHERE user_id=@characterId AND prop_id=9025)
            FROM character_base cb
            JOIN holy_suit_daily_exp_storage hs
              ON hs.account_id=cb.account_id AND hs.realm_id=1
             AND hs.usage_day=
                 (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Singapore')::date
            WHERE cb.id=@characterId;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", fixture.CharacterId);
        command.Parameters.AddWithValue(
            "principal",
            fixture.AccountId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "read Holy Suit durable state");
        return new DurableState(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
            reader.GetInt64(9), reader.GetInt64(10));
    }

    private static CompactItemEntry Item(
        uint id,
        short bound = 0,
        short stack = 1,
        int exp = 0,
        int suit = 0) =>
        CompactItemEntry.Empty with
        {
            Id = id,
            Quality = 1,
            Grade = 1,
            Bound = bound,
            Stack = stack,
            Exp = exp,
            HolySuitCode = suit
        };

    private sealed record Fixture(
        int AccountId,
        int CharacterId,
        CommandSubject Subject);

    private sealed record DurableState(
        long Experience,
        long ProgressionRevision,
        long InventoryRevision,
        long DailyStored,
        long OutboxCount,
        long InboxCount,
        long AuditCount,
        long InventoryLedgerCount,
        long DuplicateCount,
        long ConsumedBoxCount,
        long PrismCount);
}
