using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetOwnerMergeChecks
{
    private static readonly IPetOwnerMergeContentCatalog OwnerMergeContent =
        PetOwnerMergeContentBaseline.Create();

    public static Task RunAsync()
    {
        CheckStockBaseAndAllFirstBands();
        CheckContinuousAgilityBoundaries();
        CheckFinalRoundingAndEffectRoundTrip();
        CheckTechniqueReductionPolicy();
        CheckTwentyThousandSavvyGoldenVectors();
        CheckPlayerVisibleSavvyTotal();
        CheckSoulContractEffectiveSavvy();
        CheckDurableReceiptContract();
        CheckAuthoritativeProjectionCoverage();
        return Task.CompletedTask;
    }

    private static void CheckPlayerVisibleSavvyTotal()
    {
        var initial = new PetSavvy(
            2658.653337m,
            2657.023502m,
            2724.372752m,
            2515.867248m,
            2581.576663m,
            2583.206498m);
        var legacyAdded = new PetSavvy(
            16.767423m,
            15.081458m,
            16.762208m,
            13.337792m,
            13.332577m,
            15.018542m);
        var hatchBasic = new PetSavvy(
            663.33m,
            862.33m,
            729.67m,
            928.67m,
            995m,
            796m);
        var acceleration =
            new PetSavvy(0.1m, 0.2m, 0.3m, 0.4m, 0.5m, 0.6m);
        var expectedAdded =
            PetSavvyRuntimeSemantics.ResolveLevelScaledAdded(
                120,
                legacyAdded,
                acceleration);

        Check.Equal(
            initial + expectedAdded,
            PetSavvyRuntimeSemantics.ResolvePlayerVisibleTotal(
                120,
                initial,
                expectedAdded,
                legacyAdded,
                acceleration,
                hatchBasic),
            "scaled-Added v3 owner Merge includes Basic plus materialized Added");
        Check.Equal(
            initial + legacyAdded,
            PetSavvyRuntimeSemantics.ResolvePlayerVisibleTotal(
                120,
                initial,
                legacyAdded,
                legacyAdded,
                acceleration,
                PetSavvy.Zero),
            "legacy owner Merge preserves initial-plus-added semantics");
        Check.Throws<InvalidDataException>(
            () => PetSavvyRuntimeSemantics.ResolvePlayerVisibleTotal(
                120,
                initial,
                legacyAdded,
                legacyAdded,
                acceleration,
                hatchBasic),
            "scaled-Added v3 owner Merge rejects stale materialization");
        Check.Throws<InvalidDataException>(
            () => PetSavvyRuntimeSemantics.ResolvePlayerVisibleTotal(
                120,
                initial,
                legacyAdded,
                legacyAdded,
                acceleration,
                hatchBasic with { Luck = 0m }),
            "partial scaled-Added provenance fails closed");
    }

    private static void CheckSoulContractEffectiveSavvy()
    {
        var raw = new PetSavvy(10m, 10m, 10m, 10m, 10m, 10m);
        var unsigned = CreateOwnerMergePet(raw, stage: 0);
        var noSpirit = CreateOwnerMergePet(raw, stage: 1);
        var fiveSpirits = CreateOwnerMergePet(raw, stage: 6);

        Check.True(
            unsigned.TotalSavvy == raw &&
            noSpirit.TotalSavvy == raw &&
            fiveSpirits.TotalSavvy == raw,
            "Soul Contract never rewrites owner-Merge raw Basic/Added");
        Check.Equal(
            raw,
            unsigned.EffectiveTotalSavvy,
            "stage zero has no effective owner-Merge bonus");
        Check.Equal(
            new PetSavvy(13m, 13m, 13m, 13m, 13m, 13m),
            noSpirit.EffectiveTotalSavvy,
            "zero-spirit stage adds three to every effective trait");
        Check.Equal(
            new PetSavvy(18m, 18m, 18m, 18m, 18m, 18m),
            fiveSpirits.EffectiveTotalSavvy,
            "five-spirit stage adds eight to every effective trait");

        var baseline = PetOwnerMergeContributionCalculator.Calculate(
            unsigned.EffectiveTotalSavvy,
            OwnerMergeContent);
        var stageOne = PetOwnerMergeContributionCalculator.Calculate(
            noSpirit.EffectiveTotalSavvy,
            OwnerMergeContent);
        var stageSix = PetOwnerMergeContributionCalculator.Calculate(
            fiveSpirits.EffectiveTotalSavvy,
            OwnerMergeContent);
        Check.True(
            stageOne.MaxHealth - baseline.MaxHealth == 150m &&
            stageSix.MaxHealth - baseline.MaxHealth == 400m &&
            stageOne.PhysicalAttack - baseline.PhysicalAttack == 9m &&
            stageSix.PhysicalAttack - baseline.PhysicalAttack == 24m &&
            baseline.TechniquePhysicalReduction == 2m &&
            stageOne.TechniquePhysicalReduction == 2m &&
            stageSix.TechniquePhysicalReduction == 3m,
            "stock Soul stages change authoritative owner-Merge contributions");
    }

    private static OwnedPet CreateOwnerMergePet(
        PetSavvy raw,
        byte stage) =>
        new(
            PetId: 71,
            OwnerCharacterId: 2,
            SpeciesType: 1,
            Name: "Soul Contract Unite",
            Level: 1,
            Experience: 0,
            Rank: 1m,
            Aptitude: PetAptitude.Weak,
            InitialSavvy: raw,
            AddedSavvy: PetSavvy.Zero,
            BaseGrowthRates: PetSavvy.Zero,
            GrowthAcceleration: PetSavvy.Zero,
            CompletedPetMerges: 0,
            CompletedRebirths: 0,
            RebirthsRemaining: 1,
            HasSoulContract: stage > 0,
            HasOwnerMergeTalent: true,
            IsBound: false,
            IsSummoned: true,
            IsAway: false,
            CurrentEnergy: 100,
            MaximumEnergy: 100,
            Amity: 100,
            OwnerMerge: null,
            SoulContractStage: stage);

    private static void CheckStockBaseAndAllFirstBands()
    {
        var baseline = PetOwnerMergeContributionCalculator.Calculate(
            PetSavvy.Zero,
            OwnerMergeContent);
        Check.Equal(4000m, baseline.MaxHealth, "Merge base HP");
        Check.Equal(300m, baseline.MaxMana, "Merge base MP");
        Check.Equal(20m, baseline.HitRate, "Merge base hit");
        Check.Equal(10m, baseline.DodgeRate, "Merge base dodge");
        Check.Equal(100m, baseline.PhysicalAttack, "Merge base physical attack");
        Check.Equal(80m, baseline.PhysicalDefense, "Merge base physical defense");
        Check.Equal(80m, baseline.MagicAttack, "Merge base magic attack");
        Check.Equal(60m, baseline.MagicDefense, "Merge base magic defense");
        Check.Equal(80m, baseline.DamageAbsorption, "Merge base absorption");
        Check.Equal(200m, baseline.PhysicalDamageIncrease, "Merge base physical increase");
        Check.Equal(150m, baseline.MagicDamageIncrease, "Merge base magic increase");
        Check.Equal(600m, baseline.PhysicalDamageReduction, "Merge base physical fixed cancellation");
        Check.Equal(480m, baseline.MagicDamageReduction, "Merge base magic fixed cancellation");
        Check.Equal(800m, baseline.CriticalDamageReduction, "Merge base critical reduction");
        Check.Equal(100m, baseline.LifeAbsorption, "Merge base life absorption");
        Check.Equal(150m, baseline.DamageRebound, "Merge base rebound");

        var allSixty = PetOwnerMergeContributionCalculator.Calculate(
            new PetSavvy(60m, 60m, 60m, 60m, 60m, 60m),
            OwnerMergeContent);
        var expected = new PetOwnerStatContribution(
            MaxHealth: 7000m,
            HitRate: 56m,
            PhysicalAttack: 280m,
            PhysicalDamageIncrease: 500m,
            PhysicalDefense: 200m,
            PhysicalDamageReduction: 1320m,
            DamageAbsorption: 170m,
            LifeAbsorption: 400m,
            MaxMana: 540m,
            DodgeRate: 40m,
            MagicAttack: 200m,
            MagicDamageIncrease: 390m,
            MagicDefense: 150m,
            MagicDamageReduction: 1080m,
            CriticalDamageReduction: 1700m,
            DamageRebound: 510m,
            TechniquePhysicalReduction: 9m,
            TechniqueMagicReduction: 9m);
        Check.Equal(expected, allSixty,
            "all six stock first-band Merge curves map exactly");

        var maxLuck = PetOwnerMergeContributionCalculator.Calculate(
            new PetSavvy(0m, 0m, 0m, 0m, 0m, 20_000m),
            OwnerMergeContent);
        Check.Equal(
            60_879m,
            maxLuck.DamageRebound,
            "20k Luck retains base plus the full reviewed rebound curve");
    }

    private static void CheckContinuousAgilityBoundaries()
    {
        CheckAgility(
            60m,
            maxMana: 540m,
            hit: 27.2m,
            magicAttack: 200m,
            rebound: 150m);
        CheckAgility(
            150m,
            maxMana: 846m,
            hit: 36.38m,
            magicAttack: 353m,
            rebound: 150m);
        CheckAgility(
            300m,
            maxMana: 1266m,
            hit: 48.98m,
            magicAttack: 563m,
            rebound: 150m);
        CheckAgility(
            600m,
            maxMana: 1986m,
            hit: 70.58m,
            magicAttack: 923m,
            rebound: 150m);
        CheckAgility(
            601m,
            maxMana: 1988m,
            hit: 70.64m,
            magicAttack: 924m,
            rebound: 150m);

        var below = PetOwnerMergeContributionCalculator.Calculate(
            new PetSavvy(59.999m, 0m, 0m, 0m, 0m, 0m),
            OwnerMergeContent);
        var at = PetOwnerMergeContributionCalculator.Calculate(
            new PetSavvy(60m, 0m, 0m, 0m, 0m, 0m),
            OwnerMergeContent);
        var above = PetOwnerMergeContributionCalculator.Calculate(
            new PetSavvy(60.001m, 0m, 0m, 0m, 0m, 0m),
            OwnerMergeContent);
        Check.True(
            below.MaxMana < at.MaxMana && at.MaxMana < above.MaxMana,
            "declining Merge rates remain continuous and monotonic at 60");
    }

    private static void CheckAgility(
        decimal agility,
        decimal maxMana,
        decimal hit,
        decimal magicAttack,
        decimal rebound)
    {
        var value = PetOwnerMergeContributionCalculator.Calculate(
            new PetSavvy(agility, 0m, 0m, 0m, 0m, 0m),
            OwnerMergeContent);
        Check.True(
            value.MaxMana == maxMana &&
            value.HitRate == hit &&
            value.MagicAttack == magicAttack &&
            value.DamageRebound == rebound,
            $"Agility Merge golden vector at {agility}");
    }

    private static void CheckFinalRoundingAndEffectRoundTrip()
    {
        var rounded = PetOwnerMergeContributionCalculator.Calculate(
            new PetSavvy(0.000005m, 0m, 0m, 0m, 0m, 0m),
            OwnerMergeContent);
        Check.Equal(
            20.000001m,
            rounded.HitRate,
            "Merge rounds final channel away from midpoint at six decimals");

        var effects = PetOwnerMergeContributionCalculator.ToEffectValues(
            rounded);
        Check.True(
            effects.Count == 16 &&
            effects.Select(static value => value.Effect).Distinct().Count() == 16,
            "Merge persists every typed effect exactly once");
        Check.Equal(
            rounded,
            PetOwnerMergeContributionCalculator.FromEffectValues(effects),
            "Merge effect rows round-trip without positional coupling");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetOwnerMergeContributionCalculator.Calculate(
                new PetSavvy(-1m, 0m, 0m, 0m, 0m, 0m),
                OwnerMergeContent),
            "Merge rejects negative authoritative savvy");
        Check.Throws<InvalidDataException>(
            () => PetOwnerMergeContributionCalculator.FromEffectValues(
            [
                new(PetOwnerMergeEffectCode.MaxHealth, 1m),
                new(PetOwnerMergeEffectCode.MaxHealth, 2m)
            ]),
            "Merge rejects duplicate persisted effect rows");
    }

    private static void CheckTechniqueReductionPolicy()
    {
        Check.Equal(
            0m,
            PetOwnerMergeContributionCalculator
                .CalculateTechniqueReductionBasisPoints(3.333m),
            "Technique percentage reduction rounds below its first midpoint");
        Check.Equal(
            1m,
            PetOwnerMergeContributionCalculator
                .CalculateTechniqueReductionBasisPoints(3.334m),
            "Technique percentage reduction rounds away above its first midpoint");
        Check.Equal(
            3_000m,
            PetOwnerMergeContributionCalculator
                .CalculateTechniqueReductionBasisPoints(20_000m),
            "Technique percentage reduction reaches its thirty-percent cap");
        Check.Equal(
            3_000m,
            PetOwnerMergeContributionCalculator
                .CalculateTechniqueReductionBasisPoints(200_000m),
            "Technique percentage reduction remains capped");

        var contribution = PetOwnerMergeContributionCalculator.Calculate(
            new PetSavvy(0m, 0m, 0m, 20_000m, 0m, 0m),
            OwnerMergeContent);
        var native = PetOwnerMergeContributionCalculator
            .ToEffectValues(contribution);
        var stored = PetOwnerMergeStoredBonusCodec
            .ToStoredValues(contribution);
        Check.True(
            native.Count == 16 &&
            native.All(static value =>
                (short)value.Effect is not 1001 and not 1002) &&
            stored.Count == 18 &&
            stored.Count(static value => value.Code >= 1001) == 2,
            "server-only Technique reductions never enter native PetUnite fields");
        Check.Equal(
            contribution,
            PetOwnerMergeStoredBonusCodec.FromStoredValues(stored),
            "native and internal owner-Merge rows round-trip together");
    }

    private static void CheckDurableReceiptContract()
    {
        foreach (var status in new[]
        {
            PetDurableReceiptStatus.OwnerMerged,
            PetDurableReceiptStatus.OwnerUnmerged
        })
        {
            var receipt = new PetDurableReceipt(
                Godswar.Server.Application.Commands.CommandFamily
                    .BagItemActivation,
                status,
                AccountId: 13,
                CharacterId: 2,
                KitBagSlot: 4,
                EquipmentSlot: -1,
                PetId: 71,
                PetLevel: 120,
                PetExperience: 0,
                PetRevision: 9,
                IsCarried: true,
                IsSummoned: true,
                PresenceOperation: 0,
                AggregateRevision: 11,
                AuditReference: "owner-merge-check",
                OutboxEventId: Guid.NewGuid());
            receipt.Validate();
            Check.True(receipt.Succeeded, $"{status} is a committed receipt");
        }

        var rejected = new PetDurableReceipt(
            Godswar.Server.Application.Commands.CommandFamily
                .BagItemActivation,
            PetDurableReceiptStatus.OwnerMergeEnergyNotFull,
            AccountId: 13,
            CharacterId: 2,
            KitBagSlot: 4,
            EquipmentSlot: -1,
            PetId: 71,
            PetLevel: 120,
            PetExperience: 0,
            PetRevision: 8,
            IsCarried: true,
            IsSummoned: true,
            PresenceOperation: 0,
            AggregateRevision: 10,
            AuditReference: "owner-merge-rejected",
            OutboxEventId: null);
        rejected.Validate();
        Check.True(!rejected.Succeeded,
            "Merge precondition rejection is terminal and non-mutating");
    }

    private static void CheckAuthoritativeProjectionCoverage()
    {
        var sql = PostgresCharacterRuntimeItemProjectionSql
            .CalculatedStatsForCharacter;
        Check.True(
            sql.Contains("pet.contributes_to_character", StringComparison.Ordinal) &&
            sql.Contains("character_pet_character_bonuses", StringComparison.Ordinal) &&
            sql.Contains("physical_append_damage", StringComparison.Ordinal) &&
            sql.Contains("magic_append_damage", StringComparison.Ordinal) &&
            sql.Contains("physical_flat_absorption", StringComparison.Ordinal) &&
            sql.Contains("magic_flat_absorption", StringComparison.Ordinal) &&
            sql.Contains("critical_damage_flat_reduction", StringComparison.Ordinal) &&
            sql.Contains("life_absorption_flat", StringComparison.Ordinal) &&
            sql.Contains("damage_rebound_flat", StringComparison.Ordinal) &&
            sql.Contains("physical_damage_reduction", StringComparison.Ordinal) &&
            sql.Contains("magic_damage_reduction", StringComparison.Ordinal),
            "calculated stats include all Merge-only typed channels behind the authoritative flag");
        foreach (var code in Enum.GetValues<PetOwnerMergeEffectCode>())
        {
            Check.True(
                PostgresCharacterPetOwnerMergeProjectionSql
                    .CommonTableExpression.Contains(
                        $"WHEN {(short)code} THEN",
                        StringComparison.Ordinal),
                $"Merge projection maps effect code {(short)code}");
        }
        foreach (var code in Enum.GetValues<PetOwnerMergeInternalBonusCode>())
        {
            Check.True(
                PostgresCharacterPetOwnerMergeProjectionSql
                    .CommonTableExpression.Contains(
                        $"WHEN {(short)code} THEN",
                        StringComparison.Ordinal),
                $"Merge projection maps internal code {(short)code}");
        }
        Check.True(
            sql.Contains("WHEN 29 THEN 'physical_flat_absorption'", StringComparison.Ordinal) &&
            sql.Contains("WHEN 30 THEN 'magic_flat_absorption'", StringComparison.Ordinal) &&
            sql.Contains("WHEN 1001 THEN 'physical_damage_reduction'", StringComparison.Ordinal) &&
            sql.Contains("WHEN 1002 THEN 'magic_damage_reduction'", StringComparison.Ordinal),
            "native fixed cancellation and Reborn percentage reduction stay separate");
    }
}
