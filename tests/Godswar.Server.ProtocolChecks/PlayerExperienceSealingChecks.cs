using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerExperienceSealingChecks
{
    public const string CheckName =
        "Fighter level-seal progression and saturation";

    public static Task RunAsync()
    {
        Check.Equal(
            89,
            PlayerExperienceCatalog.FighterLevelSealLevel,
            "the original client seals only fighter level 89");
        Check.Equal(
            4_294_967_295L,
            PlayerExperienceCatalog.MaximumStoredExperience,
            "stored fighter EXP uses the full UInt32 client ceiling");

        var belowThreshold = PlayerExperienceCatalog.Apply(
            89,
            100,
            50,
            fighterLevelSealed: true);
        AssertSealed(
            belowThreshold,
            expectedExperience: 150,
            expectedCredit: 50,
            "below-threshold reward");

        var threshold =
            PlayerExperienceCatalog.GetNextLevelExperience(89);
        var crossing = PlayerExperienceCatalog.Apply(
            89,
            threshold - 10,
            20,
            fighterLevelSealed: true);
        AssertSealed(
            crossing,
            threshold + 10,
            expectedCredit: 20,
            "threshold-crossing reward");

        var partial = PlayerExperienceCatalog.Apply(
            89,
            4_294_967_290L,
            20,
            fighterLevelSealed: true);
        AssertSealed(
            partial,
            4_294_967_295L,
            expectedCredit: 5,
            "saturating partial reward");

        var saturated = PlayerExperienceCatalog.Apply(
            89,
            4_294_967_295L,
            20,
            fighterLevelSealed: true);
        AssertSealed(
            saturated,
            4_294_967_295L,
            expectedCredit: 0,
            "already-saturated reward");

        var ordinary = PlayerExperienceCatalog.Apply(
            89,
            threshold - 10,
            20);
        Check.True(
            ordinary.Level == 90 &&
            ordinary.Experience == 10 &&
            ordinary.ExperienceGained == 20 &&
            ordinary.LevelUps.Count == 1,
            "unsealed fighter progression retains normal level-up behavior");

        var catchUp = PlayerExperienceCatalog.Apply(
            89,
            4_294_967_295L,
            100_000_000);
        Check.True(
            catchUp.Level > 89 &&
            catchUp.Experience is >= 0 and <= 4_294_967_295L &&
            catchUp.LevelUps.Count > 0 &&
            catchUp.LevelUps.All(static levelUp =>
                levelUp.CurrentExperience is
                    >= 0 and <= 4_294_967_295L),
            "unsealed catch-up retains normal progression with UInt32-safe evidence");

        Check.Throws<InvalidOperationException>(
            () => PlayerExperienceCatalog.Apply(
                88,
                0,
                1,
                fighterLevelSealed: true),
            "a sealed non-89 fighter state fails closed");

        var reachesLevelCap = PlayerExperienceCatalog.Apply(
            199,
            PlayerExperienceCatalog.GetNextLevelExperience(199) - 1L,
            1);
        Check.True(
            reachesLevelCap.Level == PlayerExperienceCatalog.MaximumLevel &&
            reachesLevelCap.Experience == 0 &&
            reachesLevelCap.LevelUps.Count == 1,
            "ordinary progression reaches level 200 with zero carried EXP");
        var cappedMaximum =
            PlayerExperienceCatalog.GetClientExperienceMaximum(
                reachesLevelCap.Level,
                fighterLevelSealed: false);
        Check.Equal(
            PlayerExperienceCatalog.MaximumStoredExperience,
            cappedMaximum,
            "level 200 resolves the UInt32 client EXP-bar maximum");
        var cappedPacket = PacketBuilder.PlayerLevelUp(
            2,
            reachesLevelCap.Level,
            cappedMaximum,
            reachesLevelCap.Experience,
            1,
            1,
            1,
            1);
        Check.Equal(
            uint.MaxValue,
            BinaryPrimitives.ReadUInt32LittleEndian(
                cappedPacket.AsSpan(12, sizeof(uint))),
            "the live level-200 packet publishes the UInt32 EXP-bar maximum");
        return Task.CompletedTask;
    }

    private static void AssertSealed(
        PlayerExperienceProgression progression,
        long expectedExperience,
        int expectedCredit,
        string scenario)
    {
        Check.True(
            progression.Level == 89 &&
            progression.Experience == expectedExperience &&
            progression.ExperienceGained == expectedCredit &&
            progression.LevelUps.Count == 0,
            $"{scenario} stays level 89 with actual credited EXP and no level-up evidence");
    }
}
