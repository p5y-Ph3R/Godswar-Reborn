using System.Diagnostics;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class GeneratedWorldContentReaderLoader
{
    private const string JsonWorldContentSource =
        "json-generated-v1";

    public static Task<IWorldContentReader> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var mapIds = MapTemplateSeeds.Maps
                .Select(static map => map.MapId)
                .Distinct()
                .Order()
                .ToArray();
            var definitions = mapIds
                .SelectMany(mapId =>
                {
                    var references =
                        NpcSpawnDefinitionFactory.FromGeneratedSeeds(mapId);
                    return NpcSpawnDefinitionFactory.Create(
                        mapId,
                        [],
                        [],
                        references);
                })
                .ToArray();
            var actualNpcKeys = definitions
                .Select(static definition => definition.NpcKey)
                .ToHashSet(StringComparer.Ordinal);
            var npcTexts = NpcTemplateSeeds.Texts
                .Where(seed => actualNpcKeys.Contains(seed.NpcKey))
                .Select(static seed => new NpcTextDefinition(
                    seed.NpcKey,
                    seed.SceneKey,
                    seed.DisplayName,
                    seed.Description))
                .ToArray();
            IWorldContentReader reader = PinnedWorldContentReader.Create(
                JsonWorldContentSource,
                mapIds,
                definitions,
                [],
                [],
                npcTexts: npcTexts,
                npcDialogueRoutes:
                    NpcDialogueBaselineV1.CreateRoutes());
            stopwatch.Stop();
            WorldContentMetrics.RecordLoad(
                JsonWorldContentSource,
                "success",
                stopwatch.Elapsed);
            return Task.FromResult(reader);
        }
        catch (WorldContentUnavailableException ex)
        {
            stopwatch.Stop();
            WorldContentMetrics.RecordRejection(ex.Family, ex.Reason);
            WorldContentMetrics.RecordLoad(
                JsonWorldContentSource,
                "rejected",
                stopwatch.Elapsed);
            throw;
        }
        catch
        {
            stopwatch.Stop();
            WorldContentMetrics.RecordLoad(
                JsonWorldContentSource,
                "error",
                stopwatch.Elapsed);
            throw;
        }
    }
}
