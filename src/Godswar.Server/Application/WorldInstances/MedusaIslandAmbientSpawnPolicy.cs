namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct MedusaIslandAmbientSpawn(
    string SpawnId,
    short MapId,
    string SceneKey,
    string TemplateKey,
    string DisplayName,
    uint Tier,
    uint MaximumHealth,
    float X,
    float Z);

internal static class MedusaIslandAmbientSpawnPolicy
{
    public const string BabyRockElfSpawnId = "Baby-Rock-Elf";
    public const string SecondBabyRockElfSpawnId = "Baby-Rock-Elf-2";
    public const string BabyRockElfTemplateKey = "A_normal_male_001";
    public const float BabyRockElfX = -71.267f;
    public const float BabyRockElfZ = 3.919f;
    public const float SecondBabyRockElfX = 3.885f;
    public const float SecondBabyRockElfZ = 85.881f;

    private static readonly MedusaIslandAmbientSpawn[] CapturedSpawns =
    [
        Create(BabyRockElfSpawnId, BabyRockElfX, BabyRockElfZ),
        Create(
            SecondBabyRockElfSpawnId,
            SecondBabyRockElfX,
            SecondBabyRockElfZ)
    ];

    public static int CountFor(MedusaEncounterDifficulty difficulty) =>
        difficulty is MedusaEncounterDifficulty.Enhanced or
            MedusaEncounterDifficulty.Mythic
            ? CapturedSpawns.Length
            : 0;

    public static IReadOnlyList<MedusaIslandAmbientSpawn> SpawnsFor(
        MedusaEncounterDifficulty difficulty) =>
        CountFor(difficulty) == 0 ? [] : CapturedSpawns;

    public static bool TryResolve(
        MedusaEncounterDifficulty difficulty,
        out MedusaIslandAmbientSpawn spawn)
    {
        if (CountFor(difficulty) == 0)
        {
            spawn = default;
            return false;
        }

        spawn = CapturedSpawns[0];
        return true;
    }

    private static MedusaIslandAmbientSpawn Create(
        string spawnId,
        float x,
        float z) =>
        new(
            spawnId,
            MapId: 200,
            SceneKey: "Medusa_Island",
            BabyRockElfTemplateKey,
            DisplayName: "[Pet] Baby Rock Elf",
            Tier: 1,
            MaximumHealth: 10,
            x,
            z);
}
