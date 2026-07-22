using System.Collections.Frozen;

namespace Godswar.Server.State;

internal readonly record struct ClientStatusAggregate(
    int Hit,
    int CriticalAppend,
    float ExperienceBonus)
{
    public static ClientStatusAggregate Empty { get; } = new(0, 0, 0f);
}

internal readonly record struct SkillStatusEffectDefinition(
    int SkillId,
    uint StatusId,
    int Kind,
    int Priority,
    bool Beneficial,
    TimeSpan Duration,
    TimeSpan Cooldown,
    int HitBonus,
    int CriticalAppendBonus,
    decimal PhysicalDamageReduction = 0m,
    decimal MagicDamageReduction = 0m);

/// <summary>
/// Active-skill status data copied from Magic.ini and Status.ini. Keeping the
/// mapping server-side lets a cast update both the native status list and its
/// aggregate StatusData fields without relying on client-side inference.
/// </summary>
internal static class SkillStatusEffectCatalog
{
    private static readonly FrozenDictionary<int, SkillStatusEffectDefinition> Definitions =
        new SkillStatusEffectDefinition[]
        {
            // Holy Ward / Apollo's Shield. Magic.ini maps skills 90-94 to
            // statuses 160-164; Status.ini supplies the received-damage
            // reductions below. All five ranks replace the same kind-6 buff.
            new(90, 160, 6, 2, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0, 0.10m, 0m),
            new(91, 161, 6, 3, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0, 0.13m, 0m),
            new(92, 162, 6, 4, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0, 0.16m, 0.05m),
            new(93, 163, 6, 5, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0, 0.20m, 0.10m),
            new(94, 164, 6, 6, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0, 0.25m, 0.15m),
            new(340, 200, 7, 1, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 10, 4),
            new(341, 201, 7, 2, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 20, 8),
            new(342, 202, 7, 3, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 30, 12),
            new(343, 203, 7, 4, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 45, 18),
            new(344, 204, 7, 5, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 60, 24)
        }.ToFrozenDictionary(static definition => definition.SkillId);

    public static bool TryGet(int skillId, out SkillStatusEffectDefinition definition) =>
        Definitions.TryGetValue(skillId, out definition);
}
