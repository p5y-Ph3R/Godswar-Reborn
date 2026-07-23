using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<ExperienceBoostState> GetExperienceBoostStateAsync(
        int accountId,
        int characterId,
        byte camp,
        byte mapId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var boosts = new List<ActiveExperienceBoost>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand("""
            WITH personal AS (
                SELECT DISTINCT ON (modifier.kind)
                    modifier.status_id,
                    modifier.kind,
                    modifier.bonus_basis_points,
                    modifier.priority,
                    COALESCE(
                        modifier.remaining_online_ticks,
                        CASE WHEN modifier.expires_at IS NULL THEN NULL
                             ELSE GREATEST(
                                 0,
                                 ROUND(EXTRACT(EPOCH FROM (
                                     modifier.expires_at - modifier.activated_at
                                 )) * 10000000)::bigint)
                        END) AS remaining_online_ticks,
                    modifier.source
                FROM character_experience_modifiers modifier
                JOIN character_base character ON character.id = modifier.character_id
                WHERE modifier.character_id = @characterId
                  AND character.account_id = @accountId
                  AND modifier.activated_at <= @now
                  AND (
                      modifier.expires_at IS NULL AND modifier.remaining_online_ticks IS NULL
                      OR COALESCE(
                          modifier.remaining_online_ticks,
                          GREATEST(
                              0,
                              ROUND(EXTRACT(EPOCH FROM (
                                  modifier.expires_at - modifier.activated_at
                              )) * 10000000)::bigint)
                      ) > 0
                  )
                ORDER BY modifier.kind, modifier.priority DESC, modifier.bonus_basis_points DESC
            )
            SELECT status_id, kind, bonus_basis_points, priority, remaining_online_ticks, source
            FROM personal
            UNION ALL
            SELECT
                1504,
                1009,
                control.bonus_basis_points,
                1,
                ROUND(EXTRACT(EPOCH FROM (control.expires_at - @now)) * 10000000)::bigint,
                'world-boss:' || control.boss_template_key
            FROM faction_area_experience_control control
            JOIN world_boss_areas area
              ON area.map_id = control.map_id
             AND area.boss_template_key = control.boss_template_key
             AND area.enabled
            WHERE control.map_id = @mapId
              AND control.controlling_camp = @camp
              AND control.activated_at <= @now
              AND control.expires_at > @now
            ORDER BY kind;
            """, connection))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("camp", (short)camp);
            command.Parameters.AddWithValue("mapId", (short)mapId);
            command.Parameters.AddWithValue("now", now);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                boosts.Add(new ActiveExperienceBoost(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4)
                        ? null
                        : now + TimeSpan.FromTicks(Math.Max(0L, reader.GetInt64(4))),
                    reader.GetString(5)));
            }
        }

        await using (var command = new NpgsqlCommand("""
            SELECT vip_tier, vip_expires_at
            FROM accounts
            WHERE id = @accountId
              AND vip_tier BETWEEN 1 AND 4
              AND (vip_expires_at IS NULL OR vip_expires_at > @now);
            """, connection))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("now", now);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var tier = (VipTier)reader.GetInt16(0);
                var expiresAt = reader.IsDBNull(1)
                    ? (DateTimeOffset?)null
                    : new DateTimeOffset(reader.GetDateTime(1).ToUniversalTime());
                boosts.Add(new ActiveExperienceBoost(
                    VipExperienceBoosts.StatusId(tier),
                    ExperienceBoostKinds.Vip,
                    VipExperienceBoosts.BonusBasisPoints(tier),
                    (int)tier,
                    expiresAt,
                    $"vip:{tier.ToString().ToLowerInvariant()}"));
            }
        }

        return new ExperienceBoostState(boosts.OrderBy(boost => boost.Kind).ToArray());
    }

    public async Task ConsumeCharacterBoostOnlineTimeAsync(
        int accountId,
        int characterId,
        DateTimeOffset onlineFrom,
        DateTimeOffset onlineUntil,
        CancellationToken cancellationToken = default)
    {
        if (onlineUntil <= onlineFrom)
        {
            return;
        }

        await using var command = _dataSource.CreateCommand("""
            UPDATE character_experience_modifiers modifier
            SET remaining_online_ticks = GREATEST(
                0,
                COALESCE(
                    modifier.remaining_online_ticks,
                    GREATEST(
                        0,
                        ROUND(EXTRACT(EPOCH FROM (
                            modifier.expires_at - modifier.activated_at
                        )) * 10000000)::bigint)
                ) - GREATEST(
                    0,
                    ROUND(EXTRACT(EPOCH FROM (
                        @onlineUntil - GREATEST(@onlineFrom, modifier.activated_at)
                    )) * 10000000)::bigint
                )
            )
            FROM character_base character
            WHERE modifier.character_id = @characterId
              AND character.id = modifier.character_id
              AND character.account_id = @accountId
              AND (
                  modifier.remaining_online_ticks > 0
                  OR modifier.remaining_online_ticks IS NULL
                     AND modifier.expires_at IS NOT NULL
                     AND modifier.expires_at > modifier.activated_at
              )
              AND modifier.activated_at < @onlineUntil;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("onlineFrom", onlineFrom);
        command.Parameters.AddWithValue("onlineUntil", onlineUntil);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FactionAreaExperienceControl?> ActivateWorldBossAreaAsync(
        short mapId,
        string bossTemplateKey,
        byte controllingCamp,
        DateTimeOffset killedAt,
        string deathToken,
        CancellationToken cancellationToken = default)
    {
        if (controllingCamp is not (GameDefaults.SpartaCamp or GameDefaults.AthensCamp) ||
            string.IsNullOrWhiteSpace(bossTemplateKey) ||
            string.IsNullOrWhiteSpace(deathToken))
        {
            return null;
        }

        await using var command = _dataSource.CreateCommand("""
            INSERT INTO faction_area_experience_control (
                map_id,
                controlling_camp,
                boss_template_key,
                bonus_basis_points,
                activated_at,
                expires_at,
                death_token
            )
            SELECT
                area.map_id,
                @controllingCamp,
                area.boss_template_key,
                area.bonus_basis_points,
                @killedAt,
                @killedAt + (area.respawn_interval_seconds * interval '1 second'),
                @deathToken
            FROM world_boss_areas area
            WHERE area.map_id = @mapId
              AND area.boss_template_key = @bossTemplateKey
              AND area.enabled
            ON CONFLICT (map_id) DO UPDATE
            SET controlling_camp = EXCLUDED.controlling_camp,
                boss_template_key = EXCLUDED.boss_template_key,
                bonus_basis_points = EXCLUDED.bonus_basis_points,
                activated_at = EXCLUDED.activated_at,
                expires_at = EXCLUDED.expires_at,
                death_token = EXCLUDED.death_token
            WHERE faction_area_experience_control.death_token <> EXCLUDED.death_token
            RETURNING map_id, controlling_camp, boss_template_key, death_token,
                      bonus_basis_points, activated_at, expires_at;
            """);
        command.Parameters.AddWithValue("mapId", mapId);
        command.Parameters.AddWithValue("bossTemplateKey", bossTemplateKey);
        command.Parameters.AddWithValue("controllingCamp", (short)controllingCamp);
        command.Parameters.AddWithValue("killedAt", killedAt);
        command.Parameters.AddWithValue("deathToken", deathToken);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FactionAreaExperienceControl
        {
            MapId = checked((byte)reader.GetInt16(0)),
            ControllingCamp = checked((byte)reader.GetInt16(1)),
            BossTemplateKey = reader.GetString(2),
            DeathToken = reader.GetString(3),
            BonusBasisPoints = reader.GetInt32(4),
            ActivatedAt = new DateTimeOffset(reader.GetDateTime(5).ToUniversalTime()),
            ExpiresAt = new DateTimeOffset(reader.GetDateTime(6).ToUniversalTime())
        };
    }

    public async Task<WorldBossRespawnState?> GetActiveWorldBossRespawnAsync(
        short mapId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT control.map_id, control.boss_template_key, control.expires_at
            FROM faction_area_experience_control control
            JOIN world_boss_areas area
              ON area.map_id = control.map_id
             AND area.boss_template_key = control.boss_template_key
             AND area.enabled
            WHERE control.map_id = @mapId
              AND control.expires_at > @now;
            """);
        command.Parameters.AddWithValue("mapId", mapId);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WorldBossRespawnState(
                reader.GetInt16(0),
                reader.GetString(1),
                new DateTimeOffset(reader.GetDateTime(2).ToUniversalTime()))
            : null;
    }

}
