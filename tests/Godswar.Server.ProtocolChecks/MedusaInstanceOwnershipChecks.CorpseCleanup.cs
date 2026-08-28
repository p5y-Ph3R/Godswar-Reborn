using Godswar.Server.Game;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    internal static Task RunMedusaCorpseCleanupChecksAsync()
    {
        var fixture = CreateAttachmentFixture();
        var attachment = AttachAuthored(fixture);
        Check.True(
            attachment.IsAttached,
            "Medusa corpse-cleanup fixture attaches its authored runtime");

        var monster = fixture.Map.SnapshotMonsters()
            .First(static candidate =>
                candidate.IsAlive && candidate.IsSpawned);
        var killedAt = StartedAt.AddSeconds(1);
        var lethal = CommitTypedDamage(
            fixture.Map,
            monster,
            attackerCharacterId: 101,
            CombatDamageChannel.Physical,
            uint.MaxValue,
            killedAt);
        Check.True(
            lethal.Applied && lethal.DamageResult is
            {
                Killed: true,
                Monster.RespawnAt: null
            },
            "Medusa lethal damage commits without scheduling a respawn");

        var deathTick = fixture.Map.AdvanceMonsters(killedAt);
        Check.True(
            deathTick.Updates.Any(static update =>
                update.Kind == MonsterRuntimeUpdateKind.Died) &&
            deathTick.Updates.All(static update =>
                update.Kind != MonsterRuntimeUpdateKind.Despawned) &&
            fixture.Map.TryGetMonsterSnapshot(
                monster.ObjectId,
                out var visibleCorpse) &&
            visibleCorpse.IsSpawned,
            "lethal damage remains visible for its damage-delivery tick");

        var removalDelay =
            MedusaMonsterPresentationPolicy.CorpseRemovalDelay;
        Check.True(
            removalDelay > MonsterMapRuntime.TickInterval &&
            removalDelay == TimeSpan.FromMilliseconds(4_200),
            "Medusa matches the captured no-loot corpse presentation");

        var preRemovalTick = fixture.Map.AdvanceMonsters(
            killedAt + removalDelay -
            MonsterMapRuntime.TickInterval);
        Check.True(
            preRemovalTick.Updates.All(static update =>
                update.Kind != MonsterRuntimeUpdateKind.Despawned),
            "Medusa keeps the corpse through the damage-rendering window");

        var removalTick = fixture.Map.AdvanceMonsters(
            killedAt + removalDelay);
        Check.True(
            removalTick.Updates.Any(update =>
                update.Kind == MonsterRuntimeUpdateKind.Despawned &&
                update.Monster.ObjectId == monster.ObjectId),
            "the captured corpse deadline removes the object from the world");

        _ = fixture.Map.AdvanceMonsters(StartedAt.AddDays(365));
        Check.True(
            fixture.Map.TryGetMonsterSnapshot(
                monster.ObjectId,
                out var retired) &&
            !retired.IsAlive &&
            !retired.IsSpawned &&
            retired.RespawnAt is null &&
            retired.SpawnGeneration == 1,
            "corpse cleanup preserves Medusa never-respawn state");

        return Task.CompletedTask;
    }
}
