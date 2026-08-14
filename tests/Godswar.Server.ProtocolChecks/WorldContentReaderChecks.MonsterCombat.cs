using Godswar.Server.Application.World;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldContentReaderChecks
{
    private static void CheckMonsterCombatAuthority()
    {
        var physical = MonsterCombatProfileCatalog.Resolve(
            3,
            MonsterAttackDamageKind.Physical);
        Check.Equal(31, physical.PhysicalAttack,
            "authored monster V1 retains the captured tier-three physical attack");
        Check.True(
            physical.AttackKind == MonsterAttackDamageKind.Physical,
            "physical monster attack type remains explicit");
        Check.True(
            physical.PhysicalDefense > 0 &&
            physical.MagicDefense > 0 &&
            physical.Hit > 0 &&
            physical.Dodge > 0 &&
            physical.Critical > 0 &&
            physical.CriticalResistance > 0,
            "authored monster V1 supplies all deterministic defense and rating channels");

        var magicalBoss = MonsterCombatProfileCatalog.Resolve(
            120,
            MonsterAttackDamageKind.Magical,
            isBoss: true);
        Check.True(
            magicalBoss.AttackKind == MonsterAttackDamageKind.Magical,
            "published magical attack type selects magical combat authority");
        Check.True(
            magicalBoss.MagicAttack > physical.PhysicalAttack &&
            magicalBoss.PhysicalDefense > physical.PhysicalDefense,
            "tier and rank deterministically scale monster combat authority");

        var special = MonsterCombatProfileCatalog.Resolve(
            120,
            MonsterAttackDamageKind.Special,
            isBoss: true);
        Check.True(
            special.AttackKind == MonsterAttackDamageKind.Special &&
            !special.UsesMagicDamage,
            "stock attack type three preserves its wire identity and uses the reviewed physical fallback");

        var baseDefinition = new GameplayMonsterTemplateDefinition(
            "source",
            "map",
            1,
            "map",
            "monster",
            "Monster",
            "normal",
            false,
            false,
            false,
            1,
            2.5f);
        var physicalContent = GameplayContentCatalog.Empty with
        {
            MonsterTemplates = [baseDefinition]
        };
        var magicalContent = physicalContent with
        {
            MonsterTemplates = [baseDefinition with { AttackType = 2 }]
        };
        var specialContent = physicalContent with
        {
            MonsterTemplates = [baseDefinition with { AttackType = 3 }]
        };
        Check.True(
            WorldContentRevisionHasher.HashGameplay(physicalContent).Sha256 !=
                WorldContentRevisionHasher.HashGameplay(magicalContent).Sha256 &&
            WorldContentRevisionHasher.HashGameplay(magicalContent).Sha256 !=
                WorldContentRevisionHasher.HashGameplay(specialContent).Sha256,
            "monster attack type participates in the sealed gameplay revision");
    }
}
