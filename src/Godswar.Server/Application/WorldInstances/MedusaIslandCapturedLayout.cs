using System.Collections.Immutable;
using static Godswar.Server.Application.WorldInstances.MedusaIslandRosterTemplateAliases;
using static Godswar.Server.Application.WorldInstances.MedusaIslandRosterMechanic;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Monster identities and positions observed in the captured stock-compatible
/// Medusa run on 2026-08-27. Internal spawn IDs remain stable for encounter
/// mechanics; template and placement data follow the capture.
/// </summary>
internal static class MedusaIslandCapturedLayout
{
    public static readonly ImmutableArray<MedusaIslandCapturedSpawn> Spawns =
        Build();

    private static ImmutableArray<MedusaIslandCapturedSpawn> Build()
    {
        var spawns = ImmutableArray.CreateBuilder<MedusaIslandCapturedSpawn>(136);

        Normal(spawns, "First-Normal-01", PikemanA, 164.247f, -169.563f, Stun);
        Normal(spawns, "First-Normal-02", PikemanA, 163.218f, -169.012f, Stun);
        Elite(spawns, "E1-Elite", EliteCrazyAxemanC, 163.004f, -169.114f, Stun);
        Normal(spawns, "First-Normal-03", MudCrocodile, 146.085f, -207.588f, Stun);
        Normal(spawns, "First-Normal-04", MudCrocodile, 146.806f, -207.990f, Stun);
        Elite(spawns, "E2-Elite", EliteMudCrocodile, 146.058f, -207.949f, Stun);
        Elite(spawns, "E3-Elite", EliteMudCrocodile, 111.656f, -198.980f, Stun);
        Normal(spawns, "First-Normal-05", MudCrocodile, 109.231f, -201.752f, Stun);
        Normal(spawns, "First-Normal-06", MudCrocodile, 108.468f, -200.880f, Stun);

        Elite(spawns, "E4-Elite", ElitePriestA12, 60.029f, -181.045f, Freeze);
        Normal(spawns, "First-Normal-07", JungleWizard, 59.321f, -180.228f, Freeze);
        Normal(spawns, "First-Normal-08", JungleWizard, 59.432f, -181.067f, Freeze);
        Normal(spawns, "First-Normal-09", JungleWizard, 49.534f, -142.388f, Freeze);
        Normal(spawns, "First-Normal-10", JungleWizard, 49.959f, -141.570f, Freeze);
        Elite(spawns, "E5-Elite", ElitePriestB12, 48.297f, -138.979f, Freeze);
        Elite(spawns, "E6-Elite", EliteShamanC9, -3.184f, -119.766f, Freeze);
        Normal(spawns, "First-Normal-11", PikemanB, -5.076f, -117.727f, Freeze);
        Normal(spawns, "First-Normal-12", PikemanB, -5.558f, -116.803f, Freeze);
        Elite(spawns, "E7-Elite", EliteHammerSoldier, -10.536f, -83.539f, Freeze);
        Elite(spawns, "E8-Elite", EliteGorgonPriestC14, -39.313f, -59.791f, Freeze);

        Elite(spawns, "E14-Elite", EliteDarkShaman, -93.190f, -9.323f);
        Boss(spawns, "Euryale", Euryale, MedusaEncounterEnemyRole.Euryale,
            -100.369f, -12.834f, Shackle,
            MedusaIslandRosterAnchor.FirstIslandTopLeft);
        Elite(spawns, "E9-Elite", EliteJungleWizardB, 11.319f, -21.677f, Bleed);
        Normal(spawns, "First-Normal-13", JungleDeer, 38.714f, -35.160f, Bleed);
        Normal(spawns, "First-Normal-14", JungleDeer, 39.490f, -34.483f, Bleed);
        Elite(spawns, "E10-Elite", EliteJungleWizardC6, 45.556f, -38.795f, Bleed);
        Elite(spawns, "E11-Elite", EliteJungleWizardC5, 68.458f, -54.386f, Bleed);
        Elite(spawns, "E12-Elite", EliteShamanC8, 83.782f, -81.217f, Bleed);
        Normal(spawns, "First-Normal-15", Shaman, 82.106f, -85.494f, Bleed);
        Normal(spawns, "First-Normal-16", Shaman, 82.896f, -86.486f, Bleed);
        Elite(spawns, "First-Elite-13", EliteShamanEight, 103.593f, -95.674f, Bleed);
        Normal(spawns, "First-Normal-17", PikemanA, 105.420f, -96.270f, Bleed);
        Normal(spawns, "First-Normal-18", PikemanA, 106.232f, -97.636f, Bleed);
        Normal(spawns, "First-Normal-19", Shaman, 112.164f, -115.923f, Bleed);
        Normal(spawns, "First-Normal-20", Shaman, 113.000f, -116.824f, Bleed);
        Elite(spawns, "First-Elite-14", EliteArcher, 115.851f, -119.990f, Bleed);
        Elite(spawns, "First-Elite-15", EliteCrazyAxemanC, 136.359f, -145.923f, Bleed);
        Normal(spawns, "First-Normal-21", PikemanB, 144.022f, -145.098f, Bleed);
        Normal(spawns, "First-Normal-22", PikemanB, 145.760f, -145.695f, Bleed);

        Elite(spawns, "First-Elite-16", EliteGuardianB, 176.969f, -76.746f);
        Normal(spawns, "First-Normal-23", GiantAxeman, 183.928f, -73.767f);
        Normal(spawns, "First-Normal-24", GiantAxeman, 184.840f, -73.325f);
        Elite(spawns, "First-Elite-17", EliteAstrologerB9, 138.228f, -32.877f);
        Normal(spawns, "First-Normal-25", GiantAxeman, 132.981f, -35.149f);
        Normal(spawns, "First-Normal-26", GiantAxeman, 131.210f, -35.371f);
        Normal(spawns, "First-Normal-27", Astrologer, 115.509f, -8.772f);
        Normal(spawns, "First-Normal-28", Astrologer, 114.071f, -8.450f);
        Elite(spawns, "First-Elite-18", EliteAstrologerA6, 114.273f, -6.290f);
        Elite(spawns, "First-Elite-19", EliteShamanSix, 104.048f, 18.511f);
        Elite(spawns, "First-Elite-20", EliteAstrologer, 77.920f, 30.308f);
        Normal(spawns, "First-Normal-29", GiantAxeman, 75.703f, 35.126f);
        Normal(spawns, "First-Normal-30", GiantAxeman, 75.842f, 36.487f);
        Elite(spawns, "E15-Elite", EliteGuardianA, 25.883f, 107.138f);
        Boss(spawns, "Chrysaor", Chrysaor, MedusaEncounterEnemyRole.Chrysaor,
            30.527f, 112.286f, Bleed,
            MedusaIslandRosterAnchor.FirstIslandTopRight);

        Elite(spawns, "E13-Elite", EliteGorgonDemon,
            -91.855f, 109.868f, island: MedusaIslandRosterIsland.Second);
        Elite(spawns, "E16-Elite", EliteDarkPriest,
            -100.915f, 113.748f, island: MedusaIslandRosterIsland.Second);
        AddSecondIslandAxemen(spawns);

        Normal(spawns, "Final-Pikeman-1", AxemanB, -154.795f, 174.998f,
            OutgoingPhysicalAmplifier, MedusaIslandRosterIsland.Final);
        Normal(spawns, "Final-Pikeman-2", AxemanB, -154.625f, 174.285f,
            OutgoingPhysicalAmplifier, MedusaIslandRosterIsland.Final);
        Elite(spawns, "Final-Axeman-1", EliteAxeman, -169.111f, 162.171f,
            OutgoingMagicalAmplifier, MedusaIslandRosterIsland.Final);
        Elite(spawns, "Final-Axeman-2", EliteAxeman, -169.450f, 161.821f,
            OutgoingMagicalAmplifier, MedusaIslandRosterIsland.Final);
        Elite(spawns, "Final-Cyclops-1", EliteCyclopsSwordsman,
            -144.739f, 182.128f, island: MedusaIslandRosterIsland.Final);
        Elite(spawns, "Final-Cyclops-2", EliteCyclopsSwordsman,
            -143.432f, 183.036f, island: MedusaIslandRosterIsland.Final);
        Elite(spawns, "Final-Wizard-1", EliteGorgonWizard,
            -173.376f, 162.941f, island: MedusaIslandRosterIsland.Final);
        Elite(spawns, "Final-Wizard-2", EliteGorgonWizard,
            -178.439f, 161.514f, island: MedusaIslandRosterIsland.Final);
        Boss(spawns, "Stheno", Stheno, MedusaEncounterEnemyRole.Stheno,
            -172.951f, 175.696f, island: MedusaIslandRosterIsland.Final);
        Boss(spawns, "Medusa", Medusa, MedusaEncounterEnemyRole.Medusa,
            -169.270f, 190.744f, island: MedusaIslandRosterIsland.Final);

        return spawns.MoveToImmutable();
    }

