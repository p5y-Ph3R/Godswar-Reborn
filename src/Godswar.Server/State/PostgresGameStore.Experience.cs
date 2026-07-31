using Godswar.Server.Application.Progression;
using Godswar.Server.Application.World;

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
        var snapshot = await _experienceBoostStateReader.ReadAsync(
            new ExperienceBoostReadRequest(
                accountId,
                characterId,
                camp,
                mapId,
                now),
            cancellationToken);
        return new ExperienceBoostState(
            snapshot.ActiveBoosts
                .Select(static boost => new ActiveExperienceBoost(
                    boost.StatusId,
                    boost.Kind,
                    boost.BonusBasisPoints,
                    boost.Priority,
                    boost.ExpiresAtUtc,
                    boost.Source))
                .ToArray());
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

    public async Task<FactionAreaExperienceControl?>
        ActivateWorldBossAreaAsync(
            short mapId,
            string bossTemplateKey,
            byte controllingCamp,
            DateTimeOffset killedAt,
            string deathToken,
            CancellationToken cancellationToken = default)
    {
        // The legacy JSON-shaped model can only represent byte map IDs.
        if (mapId is < byte.MinValue or > byte.MaxValue)
        {
            return null;
        }

        var result = await _worldBossAreaControlStore.ActivateAsync(
            new WorldBossAreaActivation(
                mapId,
                bossTemplateKey,
                controllingCamp,
                killedAt,
                deathToken),
            cancellationToken);
        if (result.Disposition !=
                WorldBossAreaActivationDisposition.Committed ||
            result.Control is null)
        {
            return null;
        }

        var control = result.Control;
        return new FactionAreaExperienceControl
        {
            MapId = checked((byte)control.MapId),
            ControllingCamp = control.ControllingCamp,
            BossTemplateKey = control.BossTemplateKey,
            DeathToken = control.DeathToken,
            BonusBasisPoints = control.BonusBasisPoints,
            ActivatedAt = control.ActivatedAtUtc,
            ExpiresAt = control.ExpiresAtUtc
        };
    }

    public async Task<WorldBossRespawnState?>
        GetActiveWorldBossRespawnAsync(
            short mapId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
    {
        var respawn = await _worldBossAreaControlStore.ReadActiveAsync(
            new WorldBossRespawnReadRequest(mapId, now),
            cancellationToken);
        return respawn is null
            ? null
            : new WorldBossRespawnState(
                respawn.MapId,
                respawn.BossTemplateKey,
                respawn.RespawnAtUtc);
    }
}
