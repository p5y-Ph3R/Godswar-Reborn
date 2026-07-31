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
