using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private sealed record OwnerMergePersistenceState(
        bool Contributes,
        long PetRevision,
        int CurrentEnergy,
        long BonusCount,
        long InventoryRevision,
        long AuditCount,
        long CommittedAuditCount,
        long RejectedAuditCount,
        long EmptyConsumedAuditCount);

    private sealed record OwnerMergeEffectState(
        short EffectCode,
        decimal EffectValue,
        long Revision);

    private sealed record OwnerMergeProjectedStats(
        int MaxHp,
        int MaxMp,
        int PhysicalAttack,
        int PhysicalDefense,
        int MagicAttack,
        int MagicDefense,
        int Hit,
        int Dodge,
        int DamageAbsorb,
        int PhysicalAppendDamage,
        int MagicAppendDamage,
        int PhysicalDamageReduction,
        int MagicDamageReduction,
        int PhysicalFlatAbsorption,
        int MagicFlatAbsorption,
        int CriticalDamageFlatReduction,
        int LifeAbsorptionFlat,
        int DamageReboundFlat)
    {
        public static OwnerMergeProjectedStats From(CharacterStats stats) =>
            new(
                stats.MaxHp,
                stats.MaxMp,
                stats.PhysicalAttack,
                stats.PhysicalDefense,
                stats.MagicAttack,
                stats.MagicDefense,
                stats.Hit,
                stats.Dodge,
                stats.DamageAbsorb,
                stats.PhysicalAppendDamage,
                stats.MagicAppendDamage,
                stats.PhysicalDamageReduction,
                stats.MagicDamageReduction,
                stats.PhysicalFlatAbsorption,
                stats.MagicFlatAbsorption,
                stats.CriticalDamageFlatReduction,
                stats.LifeAbsorptionFlat,
                stats.DamageReboundFlat);

        public bool IsStrictlyGreaterThan(OwnerMergeProjectedStats other) =>
            MaxHp > other.MaxHp &&
            MaxMp > other.MaxMp &&
            PhysicalAttack > other.PhysicalAttack &&
            PhysicalDefense > other.PhysicalDefense &&
            MagicAttack > other.MagicAttack &&
            MagicDefense > other.MagicDefense &&
            Hit > other.Hit &&
            Dodge > other.Dodge &&
            DamageAbsorb > other.DamageAbsorb &&
            PhysicalAppendDamage > other.PhysicalAppendDamage &&
            MagicAppendDamage > other.MagicAppendDamage &&
            PhysicalDamageReduction > other.PhysicalDamageReduction &&
            MagicDamageReduction > other.MagicDamageReduction &&
            PhysicalFlatAbsorption > other.PhysicalFlatAbsorption &&
            MagicFlatAbsorption > other.MagicFlatAbsorption &&
            CriticalDamageFlatReduction >
                other.CriticalDamageFlatReduction &&
            LifeAbsorptionFlat > other.LifeAbsorptionFlat &&
            DamageReboundFlat > other.DamageReboundFlat;
    }
}
