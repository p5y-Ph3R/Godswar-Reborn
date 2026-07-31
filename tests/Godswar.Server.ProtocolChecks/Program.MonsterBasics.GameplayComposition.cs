using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckJsonFocusedWorldBossPersistenceAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-focused-world-boss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            var definition = WorldBossCatalog.Default.Definitions[0];
            var killedAt = new DateTimeOffset(
                2026,
                7,
                20,
                2,
                0,
                0,
                TimeSpan.Zero);
            var expectedRespawnAt =
                killedAt + WorldBossCatalog.Default.RespawnInterval;

            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var providers = ServerGameplayPersistenceComposition.Create(
                    null,
                    store);
                var activation = new WorldBossAreaActivation(
                    definition.MapId,
                    definition.TemplateKey,
                    GameDefaults.SpartaCamp,
                    killedAt,
                    "focused-death-1");
                var committed = await providers.WorldBossAreaControl
                    .ActivateAsync(activation);
                Check.Equal(
                    (int)WorldBossAreaActivationDisposition.Committed,
                    (int)committed.Disposition,
                    "focused JSON world-boss activation commits once");
                Check.True(
                    committed.Control?.ExpiresAtUtc == expectedRespawnAt,
                    "focused JSON world-boss activation returns its authoritative expiry");

                var duplicate = await providers.WorldBossAreaControl
                    .ActivateAsync(activation);
                Check.Equal(
                    (int)WorldBossAreaActivationDisposition.Duplicate,
                    (int)duplicate.Disposition,
                    "focused JSON world-boss activation deduplicates one death token");

                var otherDefinition = WorldBossCatalog.Default.Definitions
                    .First(candidate => candidate.MapId != definition.MapId);
                var crossMapReplay = await providers.WorldBossAreaControl
                    .ActivateAsync(
                        new WorldBossAreaActivation(
                            otherDefinition.MapId,
                            otherDefinition.TemplateKey,
                            GameDefaults.AthensCamp,
                            killedAt,
                            activation.DeathToken));
                Check.Equal(
                    (int)WorldBossAreaActivationDisposition.Invalid,
                    (int)crossMapReplay.Disposition,
                    "focused JSON world-boss activation rejects a cross-map token replay");

                var stale = await providers.WorldBossAreaControl.ActivateAsync(
                    activation with
                    {
                        KilledAtUtc = killedAt.AddMinutes(-1),
                        DeathToken = "focused-stale-death"
                    });
                Check.Equal(
                    (int)WorldBossAreaActivationDisposition.Stale,
                    (int)stale.Disposition,
                    "focused JSON world-boss activation rejects older deaths");

                var active = await providers.WorldBossRespawns.ReadActiveAsync(
                    new WorldBossRespawnReadRequest(
                        definition.MapId,
                        killedAt.AddMinutes(1))) ??
                    throw new InvalidOperationException(
                        "Focused JSON world-boss respawn was not readable.");
                Check.Equal(
                    expectedRespawnAt,
                    active.RespawnAtUtc,
                    "duplicate and stale activations do not move the respawn deadline");
            }

            await using var restartedStore = new JsonGameStore(dataPath);
            var restartedProviders =
                ServerGameplayPersistenceComposition.Create(
                    null,
                    restartedStore);
            var restored = await restartedProviders.WorldBossRespawns
                .ReadActiveAsync(
                    new WorldBossRespawnReadRequest(
                        definition.MapId,
                        killedAt.AddHours(1))) ??
                throw new InvalidOperationException(
                    "Focused JSON world-boss respawn did not survive restart.");
            Check.Equal(
                definition.TemplateKey,
                restored.BossTemplateKey,
                "focused JSON world-boss respawn preserves its template across restart");
            Check.Equal(
                expectedRespawnAt,
                restored.RespawnAtUtc,
                "focused JSON world-boss respawn preserves its deadline across restart");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
