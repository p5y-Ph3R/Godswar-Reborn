using System.Collections.Frozen;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal enum MonsterAttackDamageKind : short
{
    Physical = 1,
    Magical = 2,
    // Stock content uses type 3 for turrets and special/boss templates. Its
    // wire identity is preserved; authored V1 mitigates it as physical until
    // a capture-backed channel rule is available.
    Special = 3
}

/// <summary>
/// Versioned authored combat ratings for a spawned monster. Attack type is
/// recovered content; numeric ratings are the explicit Reborn V1 balance
/// policy because the stock resources do not publish those values.
/// </summary>
internal readonly record struct MonsterCombatProfile(
    MonsterAttackDamageKind AttackKind,
    int Level,
    int PhysicalAttack,
    int MagicAttack,
    int PhysicalDefense,
    int MagicDefense,
    int Hit,
    int Dodge,
    int Critical,
    int CriticalResistance,
    bool IsElite,
    bool IsBoss)
{
    public bool UsesMagicDamage =>
        AttackKind == MonsterAttackDamageKind.Magical;

    public CombatAttackerStats ToAttackerStats() => new()
    {
        Level = Level,
        Profession = UsesMagicDamage
            ? (byte)3
            : (byte)0,
        PhysicalAttack = this.PhysicalAttack,
        MagicAttack = this.MagicAttack,
        Hit = this.Hit,
        Critical = this.Critical
    };

    public CombatTargetStats ToTargetStats() => new()
    {
        Level = Level,
        PhysicalDefense = this.PhysicalDefense,
        MagicDefense = this.MagicDefense,
        Dodge = this.Dodge,
        CriticalResistance = this.CriticalResistance
    };
}

internal sealed class MonsterCombatProfileCatalog
{
    internal const string PolicyVersion = "reborn-monster-combat-v1";

    private readonly FrozenDictionary<(short MapId, string TemplateKey),
        TemplateProfile> _exact;
    private readonly FrozenDictionary<string, TemplateProfile> _fallback;

    private MonsterCombatProfileCatalog(
        FrozenDictionary<(short MapId, string TemplateKey), TemplateProfile>
            exact,
        FrozenDictionary<string, TemplateProfile> fallback)
    {
        _exact = exact;
        _fallback = fallback;
    }

    public static MonsterCombatProfileCatalog Empty { get; } = Create(
        GameplayContentCatalog.Empty);

    public static MonsterCombatProfileCatalog Create(
        GameplayContentCatalog gameplay)
    {
        ArgumentNullException.ThrowIfNull(gameplay);
        var entries = gameplay.MonsterTemplates
            .Select(static value => new KeyValuePair<
                GameplayMonsterTemplateDefinition,
                TemplateProfile>(
                value,
                TemplateProfile.From(value)))
            .ToArray();
        var exactGroups = entries
            .Where(static value => value.Key.SourceMapId.HasValue)
            .GroupBy(static value => (
                value.Key.SourceMapId!.Value,
                value.Key.TemplateKey))
            .ToArray();
        ValidateUnambiguous(exactGroups.Select(static group =>
            group.Select(static value => value.Value)));

        var fallbackGroups = entries
            .GroupBy(
                static value => value.Key.TemplateKey,
                StringComparer.Ordinal)
            .Where(static group =>
                !group.Select(static value => value.Value)
                    .Distinct()
                    .Skip(1)
                    .Any())
            .ToArray();

        return new MonsterCombatProfileCatalog(
            exactGroups.ToFrozenDictionary(
                static group => group.Key,
                static group => group.First().Value),
            fallbackGroups.ToFrozenDictionary(
                static group => group.Key,
                static group => group.First().Value,
                StringComparer.Ordinal));
    }

    public MonsterCombatProfile Resolve(CapturedMonsterSpawn monster)
    {
        ArgumentNullException.ThrowIfNull(monster);
        var known = _exact.TryGetValue(
                (monster.MapId, monster.TemplateKey),
                out var template) ||
            _fallback.TryGetValue(monster.TemplateKey, out template);
        template = known ? template : TemplateProfile.Default;
        return Resolve(
            monster.Tier,
            template,
            authoritativeIsElite: known && template.IsElite,
            // Unknown published identity fails closed for boss-control
            // immunity without changing the default combat-rating scale.
            authoritativeIsBoss: !known || template.IsBoss);
    }

    internal static MonsterCombatProfile Resolve(
        uint tier,
        MonsterAttackDamageKind attackKind,
        bool isElite = false,
        bool isBoss = false) =>
        Resolve(
            tier,
            new TemplateProfile(attackKind, isElite, isBoss),
            isElite,
            isBoss);

    private static MonsterCombatProfile Resolve(
        uint tier,
        TemplateProfile template,
        bool authoritativeIsElite,
        bool authoritativeIsBoss)
    {
        var level = (int)Math.Clamp(tier, 1u, 10_000u);
        var rankScale = template.IsBoss
            ? 13_000
            : template.IsElite
                ? 11_500
                : 10_000;
        var physicalAttack = Scale(
            21L + (3L * level) + (level / 3L),
            rankScale);
        var magicAttack = Scale(
            23L + (3L * level) + (level / 2L),
            rankScale);
        var square = (long)level * level;
        return new MonsterCombatProfile(
            template.AttackKind,
            level,
            physicalAttack,
            magicAttack,
            Scale(10L + (6L * level) + (square / 10L), rankScale),
            Scale(10L + (5L * level) + (square / 12L), rankScale),
            Scale(100L + (20L * level), rankScale),
            Scale(50L + (12L * level), rankScale),
            Scale(25L + (8L * level), rankScale),
            Scale(25L + (10L * level), rankScale),
            authoritativeIsElite,
            authoritativeIsBoss);
    }

    private static int Scale(long value, int basisPoints) =>
        (int)Math.Clamp(
            (value * basisPoints) / 10_000L,
            0L,
            int.MaxValue);

    private static void ValidateUnambiguous(
        IEnumerable<IEnumerable<TemplateProfile>> groups)
    {
        if (groups.Any(static values => values.Distinct().Skip(1).Any()))
        {
            throw new InvalidDataException(
                "Published monster templates contain conflicting attack " +
                "types or combat ranks for one template identity.");
        }
    }

    private readonly record struct TemplateProfile(
        MonsterAttackDamageKind AttackKind,
        bool IsElite,
        bool IsBoss)
    {
        public static TemplateProfile Default { get; } = new(
            MonsterAttackDamageKind.Physical,
            false,
            false);

        public static TemplateProfile From(
            GameplayMonsterTemplateDefinition definition) =>
            new(
                definition.AttackType switch
                {
                    2 => MonsterAttackDamageKind.Magical,
                    3 => MonsterAttackDamageKind.Special,
                    _ => MonsterAttackDamageKind.Physical
                },
                definition.IsElite,
                definition.IsBoss);
    }
}
