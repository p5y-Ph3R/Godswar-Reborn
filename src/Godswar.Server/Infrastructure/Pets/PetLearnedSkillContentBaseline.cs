using System.Globalization;
using Godswar.Server.Application.Pets;

namespace Godswar.Server.Infrastructure.Pets;

internal static partial class PetLearnedSkillContentBaseline
{
    public const string Source =
        "installed-en-us-pet-skill-normalized-v1";
    public const string SourceSha256 =
        "B2EE9219E5E804AFA34797D6D2BCB8787B7C1C6EDF7914F4C1A6AC982A553F43";
    public const string ExpectedRevision =
        "64748AC27B0D815B9C30CFF78A7CE8AD519AE83DF528CB5CDFF4374503ABB473";

    public static PinnedPetLearnedSkillContentCatalog Create() =>
        PinnedPetLearnedSkillContentCatalog.Create(
            Source,
            SourceSha256,
            Parse(Data1).Concat(Parse(Data2)).ToArray(),
            ExpectedRevision);

    private static IEnumerable<PetLearnedSkillCurveContentDefinition> Parse(
        string data)
    {
        foreach (var line in data.Split(
                     '\n',
                     StringSplitOptions.TrimEntries |
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('|');
            if (fields.Length != 10)
            {
                throw new InvalidDataException(
                    "A normalized learned pet-skill curve is malformed.");
            }
            var traits = Decimals(fields[6]);
            var ranks = Shorts(fields[8]);
            var values = Decimals(fields[9]);
            if (ranks.Length != values.Length)
            {
                throw new InvalidDataException(
                    "A learned pet-skill curve has mismatched rank/value steps.");
            }
            var firstRuntimeId = Integer(fields[7]);
            yield return new PetLearnedSkillCurveContentDefinition(
                Integer(fields[0]),
                Short(fields[1]),
                Integer(fields[2]),
                Integer(fields[3]),
                Integer(fields[4]),
                Integer(fields[5]),
                new PetSkillTraitRequirement(
                    traits[0], traits[1], traits[2],
                    traits[3], traits[4], traits[5]),
                firstRuntimeId,
                Array.AsReadOnly(ranks.Select((rank, index) =>
                        new PetLearnedSkillStepContentDefinition(
                            checked((short)index),
                            checked(firstRuntimeId + index),
                            rank,
                            values[index]))
                    .ToArray()));
        }
    }

    private static int Integer(string value) =>
        int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static short Short(string value) => checked((short)Integer(value));

    private static short[] Shorts(string value) =>
        value.Split(',').Select(Short).ToArray();

    private static decimal[] Decimals(string value) =>
        value.Split(',').Select(static item => decimal.Parse(
            item,
            NumberStyles.Number,
            CultureInfo.InvariantCulture)).ToArray();
}
