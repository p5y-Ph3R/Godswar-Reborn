using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static readonly MethodInfo TrySendMonsterAttackPacketExact =
        typeof(GameSessionRegistry).GetMethod(
            "TrySendMonsterAttackPacketExactAsync",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "Exact monster-attack publication method is unavailable.");

    private static async Task
        CheckMonsterAttackPublicationTargetFenceAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync(
                "Euryale",
                102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var observer = CreateRegistryDamageCharacter(102, mapId: 200);
        observer.CheckpointOwnerId = Guid.NewGuid();
        observer.CheckpointOwnerGeneration = 1;
        var observerOwnership = new PlayerOwnershipFence(
            observer.CheckpointOwnerId,
            observer.CheckpointOwnerGeneration);
        fixture.Registry.ReplaceAccountSession(
            observer.AccountId,
            observerSocket.Session);
        Check.True(
            fixture.Registry.TryBindAccountSessionOwnership(
                observer.AccountId,
                observerSocket.Session,
                observerOwnership),
            "publication observer binds exact ownership");
        fixture.Registry.JoinWorldInstance(
            observerSocket.Session,
            observer.AccountId,
            observer,
            WorldObjectIds.ForPlayer(observer.Id),
            fixture.Runtime.InstanceId,
            worldReady: true,
            joinedAt: DateTimeOffset.UtcNow);

        try
        {
            var observerContext = fixture.Map.Snapshot().Single(value =>
                ReferenceEquals(
                    value.Session,
                    observerSocket.Session));
            var capturedTarget = fixture.Context;
            var capturedTargetLife = fixture.Registry
                .GetPlayerLifeRevision(fixture.Socket.Session);
            var observerLife = fixture.Registry
                .GetPlayerLifeRevision(observerSocket.Session);
            await fixture.ReconnectAndReacquireAsync();
            var currentTarget = fixture.Context;
            var currentTargetLife = fixture.Registry
                .GetPlayerLifeRevision(fixture.Socket.Session);
            var packet = new byte[] { 4, 0, 0, 0 };
            var baselineBytes = observerSocket.Available;

            var staleSent = await InvokeExactMonsterAttackSendAsync(
                fixture.Registry,
                fixture.Runtime,
                observerContext,
                observerLife,
                capturedTarget,
                capturedTargetLife,
                packet);
            Check.True(
                !staleSent &&
                observerSocket.Available == baselineBytes,
                "unchanged observer receives no old-context target packet after target reconnect");

            var currentSent = await InvokeExactMonsterAttackSendAsync(
                fixture.Registry,
                fixture.Runtime,
                observerContext,
                observerLife,
                currentTarget,
                currentTargetLife,
                packet);
            var currentDelivered = SpinWait.SpinUntil(
                () => observerSocket.Available >=
                    baselineBytes + packet.Length,
                TimeSpan.FromSeconds(2));
            Check.True(
                currentSent &&
                currentDelivered &&
                observerSocket.Available ==
                    baselineBytes + packet.Length,
                "exact reconnected target allows exactly one observer packet");

            long lethalVitalsRevision;
            lock (fixture.Character.VitalsSync)
            {
                fixture.Character.CurrentHp = 0;
                fixture.Character.MarkVitalsChanged();
                lethalVitalsRevision =
                    fixture.Character.VitalsRevision;
                fixture.Character.CurrentHp = 1;
                fixture.Character.MarkVitalsChanged();
            }
            var beforeStaleLethal = observerSocket.Available;
            var staleLethalSent =
                await InvokeExactMonsterAttackSendAsync(
                    fixture.Registry,
                    fixture.Runtime,
                    observerContext,
                    observerLife,
                    currentTarget,
                    currentTargetLife,
                    packet,
                    lethalVitalsRevision,
                    requireTargetDead: true);
            Check.True(
                !staleLethalSent &&
                observerSocket.Available == beforeStaleLethal &&
                !fixture.Socket.Session.IsDisconnected &&
                !observerSocket.Session.IsDisconnected,
                "recovered target rejects an old lethal-vitals publication epoch");
        }
        finally
        {
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task<bool>
        InvokeExactMonsterAttackSendAsync(
            GameSessionRegistry registry,
            WorldInstanceRuntime runtime,
            GameSessionContext recipient,
            long recipientLifeRevision,
            GameSessionContext eventTarget,
            long targetLifeRevision,
            ReadOnlyMemory<byte> packet,
            long? targetVitalsRevision = null,
            bool requireTargetDead = false)
    {
        var invocation = TrySendMonsterAttackPacketExact.Invoke(
            registry,
            [
                runtime,
                recipient,
                recipientLifeRevision,
                eventTarget,
                targetLifeRevision,
                packet,
                CancellationToken.None,
                "monster-publication-fence-check",
                targetVitalsRevision,
                requireTargetDead
            ]);
        return await (Task<bool>)(invocation ??
            throw new InvalidOperationException(
                "Exact monster publication did not return a task."));
    }
}
