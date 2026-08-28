using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static readonly FieldInfo
        BeforeFoundationExactAdmissionObservation = RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckBeforeExactAdmissionObservation");

    private static readonly MethodInfo SendFoundationExactBatch =
        typeof(GameSessionRegistry).GetMethod(
            "TrySendMonsterAttackPacketBatchExactOutcome",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            "The exact monster-attack batch publisher is unavailable.");

    private static async Task CheckExactEgressOwnershipTruthAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        _ = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);
        var observerContext = fixture.Map.Snapshot().Single(context =>
            ReferenceEquals(context.Session, observerSocket.Session));
        var observerLife = fixture.Registry.GetPlayerLifeRevision(
            observerSocket.Session);
        var targetLife = fixture.Registry.GetPlayerLifeRevision(
            fixture.Socket.Session);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);
        var observationCalls = 0;
        BeforeFoundationExactAdmissionObservation.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                var call = Interlocked.Increment(
                    ref observationCalls);
                if (call is 1 or 3)
                {
                    throw new InvalidOperationException(
                        "simulated completion-observer creation fault");
                }
            }));

        try
        {
            var observerOutcome = InvokeFoundationExactBatch(
                fixture.Registry,
                fixture.Runtime,
                observerContext,
                observerLife,
                fixture.Context,
                targetLife,
                [MedusaTestPacket(0x7D11), MedusaTestPacket(0x7D12)],
                "PeriodicFoundationObserver");
            var selfOutcome = InvokeFoundationExactBatch(
                fixture.Registry,
                fixture.Runtime,
                fixture.Context,
                targetLife,
                fixture.Context,
                targetLife,
                [MedusaTestPacket(0x7D13), MedusaTestPacket(0x7D14)],
                "PeriodicFoundationSelf");
            var observerFirst = await observerSocket.ReadPacketAsync();
            var observerSecond = await observerSocket.ReadPacketAsync();
            var selfFirst = await fixture.Socket.ReadPacketAsync();
            var selfSecond = await fixture.Socket.ReadPacketAsync();
            Check.True(
                observerOutcome == "Admitted" &&
                selfOutcome == "Admitted" &&
                observationCalls == 2 &&
                MedusaPacketOpcode(observerFirst) == 0x7D11 &&
                MedusaPacketOpcode(observerSecond) == 0x7D12 &&
                MedusaPacketOpcode(selfFirst) == 0x7D13 &&
                MedusaPacketOpcode(selfSecond) == 0x7D14 &&
                observerSocket.Available == 0 &&
                fixture.Socket.Available == 0 &&
                !observerSocket.Session.IsDisconnected &&
                !fixture.Socket.Session.IsDisconnected,
                "a completion-observer creation fault cannot change truthful owned admission, replay bytes, or suppress the later self recipient's exact FIFO batch");

            observerSocket.Session
                .ProtocolCheckFailNextExactBatchAfterCommit();
            var terminalOutcome = InvokeFoundationExactBatch(
                fixture.Registry,
                fixture.Runtime,
                observerContext,
                observerLife,
                fixture.Context,
                targetLife,
                [MedusaTestPacket(0x7D15), MedusaTestPacket(0x7D16)],
                "PeriodicFoundationAdmittedTerminal");
            var isolatedSelfOutcome = InvokeFoundationExactBatch(
                fixture.Registry,
                fixture.Runtime,
                fixture.Context,
                targetLife,
                fixture.Context,
                targetLife,
                [MedusaTestPacket(0x7D17), MedusaTestPacket(0x7D18)],
                "PeriodicFoundationTerminalIsolation");
            var isolatedSelfFirst =
                await fixture.Socket.ReadPacketAsync();
            var isolatedSelfSecond =
                await fixture.Socket.ReadPacketAsync();
            Check.True(
                terminalOutcome == "AdmittedTerminal" &&
                observerSocket.Session.IsDisconnected &&
                fixture.Map.Snapshot().All(context =>
                    !ReferenceEquals(
                        context.Session,
                        observerSocket.Session)) &&
                observerSocket.Available == 0 &&
                isolatedSelfOutcome == "Admitted" &&
                observationCalls == 4 &&
                MedusaPacketOpcode(isolatedSelfFirst) == 0x7D17 &&
                MedusaPacketOpcode(isolatedSelfSecond) == 0x7D18 &&
                fixture.Socket.Available == 0 &&
                !fixture.Socket.Session.IsDisconnected,
                "AdmittedTerminal truthfully reports transferred batch ownership with no retry and fail-closes only that recipient while a later healthy self recipient retains one FIFO batch");
        }
        finally
        {
            BeforeFoundationExactAdmissionObservation.SetValue(
                fixture.Registry,
                null);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static string InvokeFoundationExactBatch(
        GameSessionRegistry registry,
        WorldInstanceRuntime runtime,
        GameSessionContext recipient,
        long recipientLife,
        GameSessionContext target,
        long targetLife,
        IReadOnlyList<ReadOnlyMemory<byte>> packets,
        string label) =>
        SendFoundationExactBatch.Invoke(
                registry,
                [
                    runtime,
                    recipient,
                    recipientLife,
                    target,
                    targetLife,
                    packets,
                    CancellationToken.None,
                    label,
                    null,
                    false
                ])?.ToString() ??
            throw new InvalidOperationException(
                "The exact batch publisher returned no outcome.");
#endif
}
