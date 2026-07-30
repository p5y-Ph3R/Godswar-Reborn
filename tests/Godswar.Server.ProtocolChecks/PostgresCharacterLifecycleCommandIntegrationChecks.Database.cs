using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterLifecycleCommandIntegrationChecks
{
    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ??
            throw new InvalidDataException(
                "PostgreSQL returned no database name.");
    }

    private static async Task<GameAccount> CreateAccountAsync(
        string connectionString)
    {
        await using var store = new PostgresGameStore(connectionString);
        var token = Guid.NewGuid().ToString("N")[..12];
        return await store.LoginOrCreateAccountAsync(
            $"b11_lifecycle_{token}",
            string.Empty);
    }

    private static async Task MakePurgeEligibleAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET "Register_time" =
                    transaction_timestamp() - interval '40 days',
                deleted_at =
                    transaction_timestamp() - interval '3 days',
                restore_until =
                    transaction_timestamp() - interval '2 days',
                purge_after =
                    transaction_timestamp() - interval '1 day'
            WHERE account_id = @accountId
              AND id = @characterId
              AND lifecycle_state = 'deleted';
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "purge fixture updates exactly one tombstone");
    }

    private static async Task<StarterState> ReadStarterStateAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                character_row.fighter_job_lv,
                character_row."Money",
                character_row."Stone",
                character_row."SkillPoint",
                character_row."curHP",
                character_row."curMP",
                character_row."MaxHP",
                character_row."MaxMP",
                character_row."Map",
                character_row."Pos_X",
                character_row."Pos_Z",
                (
                    SELECT count(*)
                    FROM public.character_items item_row
                    WHERE item_row.user_id = character_row.id
                ),
                baseline.silver,
                baseline.gold
            FROM public.character_base character_row
            JOIN public.character_economy_baseline baseline
              ON baseline.character_id = character_row.id
             AND baseline.account_id = character_row.account_id
            WHERE character_row.account_id = @accountId
              AND character_row.id = @characterId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The starter character fixture is missing.");
        }

        return new StarterState(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt16(8),
            reader.GetFloat(9),
            reader.GetFloat(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13));
    }

    private static async Task<Guid> SeedCheckpointLeaseAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        int characterId,
        long generation)
    {
        var ownerId = Guid.NewGuid();
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET checkpoint_owner_id = @ownerId,
                checkpoint_owner_generation = @generation
            WHERE account_id = @accountId
              AND id = @characterId
              AND lifecycle_state = 'active';
            """);
        command.Parameters.AddWithValue("ownerId", ownerId);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "checkpoint fence fixture updates exactly one character");
        return ownerId;
    }

    private static async Task<CheckpointFence> ReadCheckpointFenceAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                checkpoint_owner_id,
                checkpoint_owner_generation
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The checkpoint fence fixture disappeared.");
        }

        return new CheckpointFence(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.GetInt64(1));
    }

    private static async Task<LifecycleDatabaseState>
        ReadLifecycleStateAsync(
            NpgsqlDataSource dataSource,
            int accountId,
            int purgedCharacterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                account_row.character_lifecycle_version,
                (
                    SELECT count(*)
                    FROM public.character_base character_row
                    WHERE character_row.id = @purgedCharacterId
                ),
                (
                    SELECT count(*)
                    FROM public.character_economy_baseline baseline
                    WHERE baseline.character_id = @purgedCharacterId
                      AND baseline.account_id = @accountId
                ),
                (
                    SELECT count(*)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = 'account'
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type =
                          'account_character_slot'
                ),
                (
                    SELECT count(*)
                    FROM public.outbox_events event_row
                    WHERE event_row.consumer_key =
                          'character_lifecycle_v1'
                      AND event_row.aggregate_type =
                          'account_character_slot'
                      AND event_row.aggregate_key = @aggregateKey
                ),
                ARRAY(
                    SELECT event_row.aggregate_version
                    FROM public.outbox_events event_row
                    WHERE event_row.consumer_key =
                          'character_lifecycle_v1'
                      AND event_row.aggregate_type =
                          'account_character_slot'
                      AND event_row.aggregate_key = @aggregateKey
                    ORDER BY event_row.aggregate_version
                )
            FROM public.accounts account_row
            WHERE account_row.id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue(
            "purgedCharacterId",
            purgedCharacterId);
        command.Parameters.AddWithValue(
            "principalKey",
            accountId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"{accountId}:0");
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The lifecycle account fixture disappeared.");
        }

        return new LifecycleDatabaseState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetFieldValue<long[]>(5));
    }

    private static async Task<OutboxDispatchState>
        ReadOutboxDispatchStateAsync(
            NpgsqlDataSource dataSource,
            int accountId,
            Guid eventId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                position.current_version,
                event_row.delivered_at IS NOT NULL
            FROM public.outbox_events event_row
            JOIN public.outbox_consumer_positions position
              ON position.consumer_key = event_row.consumer_key
             AND position.aggregate_type = event_row.aggregate_type
             AND position.aggregate_key = event_row.aggregate_key
            WHERE event_row.event_id = @eventId
              AND event_row.consumer_key =
                    'character_lifecycle_v1'
              AND event_row.aggregate_key = @aggregateKey;
            """);
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue(
            "aggregateKey",
            $"{accountId}:0");
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The lifecycle outbox dispatch fixture is missing.");
        }

        return new OutboxDispatchState(
            reader.GetInt64(0),
            reader.GetBoolean(1));
    }

    private sealed record LifecycleDatabaseState(
        long AccountVersion,
        long PurgedCharacterRows,
        long PreservedEconomyBaselines,
        long InboxReceipts,
        long OutboxEvents,
        IReadOnlyList<long> OutboxVersions);

    private readonly record struct CheckpointFence(
        Guid? OwnerId,
        long OwnerGeneration);

    private readonly record struct OutboxDispatchState(
        long CurrentVersion,
        bool Delivered);

    private readonly record struct StarterState(
        int Level,
        int Silver,
        int Gold,
        int TalentPoints,
        int CurrentHp,
        int CurrentMp,
        int MaxHp,
        int MaxMp,
        short CurrentMap,
        float PositionX,
        float PositionZ,
        long ItemCount,
        long EconomyBaselineSilver,
        long EconomyBaselineGold);
}
