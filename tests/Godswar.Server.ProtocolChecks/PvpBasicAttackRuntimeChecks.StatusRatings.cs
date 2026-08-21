using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PvpBasicAttackRuntimeChecks
{
    private static async Task CheckRuntimeStatusRatingsAsync()
    {
        foreach (var mode in new[]
                 {
                     PlayerRuntimeMode.Legacy,
                     PlayerRuntimeMode.Ecs
                 })
        {
            await CheckRuntimeStatusRatingsAsync(mode);
        }
    }

    private static async Task CheckRuntimeStatusRatingsAsync(
        PlayerRuntimeMode mode)
    {
        await using var attackerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var attacker = Player(
            900 + (int)mode,
            GameDefaults.SpartaCamp,
            physicalAttack: 1,
            hit: 3_000,
            critical: 500);
        var target = Player(
            910 + (int)mode,
            GameDefaults.AthensCamp,
            physicalDefense: 0,
            dodge: 3_500,
            criticalResistance: 430);
        var registry = Registry(mode);
        Join(registry, attackerSocket, attacker);
        Join(registry, targetSocket, target);

        if (!SkillStatusEffectCatalog.TryGet(344, out var sacredZeal) ||
            !SkillStatusEffectCatalog.TryGet(774, out var gaiaCare))
        {
            throw new InvalidOperationException(
                "Authoritative PvP status fixtures are missing.");
        }
        var now = DateTimeOffset.UtcNow;
        Check.True(
            await registry.ApplyRuntimeStatusAndPublishAsync(
                attackerSocket.Session,
                sacredZeal,
                now,
                "pvp-rating-sacred-zeal",
                CancellationToken.None) &&
            await registry.ApplyRuntimeStatusAndPublishAsync(
                targetSocket.Session,
                gaiaCare,
                now,
                "pvp-rating-gaia-care",
                CancellationToken.None),
            $"{mode} installs the offensive and defensive rating statuses");

        var protectedDecision = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            admittedCombatRevision: 1,
            now,
            CancellationToken.None);
        Check.True(
            protectedDecision.Accepted &&
            protectedDecision.Resolution.Rolls.HitChanceBasisPoints == 500 &&
            protectedDecision.Resolution.Rolls
                .CriticalChanceBasisPoints == 2_681,
            $"{mode} combat consumes Sacred Zeal and Gaia Care ratings");

        var gaiaExpired = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            admittedCombatRevision: 2,
            now + gaiaCare.Duration,
            CancellationToken.None);
        Check.True(
            gaiaExpired.Accepted &&
            gaiaExpired.Resolution.Rolls.HitChanceBasisPoints == 3_720 &&
            gaiaExpired.Resolution.Rolls
                .CriticalChanceBasisPoints == 5_492,
            $"{mode} excludes Gaia Care exactly at its expiry boundary");

        var allExpired = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            admittedCombatRevision: 3,
            now + sacredZeal.Duration,
            CancellationToken.None);
        Check.True(
            allExpired.Accepted &&
            allExpired.Resolution.Rolls.HitChanceBasisPoints == 3_000 &&
            allExpired.Resolution.Rolls
                .CriticalChanceBasisPoints == 5_376,
            $"{mode} excludes every rating modifier at exact expiry");

        registry.Remove(attackerSocket.Session);
        registry.Remove(targetSocket.Session);
    }
}
