using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static async Task CheckStatusGatedObservationFaultAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var transport = new SwitchableMedusaTransport();
        await using var session = await AttachControlledStatusSessionAsync(
            fixture,
            transport);
        var observationCalls = 0;
        var gateWasFree = false;
        BeforeFoundationExactAdmissionObservation.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                Interlocked.Increment(ref observationCalls);
                throw new InvalidOperationException(
                    "simulated status-gated observer creation fault");
            }));
        ExactStatusDisconnectHook.SetValue(
            fixture.Registry,
            (Action<ClientSession>)(recipient =>
            {
                if (ReferenceEquals(recipient, session))
                {
                    gateWasFree = IsMedusaStatusGateFree(
                        fixture.Registry,
                        session);
                }
            }));
        session.ProtocolCheckFailNextExactBatchAfterCommit();

        try
        {
            var snapshot = await fixture.Registry
                .SendStatusSnapshotToSelfAsync(
                    session,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None,
                    "PeriodicFoundationStatusObservation");
            Check.True(
                snapshot is not null &&
                observationCalls == 1 &&
                session.IsDisconnected &&
                gateWasFree &&
                fixture.Map.Snapshot().All(context =>
                    !ReferenceEquals(context.Session, session)),
                "a status-gated completion-observer fault preserves terminal-owned non-null projection truth and defers teardown until the status gate is free " +
                $"(snapshot={snapshot is not null}, calls={observationCalls}, " +
                $"disconnected={session.IsDisconnected}, gate-free={gateWasFree}, " +
                $"member={fixture.Map.Snapshot().Any(context => ReferenceEquals(context.Session, session))})");
        }
        finally
        {
            BeforeFoundationExactAdmissionObservation.SetValue(
                fixture.Registry,
                null);
            ExactStatusDisconnectHook.SetValue(
                fixture.Registry,
                null);
        }
    }

    private static async Task CheckCastStartTerminalOwnershipAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite", 102);
        await using var observerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var observer = JoinMedusaHandlerMember(
            fixture,
            observerSocket.Session,
            characterId: 102);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            observerSocket.Session,
            observer);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);
        observerSocket.Session
            .ProtocolCheckFailNextExactBatchAfterCommit();

        try
        {
            var admitted = await fixture.Registry
                .BroadcastMonsterCastStartToViewersAdmissionAsync(
                    fixture.Socket.Session,
                    fixture.Character.CurrentMap,
                    fixture.Source.ObjectId,
                    MedusaTestPacket(Opcodes.SkillCast),
                    fixture.Source.SpawnGeneration,
                    CancellationToken.None,
                    "PeriodicFoundationCastStartTerminal");
            var replayed = await fixture.Registry
                .BroadcastMonsterCastStartToViewersAdmissionAsync(
                    fixture.Socket.Session,
                    fixture.Character.CurrentMap,
                    fixture.Source.ObjectId,
                    MedusaTestPacket(Opcodes.SkillCast),
                    fixture.Source.SpawnGeneration,
                    CancellationToken.None,
                    "PeriodicFoundationCastStartReplay");
            Check.True(
                admitted == 1 &&
                replayed == 0 &&
                observerSocket.Session.IsDisconnected &&
                fixture.Map.Snapshot().All(context =>
                    !ReferenceEquals(
                        context.Session,
                        observerSocket.Session)) &&
                observerSocket.Available == 0 &&
                !fixture.Socket.Session.IsDisconnected,
                "cast-start AdmittedTerminal counts as one owned delivery, claims only recipient teardown, and cannot replay after exact membership removal");
        }
        finally
        {
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

#endif
}
