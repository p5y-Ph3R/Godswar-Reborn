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
        int PhysicalDamageBonus,
        int MagicDamageBonus,
        int PhysicalDamageReduction,
        int MagicDamageReduction,
        int CriticalDamageReduction,
        int LifeAbsorption,
        int DamageRebound)
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
                stats.PhysicalDamageBonus,
                stats.MagicDamageBonus,
                stats.PhysicalDamageReduction,
                stats.MagicDamageReduction,
                stats.CriticalDamageReduction,
                stats.LifeAbsorption,
                stats.DamageRebound);

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
            PhysicalDamageBonus > other.PhysicalDamageBonus &&
            MagicDamageBonus > other.MagicDamageBonus &&
            PhysicalDamageReduction > other.PhysicalDamageReduction &&
            MagicDamageReduction > other.MagicDamageReduction &&
            CriticalDamageReduction > other.CriticalDamageReduction &&
            LifeAbsorption > other.LifeAbsorption &&
            DamageRebound > other.DamageRebound;
    }
}
