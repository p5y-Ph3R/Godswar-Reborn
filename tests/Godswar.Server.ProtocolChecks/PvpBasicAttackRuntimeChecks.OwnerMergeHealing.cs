using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PvpBasicAttackRuntimeChecks
{
    private static async Task CheckFixedLifeAbsorptionAsync()
    {
        await using var attackerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var attacker = Player(
            700,
            GameDefaults.SpartaCamp,
            physicalAttack: 1_000,
            hit: 5_000,
            lifeAbsorptionFlat: 7);
        attacker.CurrentHp = 9_990;
        var target = Player(
            800,
            GameDefaults.AthensCamp,
            physicalDefense: 100,
            dodge: 0);
        var registry = Registry();
        Join(registry, attackerSocket, attacker);
        Join(registry, targetSocket, target);
        var revision = FindRevision(
            attacker,
            target,
            static resolution => resolution.Hit);

        var decision = await registry.ResolvePvpBasicAttackAsync(
            attackerSocket.Session,
            WorldObjectIds.ForPlayer(target.Id),
            attacker.PositionX,
            attacker.PositionZ,
            revision,
            DateTimeOffset.Parse("2026-08-21T00:00:00Z"),
            CancellationToken.None);

        Check.True(
            decision.Accepted &&
            decision.AppliedDamage > 0 &&
            decision.LifeAbsorptionHealing == 7 &&
            decision.AttackerCurrentHealth == 9_997 &&
            attacker.CurrentHp == 9_997,
            "admitted PvP commits fixed owner-Merge on-hit healing exactly once");
        registry.Remove(attackerSocket.Session);
        registry.Remove(targetSocket.Session);
    }
}
