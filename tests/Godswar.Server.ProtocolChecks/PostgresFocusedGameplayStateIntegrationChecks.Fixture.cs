using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresFocusedGameplayStateIntegrationChecks
{
    private static async Task<Fixture> CreateFixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var token = Guid.NewGuid().ToString("N")[..10];
        var nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
        var readAtUtc = new DateTimeOffset(
            nowTicks - nowTicks % TimeSpan.TicksPerSecond,
            TimeSpan.Zero);
        var killedAtUtc = readAtUtc.AddMinutes(-5);
        var vipExpiresAtUtc = readAtUtc.AddHours(2);
        var configuredSceneKey = $"B20C_Configured_{token}";
        var unconfiguredSceneKey = $"B20C_Unconfigured_{token}";
        var bossTemplateKey = $"b20c-boss-{token}";
        var deathToken = $"b20c-death:{token}";

        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        var configuredMapId = await InsertAvailableMapAsync(
            connection,
            transaction,
            configuredSceneKey);
        var unconfiguredMapId = await InsertAvailableMapAsync(
            connection,
            transaction,
            unconfiguredSceneKey);
        await InsertWorldBossPolicyAsync(
            connection,
            transaction,
            configuredMapId,
            bossTemplateKey);
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
        var characterId = await InsertCharacterAsync(
            connection,
            transaction,
            primaryAccountId,
            configuredMapId,
            $"B20C{token}");
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
            configuredMapId,
            unconfiguredMapId,
            configuredSceneKey,
            unconfiguredSceneKey,
            bossTemplateKey,
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

    private static async Task InsertWorldBossPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        short mapId,
        string bossTemplateKey)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.world_boss_areas (
                map_id,
                boss_template_key,
                boss_display_name,
                bonus_basis_points,
                respawn_interval_seconds,
                enabled
            )
            VALUES (
                @mapId,
                @bossTemplateKey,
                'B20C Boss',
                2500,
                43200,
                true
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("mapId", mapId);
        command.Parameters.AddWithValue(
            "bossTemplateKey",
            bossTemplateKey);
        await command.ExecuteNonQueryAsync();
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
        string name)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id,
                name,
                camp,
                "Map",
                lifecycle_state
            )
            VALUES (@accountId, @name, 0, @mapId, 'active')
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("mapId", mapId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
            WHERE map_id = @configuredMapId
               OR map_id = @unconfiguredMapId;
            """);
        command.Parameters.AddWithValue(
            "configuredMapId",
            fixture.ConfiguredMapId);
        command.Parameters.AddWithValue(
            "unconfiguredMapId",
            fixture.UnconfiguredMapId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadDeathTokenAsync(
        NpgsqlDataSource dataSource,
        short mapId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT death_token
            FROM public.faction_area_experience_control
            WHERE map_id = @mapId;
            """);
        command.Parameters.AddWithValue("mapId", mapId);
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
            WHERE map_id = @configuredMapId
               OR map_id = @unconfiguredMapId;
            """);
        maps.Parameters.AddWithValue(
            "configuredMapId",
            fixture.ConfiguredMapId);
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
        short ConfiguredMapId,
        short UnconfiguredMapId,
        string ConfiguredSceneKey,
        string UnconfiguredSceneKey,
        string BossTemplateKey,
        string DeathToken,
        DateTimeOffset ReadAtUtc,
        DateTimeOffset KilledAtUtc,
        DateTimeOffset VipExpiresAtUtc);
}
