using Npgsql;
using NpgsqlTypes;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFocusedGameplayStateIntegrationChecks
{
    private static async Task<Fixture> CreateFixtureAsync(
        NpgsqlDataSource dataSource,
        string gameplayContentRevision)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        var nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        var readAtUtc = new DateTimeOffset(
            nowTicks - nowTicks % TimeSpan.TicksPerSecond,
            TimeSpan.Zero);
        var killedAtUtc = readAtUtc.AddMinutes(-5);
        var vipExpiresAtUtc = readAtUtc.AddHours(2);
        var unconfiguredSceneKey = $"B20C_Unconfigured_{token}";
        var deathToken = $"b20c-death:{token}";

        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        var configuredAreas = await ReadConfiguredWorldBossAreasAsync(
            connection,
            transaction,
            gameplayContentRevision);
        var configuredArea = configuredAreas[0];
        var secondConfiguredArea = configuredAreas[1];
        var unconfiguredMapId = await InsertAvailableMapAsync(
            connection,
            transaction,
            unconfiguredSceneKey);
        var primaryAccountId = await InsertAccountAsync(
            connection,
            transaction,
            $"b20c_primary_{token}",
            vipTier: 3,
            vipExpiresAtUtc);
        var otherAccountId = await InsertAccountAsync(
            connection,
            transaction,
            $"b20c_other_{token}",
            vipTier: 4,
            vipExpiresAtUtc);
        await InsertAccountRealmAsync(
            connection,
            transaction,
            primaryAccountId,
            RealmId.Tempest);
        await InsertAccountRealmAsync(
            connection,
            transaction,
            otherAccountId,
            RealmId.Dwargon);
        var characterId = await InsertCharacterAsync(
            connection,
            transaction,
            primaryAccountId,
            configuredArea.MapId,
            $"B20C{token}",
            RealmId.Tempest);
        var dwargonCharacterId = await InsertCharacterAsync(
            connection,
            transaction,
            otherAccountId,
            configuredArea.MapId,
            $"B20D{token}",
            RealmId.Dwargon);
        await InsertPersonalBoostsAsync(
            connection,
            transaction,
            characterId,
            readAtUtc.AddMinutes(-10));
        await transaction.CommitAsync();

        return new Fixture(
            token,
            primaryAccountId,
            otherAccountId,
            characterId,
            dwargonCharacterId,
            configuredArea.MapId,
            secondConfiguredArea.MapId,
            unconfiguredMapId,
            configuredArea.BossTemplateKey,
            secondConfiguredArea.BossTemplateKey,
            configuredArea.BonusBasisPoints,
            configuredArea.RespawnIntervalSeconds,
            deathToken,
            readAtUtc,
            killedAtUtc,
            vipExpiresAtUtc);
    }

    private static async Task<short> InsertAvailableMapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sceneKey)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.map_templates (
                map_id,
                scene_key,
                display_name
            )
            SELECT
                candidate.map_id::smallint,
                @sceneKey,
                'B20C disposable integration map'
            FROM generate_series(30000, 32760) AS candidate(map_id)
            WHERE NOT EXISTS (
                SELECT 1
                FROM public.map_templates existing
                WHERE existing.map_id = candidate.map_id
            )
            ORDER BY candidate.map_id
            LIMIT 1
            RETURNING map_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("sceneKey", sceneKey);
        var scalar = await command.ExecuteScalarAsync();
        return scalar is short mapId
            ? mapId
            : throw new InvalidDataException(
                "No disposable PostgreSQL map ID was available.");
    }

    private static async Task<ConfiguredWorldBossArea[]>
        ReadConfiguredWorldBossAreasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string gameplayContentRevision)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                definition.map_id,
                definition.template_key,
                definition.bonus_basis_points,
                definition.respawn_interval_seconds
            FROM public.gameplay_world_boss_definitions definition
            WHERE definition.revision = @gameplayContentRevision
              AND NOT EXISTS (
                SELECT 1
                FROM public.faction_area_experience_control control
                WHERE control.map_id = definition.map_id
            )
            ORDER BY definition.map_id, definition.template_key
            LIMIT 2;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "gameplayContentRevision",
            gameplayContentRevision);
        var values = new List<ConfiguredWorldBossArea>(2);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(new ConfiguredWorldBossArea(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        return values.Count == 2
            ? values.ToArray()
            : throw new InvalidDataException(
                "Two unused published world-boss areas are required by the focused integration check.");
    }

    private static async Task<int> InsertAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string username,
        short vipTier,
        DateTimeOffset vipExpiresAtUtc)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (
                username,
                password,
                vip_tier,
                vip_expires_at
            )
            VALUES (@username, '', @vipTier, @vipExpiresAt)
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("vipTier", vipTier);
        command.Parameters.Add(
            "vipExpiresAt",
            NpgsqlDbType.TimestampTz).Value =
            vipExpiresAtUtc.UtcDateTime;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> InsertCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        short mapId,
        string name,
        RealmId realmId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id,
                name,
                camp,
                server_id,
                "Map",
                lifecycle_state
            )
            VALUES (
                @accountId,
                @name,
                0,
                @realmId,
                @mapId,
                'active'
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        command.Parameters.AddWithValue("mapId", mapId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task InsertAccountRealmAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        RealmId realmId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.account_realm (account_id, realm_id)
            VALUES (@accountId, @realmId);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertPersonalBoostsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        DateTimeOffset activatedAtUtc)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_experience_modifiers (
                character_id,
                status_id,
                kind,
                bonus_basis_points,
                priority,
                source,
                activated_at,
                expires_at,
                remaining_online_ticks
            )
            VALUES
                (
                    @characterId,
                    586,
                    14,
                    1000,
                    10,
                    'b20c-personal',
                    @activatedAt,
                    NULL,
                    @personalTicks
                ),
                (
                    @characterId,
                    580,
                    20,
                    2000,
                    10,
                    'b20c-talent',
                    @activatedAt,
                    NULL,
                    @talentTicks
                );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.Add(
            "activatedAt",
            NpgsqlDbType.TimestampTz).Value =
            activatedAtUtc.UtcDateTime;
        command.Parameters.AddWithValue(
            "personalTicks",
            TimeSpan.FromMinutes(30).Ticks);
        command.Parameters.AddWithValue(
            "talentTicks",
            TimeSpan.FromMinutes(45).Ticks);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountControlsAsync(
        NpgsqlDataSource dataSource,
        Fixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)
            FROM public.faction_area_experience_control
            WHERE realm_id = @realmId
              AND (
                  map_id = @configuredMapId
                  OR map_id = @unconfiguredMapId
              );
            """);
        command.Parameters.AddWithValue(
            "configuredMapId",
            fixture.ConfiguredMapId);
        command.Parameters.AddWithValue(
            "unconfiguredMapId",
            fixture.UnconfiguredMapId);
        command.Parameters.AddWithValue(
            "realmId",
            RealmId.Tempest.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadDeathTokenAsync(
        NpgsqlDataSource dataSource,
        short mapId,
        RealmId? realmId = null)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT death_token
            FROM public.faction_area_experience_control
            WHERE realm_id = @realmId
              AND map_id = @mapId;
            """);
        command.Parameters.AddWithValue("mapId", mapId);
        command.Parameters.AddWithValue(
            "realmId",
            (realmId ?? RealmId.Tempest).Value);
        return Convert.ToString(await command.ExecuteScalarAsync()) ??
            throw new InvalidDataException(
                "The durable world-boss death token is missing.");
    }

    private static async Task SoftDeleteCharacterAsync(
        NpgsqlDataSource dataSource,
        Fixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE public.character_base
            SET lifecycle_state = 'deleted',
                lifecycle_version = lifecycle_version + 1,
                deleted_at = GREATEST(clock_timestamp(), "Register_time"),
                restore_until =
                    GREATEST(clock_timestamp(), "Register_time") +
                    interval '7 days',
                purge_after =
                    GREATEST(clock_timestamp(), "Register_time") +
                    interval '14 days'
            WHERE account_id = @accountId
              AND id = @characterId
              AND lifecycle_state = 'active';
            """);
        command.Parameters.AddWithValue(
            "accountId",
            fixture.PrimaryAccountId);
        command.Parameters.AddWithValue(
            "characterId",
            fixture.CharacterId);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "fixture character transitions to deleted exactly once");
    }

    private static async Task DeleteFixtureAsync(
        NpgsqlDataSource dataSource,
        Fixture fixture)
    {
        await using (var controls = dataSource.CreateCommand(
            """
            DELETE FROM public.faction_area_experience_control
            WHERE (map_id = @configuredMapId
                   OR map_id = @secondConfiguredMapId)
              AND right(death_token, length(@token)) = @token;
            """))
        {
            controls.Parameters.AddWithValue(
                "configuredMapId",
                fixture.ConfiguredMapId);
            controls.Parameters.AddWithValue(
                "secondConfiguredMapId",
                fixture.SecondConfiguredMapId);
            controls.Parameters.AddWithValue("token", fixture.Token);
            await controls.ExecuteNonQueryAsync();
        }

        await using (var accounts = dataSource.CreateCommand(
            """
            DELETE FROM public.accounts
            WHERE id = @primaryAccountId
               OR id = @otherAccountId;
            """))
        {
            accounts.Parameters.AddWithValue(
                "primaryAccountId",
                fixture.PrimaryAccountId);
            accounts.Parameters.AddWithValue(
                "otherAccountId",
                fixture.OtherAccountId);
            await accounts.ExecuteNonQueryAsync();
        }

        await using var maps = dataSource.CreateCommand(
            """
            DELETE FROM public.map_templates
            WHERE map_id = @unconfiguredMapId;
            """);
        maps.Parameters.AddWithValue(
            "unconfiguredMapId",
            fixture.UnconfiguredMapId);
        await maps.ExecuteNonQueryAsync();
    }

    private sealed record Fixture(
        string Token,
        int PrimaryAccountId,
        int OtherAccountId,
        int CharacterId,
        int DwargonCharacterId,
        short ConfiguredMapId,
        short SecondConfiguredMapId,
        short UnconfiguredMapId,
        string BossTemplateKey,
        string SecondBossTemplateKey,
        int BonusBasisPoints,
        int RespawnIntervalSeconds,
        string DeathToken,
        DateTimeOffset ReadAtUtc,
        DateTimeOffset KilledAtUtc,
        DateTimeOffset VipExpiresAtUtc);

    private readonly record struct ConfiguredWorldBossArea(
        short MapId,
        string BossTemplateKey,
        int BonusBasisPoints,
        int RespawnIntervalSeconds);
}