    private static void AddSecondIslandAxemen(
        ImmutableArray<MedusaIslandCapturedSpawn>.Builder spawns)
    {
        ReadOnlySpan<(float X, float Z)> points =
        [
            (-101.807f,102.240f),(-91.832f,109.447f),(-91.475f,97.109f),
            (-88.569f,122.245f),(-99.761f,115.204f),(-97.348f,108.754f),
            (-78.962f,106.787f),(-90.484f,106.886f),(-79.984f,119.680f),
            (-94.462f,110.604f),(-85.268f,104.829f),(-84.907f,97.749f),
            (-106.950f,104.035f),(-79.573f,109.363f),(-101.251f,101.015f),
            (-79.650f,101.644f),(-81.696f,121.376f),(-96.761f,102.377f),
            (-89.384f,105.433f),(-104.071f,114.226f),(-87.198f,99.188f),
            (-101.558f,104.025f),(-101.922f,104.980f),(-96.446f,117.322f),
            (-90.463f,98.796f),(-97.427f,113.164f),(-98.103f,106.728f),
            (-84.948f,122.178f),(-78.921f,115.066f),(-105.598f,102.761f),
            (-100.527f,100.584f),(-105.347f,112.441f),(-108.200f,103.440f),
            (-86.506f,126.204f),(-96.147f,123.146f),(-108.450f,99.741f),
            (-101.692f,120.739f),(-102.891f,120.295f),(-86.467f,127.228f),
            (-107.186f,114.300f),(-105.745f,118.681f),(-85.292f,128.506f),
            (-112.192f,101.451f),(-99.521f,126.093f),(-101.677f,124.614f),
            (-103.438f,124.660f),(-107.575f,121.237f),(-106.107f,123.655f),
            (-89.201f,129.982f),(-114.244f,108.041f),(-97.342f,129.064f),
            (-116.012f,108.482f),(-102.861f,128.621f),(-103.581f,130.336f),
            (-102.280f,132.081f),(-97.838f,134.321f),(-97.716f,134.151f),
            (-97.613f,134.473f),(-78.117f,130.529f),(-96.343f,136.027f),
            (-114.983f,123.951f),(-87.935f,135.774f),(-110.893f,129.896f),
            (-107.736f,132.369f),(-115.243f,123.685f),(-94.439f,136.955f),
            (-115.504f,126.385f),(-116.722f,130.486f),(-115.974f,133.209f),
            (-115.848f,136.126f)
        ];
        for (var index = 0; index < points.Length; index++)
        {
            Normal(
                spawns,
                $"Second-Axeman-{index + 1:00}",
                AxemanA,
                points[index].X,
                points[index].Z,
                island: MedusaIslandRosterIsland.Second);
        }
    }

