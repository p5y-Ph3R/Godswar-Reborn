namespace Godswar.Server.State;

/// <summary>
/// Authoritative server actor placement recovered from the original city
/// NPC.ini files. Their X/Y map plane maps to protocol X/Z, while the source Z
/// value is actor facing at opcode-10020 offset 40. Source object IDs are
/// provenance only; established emulator interaction IDs remain stable.
/// </summary>
internal readonly record struct NpcActorPlacement(
    short MapId,
    string NpcKey,
    string TemplateKey,
    uint SourceObjectId,
    float X,
    float Z,
    float Facing);

internal static partial class NpcActorPlacementCatalog
{
    private static readonly IReadOnlyList<NpcActorPlacement> Athens =
    [
        new(1, "Athens_011", "Athens_011_FemSage1", 5150u, -116f, 143f, 1.7f),
        new(1, "Athens_078", "Athens_078_FemMale3", 5217u, 143f, 23f, 3f),
        new(1, "Athens_016", "Athens_016_SpartanGen1", 5155u, -92f, 137f, 2.3f),
        new(1, "Athens_142", "Athens_142_Hallo", 6101u, -228f, -28f, 1.7f),
        new(1, "Athens_143", "Athens_143_Hallo", 6102u, 97f, -163f, 1.7f),
        new(1, "Athens_008", "Athens_008_MaleVillager1", 5147u, -76f, 105f, 3f),
        new(1, "Athens_009", "Athens_009_AthenianCivilian1", 5148u, -37f, 78f, 2.3f),
        new(1, "Athens_024", "Athens_024_FemMale5", 5163u, -1f, 29f, 3f),
        new(1, "Athens_034", "Athens_034_Male12", 5173u, 10f, 29f, 3f),
        new(1, "Athens_017", "Athens_017_Belle1", 5156u, 20f, -22f, 3f),
        new(1, "Athens_081", "Athens_081_FemMale3", 5220u, -78f, -25f, 2.3f),
        new(1, "Athens_003", "Athens_003_AthenianWarrior1", 5142u, -81f, -88f, 1.7f),
        new(1, "Athens_082", "Athens_082_FemMale3", 5221u, -102f, -115f, 1.7f),
        new(1, "Athens_004", "Athens_004_MaleVillager1", 5143u, -86f, -109f, 1.7f),
        new(1, "Athens_005", "Athens_005_FemVillager2", 5144u, -73f, -130f, 3f),
        new(1, "Athens_080", "Athens_080_FemMale3", 5219u, -73f, -184f, 1.7f),
        new(1, "Athens_010", "Athens_010_FemVillager1", 5149u, -48f, -151f, 1.7f),
        new(1, "Athens_018", "Athens_018_Male21", 5157u, 9f, -169f, 1.7f),
        new(1, "Athens_013", "Athens_013_MaleVillager1", 5152u, 52f, 51f, 3f),
        new(1, "Athens_007", "Athens_007_MaleVillager1", 5146u, 96f, 28f, 1.7f),
        new(1, "Athens_019", "Athens_019_MaleHero3", 5158u, 93f, 23f, 1.7f),
        new(1, "Athens_014", "Athens_014_MaleVillager1", 5153u, 103f, 20f, 2.3f),
        new(1, "Athens_048", "Athens_048_MaleVillager2", 5187u, 81f, -9f, 3f),
        new(1, "Athens_006", "Athens_006_MaleSage2", 5145u, 100f, -12f, 3f),
        new(1, "Athens_022", "Athens_022_FemMale4", 5161u, 113f, -10f, 3f),
        new(1, "Athens_099", "Athens_099_Male28", 5238u, 121f, -59f, 1.7f),
        new(1, "Athens_100", "Athens_100_FemMale5", 5239u, 121f, -43f, 1.7f),
        new(1, "Athens_108", "Athens_108_FemMale27", 5247u, 121f, -63f, 1.7f),
        new(1, "Athens_060", "Athens_060_FemMale17", 5199u, 137f, -39f, 3f),
        new(1, "Athens_072", "Athens_072_FemMale20", 5211u, 143f, -39f, 3f),
        new(1, "Athens_114", "Athens_114_Male33", 5253u, 157f, -59f, 3f),
        new(1, "Athens_124", "Athens_124_Male16", 5263u, 147f, -36f, 1.7f),
        new(1, "Athens_077", "Athens_077_FemMale25", 5216u, 134f, -35f, 3f),
        new(1, "Athens_113", "Athens_113_Male32", 5252u, 154f, -59f, 3f),
        new(1, "Athens_074", "Athens_074_Belle2", 5213u, 76f, -79f, 3f),
        new(1, "Athens_096", "Athens_096_Male27", 5235u, 123f, -70f, 1.7f),
        new(1, "Athens_098", "Athens_098_HandsomeGuy1", 5237u, 121f, -95f, 1.7f),
        new(1, "Athens_097", "Athens_097_MaleMerchant2", 5236u, 113f, -78f, 1.7f),
        new(1, "Athens_103", "Athens_103_MaleVillager1", 5242u, 113f, -75f, 1.7f),
        new(1, "Athens_105", "Athens_105_MaleSage1", 5244u, 121f, -90f, 1.7f),
        new(1, "Athens_107", "Athens_107_FemMale26", 5246u, 123f, -66f, 1.7f),
        new(1, "Athens_101", "Athens_101_AthenianCivilian1", 5240u, 121f, -102f, 1.7f),
        new(1, "Athens_104", "Athens_104_SpartanCivilian1", 5243u, 121f, -106f, 1.7f),
        new(1, "Athens_049", "Athens_049_Male16", 5188u, 53f, -105f, 1.7f),
        new(1, "Athens_050", "Athens_050_FemMale13", 5189u, 53f, -108f, 1.7f),
        new(1, "Athens_051", "Athens_051_Male17", 5190u, 53f, -101f, 1.7f),
        new(1, "Athens_043", "Athens_043_MaleSage1", 5182u, 42f, -128f, 1.7f),
        new(1, "Athens_071", "Athens_071_FemMale19", 5210u, 56f, -131f, 3f),
        new(1, "Athens_075", "Athens_075_FemMale21", 5214u, 62f, -131f, 3f),
        new(1, "Athens_091", "Athens_091_Losebook1", 5230u, 57f, -152f, 3f),
        new(1, "Athens_083", "Athens_083_HealthR1", 5222u, 74f, -131f, 3f),
        new(1, "Athens_088", "Athens_088_FemMale30", 5227u, 68f, -131f, 3f),
        new(1, "Athens_123", "Athens_123_Male15", 5262u, 92f, -159f, 3f),
        new(1, "Athens_134", "Athens_134_Male25", 5273u, 91f, -155f, 3f),
        new(1, "Athens_028", "Athens_028_FemMale7", 5167u, 121f, -137f, 3f),
        new(1, "Athens_036", "Athens_036_Male13", 5175u, 97f, -151f, 1.7f),
        new(1, "Athens_021", "Athens_021_FemMale3", 5160u, 97f, -155f, 1.7f),
        new(1, "Athens_025", "Athens_025_Male6", 5164u, 97f, -159f, 1.7f),
        new(1, "Athens_027", "Athens_027_FemMale6", 5166u, 117f, -137f, 3f),
        new(1, "Athens_029", "Athens_029_Male8", 5168u, 125f, -137f, 3f),
        new(1, "Athens_089", "Athens_089_FemMale25", 5228u, 97f, -148f, 1.7f),
        new(1, "Athens_131", "Athens_131_MaleSage2", 5270u, 103f, -143f, 2.3f),
        new(1, "Athens_133", "Athens_133_FemVillager3", 5272u, 102f, -148f, 6.1f),
        new(1, "Athens_026", "Athens_026_Male7", 5165u, 113f, -137f, 3f),
        new(1, "Athens_058", "Athens_058_FemCivilian1", 5197u, 61f, -171f, 1.7f),
        new(1, "Athens_059", "Athens_059_Belle2", 5198u, 73f, -178f, 1.7f),
        new(1, "Athens_135", "Athens_135_FemVillager3", 5274u, 91f, -182f, 1.7f),
        new(1, "Athens_136", "Athens_136_FemVillager3", 5275u, 91f, -186f, 1.7f),
        new(1, "Athens_086", "Athens_086_Male35", 5225u, 126f, -169f, 4.7f),
        new(1, "Athens_120", "Athens_120_FemVillager3", 5259u, 97f, -178f, 1.7f),
        new(1, "Athens_122", "Athens_122_FemVillager3", 5261u, 97f, -174f, 1.7f),
        new(1, "Athens_085", "Athens_085_Male34", 5224u, 126f, -162f, 4.7f),
        new(1, "Athens_115", "Athens_115_Male9", 5254u, 126f, -174f, 3f),
        new(1, "Athens_121", "Athens_121_FemVillager3", 5260u, 97f, -183f, 1.7f),
        new(1, "Athens_137", "Athens_137_FemVillager3", 5276u, 97f, -187f, 1.7f),
        new(1, "Athens_020", "Athens_020_MaleSage1", 5159u, 125f, -200f, 1.7f),
        new(1, "Athens_045", "Athens_045_MaleVillager1", 5184u, 125f, -196f, 1.7f),
        new(1, "Athens_040", "Athens_040_FemMale11", 5179u, 135f, -157f, 6.1f),
        new(1, "Athens_052", "Athens_052_Male18", 5191u, 155f, -137f, 3f),
        new(1, "Athens_068", "Athens_068_Male21", 5207u, 147f, -137f, 3f),
        new(1, "Athens_084", "Athens_084_FemMale29", 5223u, 138f, -157f, 6.1f),
        new(1, "Athens_039", "Athens_039_FemMale10", 5178u, 129f, -157f, 6.1f),
        new(1, "Athens_055", "Athens_055_FemMale15", 5194u, 142f, -158f, 1.7f),
        new(1, "Athens_069", "Athens_069_Male25", 5208u, 151f, -137f, 3f),
        new(1, "Athens_087", "Athens_087_FemMale9", 5226u, 144f, -137f, 3f),
        new(1, "Athens_093", "Athens_093_FemMale12", 5232u, 132f, -157f, 6.1f),
        new(1, "Athens_023", "Athens_023_Male5", 5162u, 177f, -132f, 1.7f),
        new(1, "Athens_079", "Athens_079_FemMale3", 5218u, 177f, -138f, 1.7f),
        new(1, "Athens_138", "Athens_138_Male32", 5277u, 171f, -142f, 3f),
        new(1, "Athens_015", "Athens_015_Belle1", 5154u, 18f, -127f, 2.3f),
        new(1, "Athens_095", "Athens_095_Male26", 5234u, 151f, -93f, 3.9f),
        new(1, "Athens_102", "Athens_102_Male29", 5241u, 157f, -95f, 3f),
        new(1, "Athens_109", "Athens_109_FemMale22", 5248u, 149f, -86f, 4.7f),
        new(1, "Athens_094", "Athens_094_Male30", 5233u, 161f, -93f, 2.3f),
        new(1, "Athens_106", "Athens_106_MaleHero2", 5245u, 162f, -83f, 0.7f),
        new(1, "Athens_056", "Athens_056_Male20", 5195u, 174f, -39f, 3f),
        new(1, "Athens_062", "Athens_062_Belle3", 5201u, 173f, -57f, 3f),
        new(1, "Athens_090", "Athens_090_Losebook1", 5229u, 179f, -57f, 3f),
        new(1, "Athens_033", "Athens_033_Male35", 5172u, 178f, -32f, 2.3f),
        new(1, "Athens_041", "Athens_041_FemMale12", 5180u, 168f, -39f, 3f),
        new(1, "Athens_057", "Athens_057_FemMale16", 5196u, 164f, -34f, 3f),
        new(1, "Athens_061", "Athens_061_Belle3", 5200u, 176f, -57f, 3f),
        new(1, "Athens_073", "Athens_073_Male23", 5212u, 177f, -36f, 2.3f),
        new(1, "Athens_038", "Athens_038_Male15", 5177u, 53f, -95f, 1.7f),
        new(1, "Athens_044", "Athens_044_Male34", 5183u, 141f, -174f, 2.3f),
        new(1, "Athens_070", "Athens_070_Male22", 5209u, 142f, -165f, 1.7f),
        new(1, "Athens_116", "Athens_116_nvpu", 5255u, 130f, -174f, 3f),
        new(1, "Athens_118", "Athens_118_FemMale14", 5257u, 136f, -174f, 3f),
        new(1, "Athens_132", "Athens_132_MaleSage2", 5271u, 142f, -161f, 1.7f),
        new(1, "Athens_125", "Athens_125_Male35", 5264u, 142f, -169f, 1.7f),
        new(1, "Athens_139", "Athens_139_guangao", 5278u, 133f, -174f, 3f)
    ];

    private static readonly Lazy<IReadOnlyList<NpcActorPlacement>> Combined =
        new(() => Athens.Concat(Sparta!).ToArray());

    private static readonly Lazy<IReadOnlyDictionary<(short MapId, string NpcKey), NpcActorPlacement>> ByNpc =
        new(() => All.ToDictionary(
            static placement => (placement.MapId, placement.NpcKey),
            static placement => placement));

    public static IReadOnlyList<NpcActorPlacement> All => Combined.Value;

    public static bool TryGet(short mapId, string npcKey, out NpcActorPlacement placement) =>
        ByNpc.Value.TryGetValue((mapId, npcKey), out placement);
}
