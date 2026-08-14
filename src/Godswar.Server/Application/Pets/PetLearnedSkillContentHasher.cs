using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.Pets;

internal static class PetLearnedSkillContentHasher
{
    public static string Compute(
        string sourceSha256,
        IReadOnlyList<PetLearnedSkillCurveContentDefinition> curves)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        ArgumentNullException.ThrowIfNull(curves);
        var canonical = new StringBuilder(
            "pet-learned-skill-content-v1\n");
        canonical.Append("source-sha256:").Append(sourceSha256).Append('\n');
        foreach (var curve in curves
                     .OrderBy(static value => value.FamilyType)
                     .ThenBy(static value => value.Priority))
        {
            var trait = curve.LearnTraitRequirement;
            canonical.Append(curve.FamilyType).Append('|')
                .Append(curve.Priority).Append('|')
                .Append(curve.Genre).Append('|')
                .Append(curve.Effect).Append('|')
                .Append(curve.OpaqueAdd).Append('|')
                .Append(curve.OpaqueFlag).Append('|')
                .Append(Decimal(trait.Agility)).Append(',')
                .Append(Decimal(trait.Strength)).Append(',')
                .Append(Decimal(trait.Accuracy)).Append(',')
                .Append(Decimal(trait.Technique)).Append(',')
                .Append(Decimal(trait.Wisdom)).Append(',')
                .Append(Decimal(trait.Luck)).Append('|')
                .Append(curve.FirstRuntimeSkillId).Append('|');
            foreach (var step in curve.Steps.OrderBy(static x => x.StepOrder))
            {
                canonical.Append(step.StepOrder).Append(':')
                    .Append(step.RuntimeSkillId).Append(':')
                    .Append(step.MinimumPetRank).Append(':')
                    .Append(Decimal(step.AbsoluteValue)).Append(',');
            }
            canonical.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string Decimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);
}
