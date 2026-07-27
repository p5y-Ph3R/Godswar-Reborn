using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class NpcMultiSegmentSceneChecks
{
    public static Task RunAsync()
    {
        AssertAppearance(
            "Parnitha_1_001_Male14",
            "Parnitha_1_001",
            "Parnitha_1");
        AssertAppearance(
            "Nemea_2_001_Male14",
            "Nemea_2_001",
            "Nemea_2");
        AssertAppearance(
            "Agate2_001_Aga1",
            "Agate2_001",
            "Agate2");

        foreach (var mapId in new short[] { 3, 5, 12, 14 })
        {
            Check.True(
                NpcSpawnDefinitionFactory
                    .FromGeneratedSeeds(mapId)
                    .Count > 0,
                $"multi-segment map {mapId} resolves quest NPC appearances");
        }

        CheckGeneratedReferenceCoverage();
        return Task.CompletedTask;
    }

    private static void CheckGeneratedReferenceCoverage()
    {
        var appearanceKeys = NpcTemplateSeeds.Appearances
            .Select(static appearance => appearance.NpcKey)
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        var resolved = NpcTemplateSeeds.SpawnReferences
            .Where(reference => appearanceKeys.Contains(reference.NpcKey))
            .ToArray();
        var unresolved = NpcTemplateSeeds.SpawnReferences
            .Where(reference => !appearanceKeys.Contains(reference.NpcKey))
            .ToArray();

        Check.Equal(2_084, resolved.Length, "resolved quest NPC reference rows");
        Check.Equal(
            221,
            resolved.Select(static reference => reference.NpcKey)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            "resolved quest NPC keys");
        Check.Equal(20, unresolved.Length, "unresolved quest NPC reference rows");
        Check.True(
            unresolved
                .GroupBy(static reference => reference.NpcKey)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => (group.Key, group.Count()))
                .SequenceEqual(
                [
                    ("Marathon_All_006", 10),
                    ("Peloponnese_All_006", 10)
                ]),
            "unresolved quest NPC references remain explicit");
    }

    private static void AssertAppearance(
        string templateKey,
        string expectedNpcKey,
        string expectedSceneKey)
    {
        var appearance = NpcTemplateSeeds.Appearances.Single(
            candidate => string.Equals(
                candidate.TemplateKey,
                templateKey,
                StringComparison.Ordinal));
        Check.Equal(
            expectedNpcKey,
            appearance.NpcKey,
            $"{templateKey} NPC key");
        Check.Equal(
            expectedSceneKey,
            appearance.SceneKey,
            $"{templateKey} scene key");
    }
}