    private static void Normal(
        ImmutableArray<MedusaIslandCapturedSpawn>.Builder spawns,
        string id,
        string template,
        float x,
        float z,
        MedusaIslandRosterMechanic? mechanic = null,
        MedusaIslandRosterIsland island = MedusaIslandRosterIsland.First) =>
        spawns.Add(new(
            id, island, LaneFor(mechanic), MedusaIslandRosterSpawnKind.Ordinary,
            MedusaEncounterEnemyRole.Ordinary, MedusaMonsterRank.Normal,
            template, mechanic, x, z, MedusaIslandRosterAnchor.None));

    private static void Elite(
        ImmutableArray<MedusaIslandCapturedSpawn>.Builder spawns,
        string id,
        string template,
        float x,
        float z,
        MedusaIslandRosterMechanic? mechanic = null,
        MedusaIslandRosterIsland island = MedusaIslandRosterIsland.First) =>
        spawns.Add(new(
            id, island, LaneFor(mechanic), MedusaIslandRosterSpawnKind.Elite,
            MedusaEncounterEnemyRole.Elite, MedusaMonsterRank.Elite,
            template, mechanic, x, z, MedusaIslandRosterAnchor.None));

    private static void Boss(
        ImmutableArray<MedusaIslandCapturedSpawn>.Builder spawns,
        string id,
        string template,
        MedusaEncounterEnemyRole role,
        float x,
        float z,
        MedusaIslandRosterMechanic? mechanic = null,
        MedusaIslandRosterAnchor anchor = MedusaIslandRosterAnchor.None,
        MedusaIslandRosterIsland island = MedusaIslandRosterIsland.First) =>
        spawns.Add(new(
            id, island, LaneFor(mechanic), MedusaIslandRosterSpawnKind.Boss,
            role, MedusaMonsterRank.Boss, template, mechanic, x, z, anchor));

    private static MedusaIslandRosterLane LaneFor(
        MedusaIslandRosterMechanic? mechanic) =>
        mechanic switch
        {
            Stun => MedusaIslandRosterLane.Stun,
            Freeze => MedusaIslandRosterLane.Freeze,
            Bleed => MedusaIslandRosterLane.Bleed,
            _ => MedusaIslandRosterLane.None
        };
}

internal sealed record MedusaIslandCapturedSpawn(
    string SpawnId,
    MedusaIslandRosterIsland Island,
    MedusaIslandRosterLane Lane,
    MedusaIslandRosterSpawnKind Kind,
    MedusaEncounterEnemyRole Role,
    MedusaMonsterRank Rank,
    string TemplateAlias,
    MedusaIslandRosterMechanic? Mechanic,
    float X,
    float Z,
    MedusaIslandRosterAnchor Anchor);
