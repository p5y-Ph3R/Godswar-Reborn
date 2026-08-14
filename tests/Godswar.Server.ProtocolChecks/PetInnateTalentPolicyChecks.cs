using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetInnateTalentPolicyChecks
{
    public static Task RunAsync()
    {
        foreach (var aptitude in PetAptitudeCatalog.All)
        {
            var expected = aptitude.Aptitude >= PetAptitude.Godly
                ? (byte)31
                : aptitude.Aptitude >= PetAptitude.Smart
                    ? (byte)26
                    : (byte)0;
            var actual = PetInnateTalentPolicy.Resolve(aptitude.Aptitude);
            Check.Equal(
                expected,
                actual,
                $"{aptitude.DisplayName} receives its exact innate talents");

            foreach (var talent in PetTalentCatalog.All)
            {
                Check.Equal(
                    (expected & talent.MaskBit) != 0,
                    PetInnateTalentPolicy.HasTalent(
                        aptitude.Aptitude,
                        talent.Talent),
                    $"{aptitude.DisplayName}/{talent.DisplayName} membership");
            }
        }

        var baseline = PetContentBaseline.Create();
        foreach (var aptitude in baseline.Aptitudes)
        {
            Check.Equal(
                checked((short)PetInnateTalentPolicy.Resolve(
                    (PetAptitude)aptitude.Aptitude)),
                aptitude.InnateTalentMask,
                $"published aptitude {aptitude.Aptitude} pins its talent mask");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => PetInnateTalentPolicy.Resolve((PetAptitude)0),
            "unknown aptitude cannot receive an invented talent mask");
        Check.True(
            !PetInnateTalentPolicy.HasTalent(
                PetAptitude.Smart,
                (PetTalentKind)99),
            "unknown talents fail closed");
        return Task.CompletedTask;
    }
}
