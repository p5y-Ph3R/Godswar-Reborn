using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server.ProtocolChecks;

internal static class PetOwnerMergeContentBaselineChecks
{
    public static Task RunAsync()
    {
        var catalog = PetOwnerMergeContentBaseline.Create();
        Check.Equal(16, catalog.EffectBases.Count,
            "owner-Merge baseline has every owner effect base");
        Check.Equal(5, catalog.Bands.Count,
            "owner-Merge baseline has five continuous Savvy bands");
        Check.Equal(95, catalog.Rates.Count,
            "owner-Merge baseline has every typed marginal rate");
        Check.Equal(
            PetOwnerMergeContentBaseline.PolicyVersion,
            catalog.Revision.PolicyVersion,
            "owner-Merge baseline exposes its policy version");
        Check.Equal(
            PetOwnerMergeContentBaseline.Source,
            catalog.Revision.Source,
            "owner-Merge baseline exposes its reviewed V3 source");
        Check.Equal(
            "3B503660F9802514F2477DE0B28C1221CFC3C872B46859021555C7FA54DC076E",
            catalog.Revision.Sha256,
            "owner-Merge V3 revision is pinned for fixture provenance");
        var v1 = new PetOwnerMergeContentManifest(
            "E6A6FA22C0D2AEE9D6B2E7C968D903E05E0576E6D40A179DC2A3715F434A4929",
            "project-pet-unite-piecewise-marginal-v2",
            16,
            5,
            95,
            "reviewed-pet-owner-merge-v1",
            Sealed: true);
        var v2 = new PetOwnerMergeContentManifest(
            "EEA02574B39EDED6DBEFCACF80337AAE0166A44366115AB7E8360DD39B36C84D",
            "project-pet-unite-piecewise-marginal-v3",
            16,
            5,
            95,
            "reviewed-pet-owner-merge-v2",
            Sealed: true);
        Check.True(
            PostgresPetOwnerMergeContentBaselinePublisher
                .IsReviewedPredecessor(v1) &&
            PostgresPetOwnerMergeContentBaselinePublisher
                .IsReviewedPredecessor(v2) &&
            !PostgresPetOwnerMergeContentBaselinePublisher
                .IsReviewedPredecessor(v2 with
                {
                    Revision = catalog.Revision.Sha256
                }) &&
            !PostgresPetOwnerMergeContentBaselinePublisher
                .IsReviewedPredecessor(v1 with
                {
                    Source = PetOwnerMergeContentBaseline.Source
                }),
            "only exact immutable reviewed V1/V2 publications auto-upgrade");

        var accuracyHit = catalog.Rates
            .Where(static value =>
                value.SourceSavvy == PetOwnerMergeSavvyStat.Accuracy &&
                value.Effect == PetOwnerMergeEffectCode.HitRate)
            .OrderBy(static value => value.BandIndex)
            .Select(static value => value.RatePerSavvy)
            .ToArray();
        Check.True(
            accuracyHit.SequenceEqual(
                [0.48m, 0.408m, 0.336m, 0.288m, 0.24m]),
            "accuracy-to-Hit uses the reviewed 100/85/70/60/50 curve");
        var agilityRebound = catalog.Rates
            .Where(static value =>
                value.SourceSavvy == PetOwnerMergeSavvyStat.Agility &&
                value.Effect == PetOwnerMergeEffectCode.DamageRebound)
            .OrderBy(static value => value.BandIndex)
            .Select(static value => value.RatePerSavvy)
            .ToArray();
        Check.True(
            agilityRebound.SequenceEqual([0m, 0m, 0m, 0m, 0m]),
            "Agility contributes zero Damage Rebound in every band");
        var luckRebound = catalog.Rates
            .Where(static value =>
                value.SourceSavvy == PetOwnerMergeSavvyStat.Luck &&
                value.Effect == PetOwnerMergeEffectCode.DamageRebound)
            .OrderBy(static value => value.BandIndex)
            .Select(static value => value.RatePerSavvy)
            .ToArray();
        Check.True(
            luckRebound.SequenceEqual([6m, 5.1m, 4.2m, 3.6m, 3m]),
            "Luck retains the reviewed Damage Rebound curve");
        var techniquePhysicalCancellation = catalog.Rates
            .Where(static value =>
                value.SourceSavvy == PetOwnerMergeSavvyStat.Technique &&
                value.Effect ==
                    PetOwnerMergeEffectCode.PhysicalDamageReduction)
            .OrderBy(static value => value.BandIndex)
            .Select(static value => value.RatePerSavvy)
            .ToArray();
        var techniqueMagicCancellation = catalog.Rates
            .Where(static value =>
                value.SourceSavvy == PetOwnerMergeSavvyStat.Technique &&
                value.Effect ==
                    PetOwnerMergeEffectCode.MagicDamageReduction)
            .OrderBy(static value => value.BandIndex)
            .Select(static value => value.RatePerSavvy)
            .ToArray();
        Check.True(
            techniquePhysicalCancellation.SequenceEqual(
                [12m, 10.2m, 8.4m, 7.2m, 6m]) &&
            techniqueMagicCancellation.SequenceEqual(
                [10m, 8.5m, 7m, 6m, 5m]),
            "V3 doubles both complete native fixed-cancellation curves");

        foreach (var curve in catalog.Rates.GroupBy(static value =>
                     (value.SourceSavvy, value.Effect)))
        {
            var rates = curve
                .OrderBy(static value => value.BandIndex)
                .Select(static value => value.RatePerSavvy)
                .ToArray();
            Check.True(
                rates.Length == 5 &&
                rates[1] == rates[0] * 0.85m &&
                rates[2] == rates[0] * 0.70m &&
                rates[3] == rates[0] * 0.60m &&
                rates[4] == rates[0] * 0.50m,
                $"owner-Merge curve {curve.Key} uses reviewed multipliers");
        }

        return Task.CompletedTask;
    }
}
