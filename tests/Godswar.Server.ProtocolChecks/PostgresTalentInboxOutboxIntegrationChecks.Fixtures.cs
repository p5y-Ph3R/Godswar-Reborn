using Godswar.Server.Infrastructure.Talents;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresTalentInboxOutboxIntegrationChecks
{
    private static async Task<TalentFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        int level,
        int talentPoints,
        int? rank = null)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario = scenario[..Math.Min(6, scenario.Length)];
        var username = $"b08_{shortScenario}_{token}";
        var characterName = $"B8{shortScenario}{token}";

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        var templateExists = false;
        await using (var template = new NpgsqlCommand(
            """
            SELECT true
            FROM public.gameplay_talent_definitions
            WHERE id = @talentId
              AND class_id = 0
              AND revision = @gameplayContentRevision;
            """,
            connection,
            transaction))
        {
            template.Parameters.AddWithValue("talentId", TalentId);
            template.Parameters.AddWithValue(
                "gameplayContentRevision",
                GameplayContentRevision);
            templateExists =
                await template.ExecuteScalarAsync() is true;
        }

        if (!templateExists)
        {
            throw new InvalidOperationException(
                "The fighter talent-zero template is missing.");
        }

        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue("username", username);
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The fixture account insert returned no identity."));
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id,
                name,
                camp,
                profession,
                fighter_job_lv,
                "SkillPoint"
            )
            VALUES (
                @accountId,
                @name,
                1,
                0,
                @level,
                @talentPoints
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue(
                "accountId",
                accountId);
            character.Parameters.AddWithValue(
                "name",
                characterName);
            character.Parameters.AddWithValue("level", level);
            character.Parameters.AddWithValue(
                "talentPoints",
                talentPoints);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The fixture character insert returned no identity."));
        }

        if (rank.HasValue)
        {
            await InsertOrUpdateTalentAsync(
                connection,
                transaction,
                characterId,
                rank.Value);
        }

        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();
        return new TalentFixture(
            accountId,
            characterId,
            username,
            characterName);
    }

    private static async Task SetTalentRankAsync(
        string connectionString,
        TalentFixture fixture,
        int rank)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await InsertOrUpdateTalentAsync(
            connection,
            transaction,
            fixture.CharacterId,
            rank);
        await transaction.CommitAsync();
    }

    private static async Task InsertOrUpdateTalentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int rank)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_talents (
                user_id,
                talent_id,
                rank,
                outbox_revision,
                updated_at
            )
            VALUES (@characterId, @talentId, @rank, 0, now())
            ON CONFLICT (user_id, talent_id) DO UPDATE
            SET rank = EXCLUDED.rank,
                outbox_revision = 0,
                updated_at = now();
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "characterId",
            characterId);
        command.Parameters.AddWithValue("talentId", TalentId);
        command.Parameters.AddWithValue(
            "rank",
            checked((short)rank));
        await command.ExecuteNonQueryAsync();
    }

    private static Task SetLevelAsync(
        string connectionString,
        TalentFixture fixture,
        int level) =>
        UpdateCharacterValueAsync(
            connectionString,
            fixture,
            "fighter_job_lv",
            level);

    private static Task SetTalentPointsAsync(
        string connectionString,
        TalentFixture fixture,
        int talentPoints) =>
        UpdateCharacterValueAsync(
            connectionString,
            fixture,
            "\"SkillPoint\"",
            talentPoints);

    private static Task SetProfessionAsync(
        string connectionString,
        TalentFixture fixture,
        int profession) =>
        UpdateCharacterValueAsync(
            connectionString,
            fixture,
            "profession",
            profession);

    private static async Task UpdateCharacterValueAsync(
        string connectionString,
        TalentFixture fixture,
        string column,
        int value)
    {
        var sql = $"""
            UPDATE public.character_base
            SET {column} = @value
            WHERE account_id = @accountId
              AND id = @characterId;
            """;
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command =
            new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue(
            "accountId",
            fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "fixture character correction affects one row");
    }

    private static async Task<DurableState> ReadStateAsync(
        string connectionString,
        TalentFixture fixture)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                cb."SkillPoint",
                COALESCE(ct.rank, 0)::integer,
                COALESCE(ct.outbox_revision, 0),
                (
                    SELECT COUNT(*)::bigint
                    FROM public.command_audit audit
                    WHERE audit.principal_type = @principalType
                      AND audit.principal_key = @principalKey
                      AND audit.aggregate_type = @aggregateType
                      AND audit.aggregate_key = @aggregateKey
                      AND audit.command_family = @commandFamily
                ),
                (
                    SELECT COUNT(*)::bigint
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ),
                (
                    SELECT COUNT(*)::bigint
                    FROM public.outbox_events outbox
                    WHERE outbox.aggregate_type = @aggregateType
                      AND outbox.aggregate_key = @aggregateKey
                ),
                COALESCE((
                    SELECT MAX(inbox.duplicate_count)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ), 0)::integer,
                COALESCE((
                    SELECT MAX(inbox.request_conflict_count)
                    FROM public.command_inbox inbox
                    WHERE inbox.principal_type = @principalType
                      AND inbox.principal_key = @principalKey
                      AND inbox.aggregate_type = @aggregateType
                      AND inbox.aggregate_key = @aggregateKey
                      AND inbox.command_family = @commandFamily
                ), 0)::integer
            FROM public.character_base cb
            LEFT JOIN public.character_talents ct
              ON ct.user_id = cb.id
             AND ct.talent_id = @talentId
            WHERE cb.account_id = @accountId
              AND cb.id = @characterId;
            """,
            connection);
        AddFixtureParameters(command, fixture);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The talent fixture character disappeared.");
        }

        return new DurableState(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt32(6),
            reader.GetInt32(7));
    }

    private static void AddFixtureParameters(
        NpgsqlCommand command,
        TalentFixture fixture)
    {
        command.Parameters.AddWithValue(
            "accountId",
            fixture.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        command.Parameters.AddWithValue("talentId", TalentId);
        command.Parameters.AddWithValue(
            "principalType",
            TalentUpgradePersistenceCodec.PrincipalType);
        command.Parameters.AddWithValue(
            "principalKey",
            PrincipalKey(fixture));
        command.Parameters.AddWithValue(
            "aggregateType",
            TalentUpgradePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue(
            "aggregateKey",
            AggregateKey(fixture));
        command.Parameters.AddWithValue(
            "commandFamily",
            TalentUpgradePersistenceCodec.CommandFamily);
    }

    private sealed record TalentFixture(
        int AccountId,
        int CharacterId,
        string Username,
        string CharacterName);

    private sealed record DurableState(
        int TalentPoints,
        int Rank,
        long OutboxRevision,
        long AuditCount,
        long InboxCount,
        long OutboxCount,
        int DuplicateCount,
        int RequestConflictCount);
}
