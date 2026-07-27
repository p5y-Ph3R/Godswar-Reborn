using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class BackhaulSkillCatalogChecks
{
    public static Task RunAsync()
    {
        Check.Equal(
            2,
            BackhaulSkillCatalog.All.Count,
            "backhaul catalog contains only the two native Sparta skills");

        CheckDefinition(
            BackhaulSkillCatalog.CitySkillId,
            expectedName: "Sparta City",
            expectedMapId: GameDefaults.SpartaCapitalMap,
            expectedX: 165f,
            expectedZ: -97f,
            expectedCooldown: TimeSpan.FromSeconds(300));
        CheckDefinition(
            BackhaulSkillCatalog.SuburbSkillId,
            expectedName: "Sparta Suburb",
            expectedMapId: 4,
            expectedX: 102f,
            expectedZ: -217f,
            expectedCooldown: TimeSpan.FromSeconds(600));

        Check.True(
            !BackhaulSkillCatalog.TryGet(3065, out _),
            "Peloponnese skill 3065 is not repurposed as Sparta backhaul");

        var suburbTemplate = SkillTalentSeeds.Skills.Single(
            static skill =>
                skill.SkillId ==
                (int)BackhaulSkillCatalog.SuburbSkillId);
        Check.True(
            suburbTemplate.ClassIds.SequenceEqual(
                new short[] { 0, 1, 2, 3 }) &&
            suburbTemplate.Mp == 50 &&
            suburbTemplate.SkillLevel is null,
            "unbooked native suburb skill has a reproducible all-class template");
        return Task.CompletedTask;
    }

    private static void CheckDefinition(
        uint skillId,
        string expectedName,
        byte expectedMapId,
        float expectedX,
        float expectedZ,
        TimeSpan expectedCooldown)
    {
        Check.True(
            BackhaulSkillCatalog.TryGet(skillId, out var definition),
            $"native backhaul skill {skillId} resolves");
        Check.Equal(
            skillId,
            definition.SkillId,
            $"backhaul skill {skillId} identity");
        Check.Equal(
            expectedName,
            definition.DisplayName,
            $"backhaul skill {skillId} display name");
        Check.Equal(
            GameDefaults.SpartaCamp,
            definition.RequiredCamp,
            $"backhaul skill {skillId} camp");
        Check.Equal(
            expectedMapId,
            definition.TargetMapId,
            $"backhaul skill {skillId} destination map");
        Check.Equal(
            expectedX,
            definition.TargetX,
            $"backhaul skill {skillId} destination X");
        Check.Equal(
            expectedZ,
            definition.TargetZ,
            $"backhaul skill {skillId} destination Z");
        Check.Equal(
            50,
            definition.ManaCost,
            $"backhaul skill {skillId} MP cost");
        Check.Equal(
            TimeSpan.FromSeconds(6),
            definition.CastTime,
            $"backhaul skill {skillId} intonation");
        Check.Equal(
            expectedCooldown,
            definition.Cooldown,
            $"backhaul skill {skillId} cooldown");
    }
}
