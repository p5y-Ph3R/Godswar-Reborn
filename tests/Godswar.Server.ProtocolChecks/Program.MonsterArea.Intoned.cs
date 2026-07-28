using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task
        CheckIntonedMonsterAreaCompletionCohortAsync()
    {
        const uint skillId = 2_000;
        await using var hitViewer =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var observer =
            await RuntimePolicySessionSocket.CreateAsync();

        var hitCharacter = CreateCharacter();
        hitCharacter.CurrentMap = 0;
        hitCharacter.PositionX = 100f;
        hitCharacter.PositionZ = 100f;
        var observerCharacter = CreateCharacter();
        observerCharacter.Id += 1;
        observerCharacter.AccountId += 1;
        observerCharacter.Name = "IntonedAreaObserver";
        observerCharacter.CurrentMap = hitCharacter.CurrentMap;
        observerCharacter.PositionX = 500f;
        observerCharacter.PositionZ = 500f;
        var monster = CreateCapturedMonster(
            10_045,
            hitCharacter.PositionX + 1f,
            hitCharacter.PositionZ + 1f,
            "A_normal_stub_001");
        var registry = new GameSessionRegistry();
        registry.InitializeMapMonsters(
            hitCharacter.CurrentMap,
            [monster],
            DateTimeOffset.UtcNow);
        registry.JoinMap(
            hitViewer.Session,
            hitCharacter.AccountId,
            hitCharacter,
            WorldObjectIds.ForPlayer(hitCharacter.Id));
        registry.JoinMap(
            observer.Session,
            observerCharacter.AccountId,
            observerCharacter,
            WorldObjectIds.ForPlayer(observerCharacter.Id));

        await using (var hitTransition =
                     await registry.BeginMonsterVisibilityTransitionAsync(
                         hitViewer.Session,
                         hitCharacter.CurrentMap,
                         hitCharacter.PositionX,
                         hitCharacter.PositionZ,
                         CancellationToken.None)
                     ?? throw new InvalidOperationException(
                         "intoned AoE hit-viewer transition was unavailable"))
        {
            Check.True(
                hitTransition.Delta.Entering
                    .Select(entry => entry.ObjectId)
                    .SequenceEqual([monster.ObjectId]),
                "intoned AoE hit viewer sees the damaged monster");
            hitTransition.Commit();
        }

        await using (var observerTransition =
                     await registry.BeginMonsterVisibilityTransitionAsync(
                         observer.Session,
                         observerCharacter.CurrentMap,
                         observerCharacter.PositionX,
                         observerCharacter.PositionZ,
                         CancellationToken.None)
                     ?? throw new InvalidOperationException(
                         "intoned AoE observer transition was unavailable"))
        {
            Check.Equal(
                0,
                observerTransition.Delta.Entering.Count,
                "intoned AoE observer has no direct monster-health mutation");
            observerTransition.Commit();
        }

        var visual = PacketBuilder.MonsterLifecycleMarker(0xABC101);
        var impact = PacketBuilder.MonsterLifecycleMarker(0xABC102);
        Check.Equal(
            2,
            await registry.BroadcastToMapAsync(
                hitCharacter.CurrentMap,
                visual,
                CancellationToken.None,
                label: "IntonedAreaStartCohortCheck"),
            "intoned AoE start reaches both map observers");
        var hitStart = await hitViewer.ReadPacketAsync(visual.Length);
        var observerStart = await observer.ReadPacketAsync(visual.Length);
        Check.Equal(
            0xABC101u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                hitStart.AsSpan(4, 4)),
            "hit viewer receives the intoned AoE start");
        Check.Equal(
            0xABC101u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                observerStart.AsSpan(4, 4)),
            "non-hit observer receives the intoned AoE start");

        Check.True(
            registry.TryApplyMonsterDamage(
                hitCharacter.CurrentMap,
                monster.ObjectId,
                damage: 17,
                out var damage),
            "intoned AoE fixture applies authoritative damage");
        Check.Equal(
            2,
            await registry.BroadcastMonsterAreaDamageToViewersAsync(
                hitCharacter.CurrentMap,
                visual,
                impact,
                WorldObjectIds.ForPlayer(hitCharacter.Id),
                skillId,
                [
                    new MonsterAreaDamageBroadcastHit(
                        damage.HealthMutation!.Value,
                        17)
                ],
                CancellationToken.None,
                labelPrefix: "IntonedAreaCompletionCohortCheck",
                publishCastVisual: false),
            "intoned AoE completion reaches the same two map observers");

        var hitImpact = await hitViewer.ReadPacketAsync(impact.Length);
        var hitCluster = await hitViewer.ReadPacketAsync(29);
        var observerImpact =
            await observer.ReadPacketAsync(impact.Length);
        Check.Equal(
            0xABC102u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                hitImpact.AsSpan(4, 4)),
            "hit viewer receives one intoned AoE impact");
        Check.Equal(
            (ushort)10047,
            BinaryPrimitives.ReadUInt16LittleEndian(
                hitCluster.AsSpan(2, 2)),
            "hit viewer also receives its filtered damage cluster");
        Check.Equal(
            1,
            BinaryPrimitives.ReadInt32LittleEndian(
                hitCluster.AsSpan(8, 4)),
            "hit viewer damage cluster contains one hit");
        Check.Equal(
            0xABC102u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                observerImpact.AsSpan(4, 4)),
            "non-hit observer still receives the intoned AoE impact");
        await Task.Delay(25);
        Check.Equal(
            0,
            hitViewer.Available,
            "hit viewer receives no duplicate intoned AoE impact");
        Check.Equal(
            0,
            observer.Available,
            "non-hit observer receives no monster-health packet");

        registry.Remove(hitViewer.Session);
        registry.Remove(observer.Session);
    }
}
