using System.Collections.Frozen;

namespace Godswar.Server.State;

internal readonly record struct MonsterStunSkillDefinition(
    int SkillId,
    uint StatusId,
    TimeSpan Duration,
    TimeSpan Cooldown,
    int StatusOdds,
    int Priority);

/// <summary>
/// Warrior Stun values copied from Magic.ini (skills 70-74) and Status.ini
/// (status 331). StatusOdds is preserved as a rating rather than treated as a
/// percentage (every level is already above 100): the original game also
/// combines it with duration-spell hit and resistance ratings, which are not
/// yet part of monster combat. Until that scale and monster resistance data are
/// recovered, a valid stun is applied deterministically instead of inventing a
/// misleading probability formula.
/// </summary>
internal static class MonsterStunSkillCatalog
{
    internal const uint StunnedStatusId = 331;
    internal static readonly TimeSpan StunDuration = TimeSpan.FromSeconds(3);

    private static readonly FrozenDictionary<int, MonsterStunSkillDefinition> Definitions =
        new MonsterStunSkillDefinition[]
        {
            new(70, StunnedStatusId, StunDuration, TimeSpan.FromSeconds(30), 150, 1),
            new(71, StunnedStatusId, StunDuration, TimeSpan.FromSeconds(26), 190, 2),
            new(72, StunnedStatusId, StunDuration, TimeSpan.FromSeconds(23), 200, 3),
            new(73, StunnedStatusId, StunDuration, TimeSpan.FromSeconds(20), 230, 4),
            new(74, StunnedStatusId, StunDuration, TimeSpan.FromSeconds(18), 250, 5)
        }.ToFrozenDictionary(static definition => definition.SkillId);

    public static int Count => Definitions.Count;

    public static bool TryGet(int skillId, out MonsterStunSkillDefinition definition) =>
        Definitions.TryGetValue(skillId, out definition);
}
