using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task
        ReloadDurableEquipmentBagTransferProjectionAsync(
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken,
            EquipmentBagTransferExecutionReceipt?
                committedReceipt = null)
    {
        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account!.Id,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            throw new InvalidOperationException(
                "The equipment owner changed during projection reload.");
        }

        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot);
        if (hydrated is null ||
            hydrated.Character.Id != _character!.Id)
        {
            throw new InvalidDataException(
                "The durable equipment/bag transfer character could not " +
                "be reloaded.");
        }
        if (committedReceipt is not null)
        {
            ValidateCommittedEquipmentBagTransferProjection(
                hydrated.Character,
                committedReceipt);
        }

        ApplyDurableEquipmentBagTransferProjection(
            _character,
            hydrated.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        _pendingUnequipFollowup = null;
        ClearForgeSelection();
        ClearGearEnhancerSelection();
    }

    internal static void ApplyDurableEquipmentBagTransferProjection(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        if (liveCharacter.Id != persistedCharacter.Id ||
            liveCharacter.AccountId != persistedCharacter.AccountId)
        {
            throw new InvalidDataException(
                "An equipment/bag transfer projection cannot change " +
                "character identity.");
        }
        if (persistedCharacter.CalculatedStats is null)
        {
            throw new InvalidDataException(
                "An equipment/bag transfer projection requires " +
                "calculated stats.");
        }

        var fashionHidden =
            ResolveFashionHiddenAfterEquipmentChange(
                liveCharacter,
                persistedCharacter);
        liveCharacter.Equipment = persistedCharacter.Equipment;
        liveCharacter.FashionHidden = fashionHidden;
        liveCharacter.KitBag = persistedCharacter.KitBag;
        liveCharacter.HolySuitPoints =
            persistedCharacter.HolySuitPoints;
        ApplyDurableEquipmentStatsProjection(
            liveCharacter,
            persistedCharacter.CalculatedStats);
        ApplyElementalPassiveStats(
            liveCharacter,
            persistedCharacter.CalculatedStats);
    }

    private static void ApplyDurableEquipmentStatsProjection(
        GameCharacter liveCharacter,
        CharacterStats persistedStats)
    {
        lock (liveCharacter.VitalsSync)
        {
            var maxHp = Math.Max(1, persistedStats.MaxHp);
            var maxMp = Math.Max(0, persistedStats.MaxMp);
            var currentHp = Math.Clamp(
                liveCharacter.CurrentHp,
                0,
                maxHp);
            var currentMp = Math.Clamp(
                liveCharacter.CurrentMp,
                0,
                maxMp);
            var projectedStats = new CharacterStats
            {
                CharacterId = persistedStats.CharacterId,
                AccountId = persistedStats.AccountId,
                Name = persistedStats.Name,
                Profession = persistedStats.Profession,
                Level = persistedStats.Level,
                MaxHp = maxHp,
                MaxMp = maxMp,
                CurrentHp = currentHp,
                CurrentMp = currentMp,
                PhysicalAttack = persistedStats.PhysicalAttack,
                PhysicalDefense = persistedStats.PhysicalDefense,
                MagicAttack = persistedStats.MagicAttack,
                MagicDefense = persistedStats.MagicDefense,
                Hit = persistedStats.Hit,
                Dodge = persistedStats.Dodge,
                Critical = persistedStats.Critical,
                CriticalResistance =
                    persistedStats.CriticalResistance,
                DamageAbsorb = persistedStats.DamageAbsorb,
                PhysicalDamageBonus =
                    persistedStats.PhysicalDamageBonus,
                MagicDamageBonus = persistedStats.MagicDamageBonus,
                CureBonus = persistedStats.CureBonus,
                BeCureBonus = persistedStats.BeCureBonus,
                HpRecovery = persistedStats.HpRecovery,
                MpRecovery = persistedStats.MpRecovery,
                IgnorePhysicalDefense =
                    persistedStats.IgnorePhysicalDefense,
                IgnoreMagicDefense =
                    persistedStats.IgnoreMagicDefense,
                PhysicalAppendDamage =
                    persistedStats.PhysicalAppendDamage,
                MagicAppendDamage =
                    persistedStats.MagicAppendDamage,
                CriticalDamagePercent =
                    persistedStats.CriticalDamagePercent,
                CriticalDamageFlat =
                    persistedStats.CriticalDamageFlat,
                PhysicalDamageReduction =
                    persistedStats.PhysicalDamageReduction,
                MagicDamageReduction =
                    persistedStats.MagicDamageReduction,
                CriticalDamageReduction =
                    persistedStats.CriticalDamageReduction,
                LifeAbsorption = persistedStats.LifeAbsorption,
                DamageRebound = persistedStats.DamageRebound,
                PhysicalFlatAbsorption =
                    persistedStats.PhysicalFlatAbsorption,
                MagicFlatAbsorption =
                    persistedStats.MagicFlatAbsorption,
                CriticalDamageFlatReduction =
                    persistedStats.CriticalDamageFlatReduction,
                DamageReboundFlat = persistedStats.DamageReboundFlat,
                BasicAttackIntervalMilliseconds =
                    persistedStats.BasicAttackIntervalMilliseconds,
                BasicAttackRange = persistedStats.BasicAttackRange,
                WeaponScore = persistedStats.WeaponScore,
                WeaponRank = persistedStats.WeaponRank,
                WeaponAuraEffect = persistedStats.WeaponAuraEffect,
                ArmorScore = persistedStats.ArmorScore,
                ArmorRank = persistedStats.ArmorRank,
                ArmorAuraEffect = persistedStats.ArmorAuraEffect,
                LearnedSkillCount = persistedStats.LearnedSkillCount
            };

            liveCharacter.MaxHp = maxHp;
            liveCharacter.MaxMp = maxMp;
            liveCharacter.CurrentHp = currentHp;
            liveCharacter.CurrentMp = currentMp;
            liveCharacter.WeaponRank = projectedStats.WeaponRank;
            liveCharacter.WeaponAuraEffect =
                projectedStats.WeaponAuraEffect;
            liveCharacter.ArmorRank = projectedStats.ArmorRank;
            liveCharacter.ArmorAuraEffect =
                projectedStats.ArmorAuraEffect;
            liveCharacter.CalculatedStats = projectedStats;
            liveCharacter.MarkVitalsChanged();
        }
    }
}
