using Godswar.Server.Application.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game.WorldInstances;

internal static partial class MedusaIslandCombatOverride
{
    /// <summary>
    /// Applies the final typed-damage rule after every earlier combat
    /// adjustment. The CombatResolution is deliberately required so callers
    /// cannot submit channelless raw damage through this policy.
    /// </summary>
    internal static CombatResolution ApplyFinalIncomingDamage(
        MedusaEncounterDifficulty difficulty,
        MedusaEncounterEnemyRole role,
        in CombatResolution source)
    {
        _ = ResolveEnemyDefinition(difficulty, role);
        var channel = ResolveMedusaDamageChannel(source.Channel);
        if (role is not (
                MedusaEncounterEnemyRole.Stheno or
                MedusaEncounterEnemyRole.Medusa))
        {
            return source;
        }

        var basisPoints = MedusaIslandEncounterPolicy
            .IncomingDamageBasisPoints(role, channel);
        if (basisPoints ==
            MedusaIslandEncounterPolicy.FullIncomingDamageBasisPoints)
        {
            return source;
        }

        if (basisPoints <= 0 ||
            basisPoints >
                MedusaIslandEncounterPolicy.FullIncomingDamageBasisPoints)
        {
            throw new InvalidOperationException(
                $"Medusa Island {role}/{channel} has an invalid final " +
                $"damage multiplier of {basisPoints} basis points.");
        }

        if (!source.Hit || source.Damage == 0)
        {
            return source;
        }

        const ulong roundingHalf =
            MedusaIslandEncounterPolicy.FullIncomingDamageBasisPoints / 2;
        var scaled = (
            ((ulong)source.Damage * (uint)basisPoints) + roundingHalf) /
            MedusaIslandEncounterPolicy.FullIncomingDamageBasisPoints;
        var adjustedDamage = checked((uint)Math.Max(1UL, scaled));
        return source with
        {
            Damage = adjustedDamage
        };
    }

    private static MedusaDamageChannel ResolveMedusaDamageChannel(
        CombatDamageChannel channel) => channel switch
        {
            CombatDamageChannel.Physical => MedusaDamageChannel.Physical,
            CombatDamageChannel.Magic => MedusaDamageChannel.Magical,
            _ => throw new ArgumentOutOfRangeException(
                nameof(channel),
                channel,
                "Medusa Island damage must declare a physical or magical channel.")
        };
}
