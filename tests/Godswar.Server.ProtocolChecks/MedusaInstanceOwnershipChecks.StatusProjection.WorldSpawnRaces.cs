using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static readonly FieldInfo MedusaWorldSpawnCaptureHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckAfterMedusaWorldSpawnCapture");
    private static readonly FieldInfo BoundMedusaViewerCaptureHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckAfterBoundMedusaViewerStatusCapture");

    private static async Task
        CheckMedusaWorldSpawnBaselineRevisionRaceAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync(
                "Final-Pikeman-1",
                102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        _ = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);

        var eventId = fixture.FindEvent(
            8_078_000,
            static resolution => resolution.Hit &&
                resolution.Damage > 0);
        _ = await fixture.AttackAsync(
            fixture.CreateAttack(eventId));
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);

        var revisionAdvanced = false;
        StatusProjectionBaselineHook.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                StatusProjectionBaselineHook.SetValue(
                    fixture.Registry,
                    null);
                var before = fixture.Map.Snapshot().Single(context =>
                    ReferenceEquals(
                        context.Session,
                        fixture.Socket.Session));
                fixture.Registry.UpdateCharacter(
                    fixture.Socket.Session,
                    fixture.Character,
                    advanceWorldRevision: true);
                fixture.Context = fixture.Map.Snapshot().Single(context =>
                    ReferenceEquals(
                        context.Session,
                        fixture.Socket.Session));
                revisionAdvanced =
                    fixture.Context.WorldRevision ==
                        before.WorldRevision + 1 &&
                    fixture.Context.WorldMembershipEpoch ==
                        before.WorldMembershipEpoch;
            }));

        try
        {
            var handled = await fixture.Registry
                .TryBroadcastMedusaWorldSpawnRefreshAsync(
                    fixture.Socket.Session,
                    CancellationToken.None,
                    "MedusaWorldSpawnBaselineRace");
            var spawn = await observerSocket.ReadPacketAsync();
            var status = await observerSocket.ReadPacketAsync();
            Check.True(
                handled == 1 &&
                revisionAdvanced &&
                MedusaPacketOpcode(spawn) == 0x2725 &&
                MedusaPacketOpcode(status) == MedusaStatusOpcode &&
                MedusaStatusDuration(status, 236) is > 0 and <= 30 &&
                observerSocket.Available == 0 &&
                !fixture.Socket.Session.IsDisconnected &&
                !observerSocket.Session.IsDisconnected,
                "a same-membership revision between baseline composition and Medusa merge recomposes one exact spawn/status pair instead of returning handled zero");
        }
        finally
        {
            StatusProjectionBaselineHook.SetValue(
                fixture.Registry,
                null);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task
        CheckMedusaWorldSpawnProjectionExhaustionAsync()
    {
        await using var fixture = await CreateAmpProjectionFixtureAsync(103);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        _ = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 103);
        await DrainMedusaPacketsAsync(observerSocket);
        var hookCalls = 0;
        var applied = 0;
        MedusaWorldSpawnCaptureHook.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                var ordinal = Interlocked.Increment(ref hookCalls);
                if (fixture.Registry
                    .ProtocolCheckMutateRuntimeStatusWhileGateHeld(
                        fixture.Socket.Session,
                        ordinal,
                        DateTimeOffset.UtcNow))
                {
                    Interlocked.Increment(ref applied);
                }
            }));

        try
        {
            var handled = await fixture.Registry
                .TryBroadcastMedusaWorldSpawnRefreshAsync(
                    fixture.Socket.Session,
                    CancellationToken.None,
                    "MedusaWorldSpawnProjectionExhaustion");
            Check.True(
                handled == 0 &&
                hookCalls == 2 &&
                applied == 2 &&
                observerSocket.Session.IsDisconnected &&
                !fixture.Socket.Session.IsDisconnected &&
                observerSocket.Available == 0,
                $"two target projection changes admit no partial spawn/status pair and exact-pair fail-close only the still-current observer (handled={handled}, hooks={hookCalls}, applied={applied}, observer-disconnected={observerSocket.Session.IsDisconnected}, target-disconnected={fixture.Socket.Session.IsDisconnected}, available={observerSocket.Available})");
        }
        finally
        {
            MedusaWorldSpawnCaptureHook.SetValue(
                fixture.Registry,
                null);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task
        CheckMedusaWorldSpawnViewerEpochSurvivesAsync()
    {
        await using var fixture = await CreateAmpProjectionFixtureAsync(104);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var observer = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 104);
        var oldContext = fixture.Map.Snapshot().Single(context =>
            ReferenceEquals(context.Session, observerSocket.Session));
        var replaced = false;
        MedusaWorldSpawnCaptureHook.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                MedusaWorldSpawnCaptureHook.SetValue(
                    fixture.Registry,
                    null);
                fixture.Registry.Remove(observerSocket.Session);
                fixture.Registry.JoinWorldInstance(
                    observerSocket.Session,
                    observer.AccountId,
                    observer,
                    oldContext.ObjectId,
                    fixture.Runtime.InstanceId,
                    worldReady: true,
                    joinedAt: DateTimeOffset.UtcNow);
                var current = fixture.Map.Snapshot().Single(context =>
                    ReferenceEquals(
                        context.Session,
                        observerSocket.Session));
                replaced = current.WorldMembershipEpoch !=
                    oldContext.WorldMembershipEpoch;
            }));

        try
        {
            var handled = await fixture.Registry
                .TryBroadcastMedusaWorldSpawnRefreshAsync(
                    fixture.Socket.Session,
                    CancellationToken.None,
                    "MedusaWorldSpawnViewerEpochRace");
            Check.True(
                handled == 0 &&
                replaced &&
                !observerSocket.Session.IsDisconnected &&
                !fixture.Socket.Session.IsDisconnected,
                "an old captured world-spawn observer cannot disconnect a remove/rejoin membership epoch");
        }
        finally
        {
            MedusaWorldSpawnCaptureHook.SetValue(
                fixture.Registry,
                null);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task
        CheckBoundMedusaViewerProjectionExhaustionAsync()
    {
        await using var fixture = await CreateAmpProjectionFixtureAsync(105);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        _ = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 105);
        await DrainMedusaPacketsAsync(observerSocket);
        var hookCalls = 0;
        var applied = 0;
        BoundMedusaViewerCaptureHook.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                var ordinal = Interlocked.Increment(ref hookCalls);
                if (fixture.Registry
                    .ProtocolCheckMutateRuntimeStatusWhileGateHeld(
                        fixture.Socket.Session,
                        ordinal + 10,
                        DateTimeOffset.UtcNow))
                {
                    Interlocked.Increment(ref applied);
                }
            }));

        try
        {
            await fixture.Registry.SendBoundMedusaStatusSnapshotToViewerAsync(
                fixture.Context,
                observerSocket.Session,
                CancellationToken.None);
            Check.True(
                hookCalls == 2 &&
                applied == 2 &&
                observerSocket.Session.IsDisconnected &&
                !fixture.Socket.Session.IsDisconnected &&
                observerSocket.Available == 0,
                "two complete-snapshot changes admit no stale bound-viewer 10167 and exact-pair fail-close only the current viewer");
        }
        finally
        {
            BoundMedusaViewerCaptureHook.SetValue(
                fixture.Registry,
                null);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task<MonsterPlayerHitFixture>
        CreateAmpProjectionFixtureAsync(
            params int[] additionalAdmittedCharacterIds)
    {
        var fixture = await MonsterPlayerHitFixture.CreateAsync(
            "Final-Pikeman-1",
            additionalAdmittedCharacterIds);
        await DrainMedusaPacketsAsync(fixture.Socket);
        _ = await fixture.AttackAsync(
            fixture.CreateAttack(
                fixture.FindEvent(
                    9_970_000,
                    static resolution => resolution.Hit &&
                        resolution.Damage > 0)));
        await DrainMedusaPacketsAsync(fixture.Socket);
        return fixture;
    }

#endif
}
