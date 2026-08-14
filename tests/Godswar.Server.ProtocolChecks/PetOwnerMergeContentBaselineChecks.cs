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
