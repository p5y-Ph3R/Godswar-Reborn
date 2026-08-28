using System.Collections.Frozen;

namespace Godswar.Server.State;

internal readonly record struct ClientStatusAggregate(
    int Hit,
    int CriticalAppend,
    float ExperienceBonus,
    float MovementSpeedMultiplier = 1f,
    bool IsRiding = false,
    int PhysicalDefense = 0,
    int MagicDefense = 0,
    int Dodge = 0,
    int CriticalResistance = 0,
    float EquippedRidingSpeedBonus = 0f,
    HostileStatusControlFlags Control = HostileStatusControlFlags.None)
{
    public static ClientStatusAggregate Empty { get; } = new(0, 0, 0f, 1f, false);
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
    decimal MagicDamageReduction = 0m,
    int PhysicalDefenseBonus = 0,
    int MagicDefenseBonus = 0,
    int DodgeBonus = 0,
    int CriticalResistanceBonus = 0);

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
            new(344, 204, 7, 5, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 60, 24),

            // Five-star Form / Celestial Shield. Magic.ini maps Mage skills
            // 590-594 to statuses 230-234. Status.ini kind 8 supplies matching
            // physical and magical mitigation for ten minutes.
            new(590, 230, 8, 1, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0, 0.08m, 0.08m),
            new(591, 231, 8, 2, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0, 0.10m, 0.10m),
            new(592, 232, 8, 3, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0, 0.12m, 0.12m),
            new(593, 233, 8, 4, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0, 0.15m, 0.15m),
            new(594, 234, 8, 5, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0, 0.20m, 0.20m),

            // Gaia Care / Hermes' Agility. Magic.ini maps Priest skills
            // 770-774 to statuses 270-274. Status.ini kind 34 supplies the
            // same short-lived +3000 Dodge and +1000 Critical Resistance at
            // every rank; higher ranks extend the duration.
            new(770, 270, 34, 1, true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(150), 0, 0,
                DodgeBonus: 3_000, CriticalResistanceBonus: 1_000),
            new(771, 271, 34, 2, true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(120), 0, 0,
                DodgeBonus: 3_000, CriticalResistanceBonus: 1_000),
            new(772, 272, 34, 3, true, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(120), 0, 0,
                DodgeBonus: 3_000, CriticalResistanceBonus: 1_000),
            new(773, 273, 34, 4, true, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(90), 0, 0,
                DodgeBonus: 3_000, CriticalResistanceBonus: 1_000),
            new(774, 274, 34, 5, true, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(90), 0, 0,
                DodgeBonus: 3_000, CriticalResistanceBonus: 1_000),

            // Mana Shield / Shield of Aeolus. Magic.ini maps Priest skills
            // 780-784 to statuses 260-264. These are caster-centred friendly
            // area buffs; the handler uses AffectObj=3 and Range=10 for target
            // selection while this catalog owns the status values.
            new(780, 260, 9, 1, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0,
                PhysicalDefenseBonus: 20, MagicDefenseBonus: 15),
            new(781, 261, 9, 2, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0,
                PhysicalDefenseBonus: 40, MagicDefenseBonus: 30),
            new(782, 262, 9, 3, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0,
                PhysicalDefenseBonus: 100, MagicDefenseBonus: 80),
            new(783, 263, 9, 4, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0,
                PhysicalDefenseBonus: 180, MagicDefenseBonus: 140),
            new(784, 264, 9, 5, true, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(10), 0, 0,
                PhysicalDefenseBonus: 280, MagicDefenseBonus: 200)
        }.ToFrozenDictionary(static definition => definition.SkillId);

    public static bool TryGet(int skillId, out SkillStatusEffectDefinition definition) =>
        Definitions.TryGetValue(skillId, out definition);
}
