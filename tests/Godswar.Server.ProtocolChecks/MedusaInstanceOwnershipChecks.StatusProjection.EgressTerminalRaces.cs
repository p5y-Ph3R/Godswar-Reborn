using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static async Task
        CheckRealPumpFailureTerminalizesDuringStatusGateAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var transport = new SwitchableMedusaTransport();
        await using var session = await AttachControlledStatusSessionAsync(
            fixture,
            transport);
        var gate = GetMedusaStatusGate(fixture.Registry, session);

        transport.BlockWrites();
        Check.True(
            session.TryAdmitExact(
                MedusaTestPacket(MedusaStatusOpcode),
                out var completion),
            "the pump-failure fixture admits one exact status before transport failure");
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await gate.WaitAsync(CancellationToken.None);
        try
        {
            transport.FailBlockedWrite(
                new IOException("simulated physical status write fault"));
            Check.True(
                SpinWait.SpinUntil(
                    () => session.IsDisconnected &&
                        fixture.Map.Snapshot().All(context =>
                            !ReferenceEquals(context.Session, session)),
                    TimeSpan.FromSeconds(5)),
                "a real pump fault marks the session terminal and removes its world membership without waiting on the status gate");
        }
        finally
        {
            gate.Release();
        }
        await ObserveExpectedExactFailureAsync(completion);
        Check.True(
            transport.IsDisconnected,
            "the pump terminalizer independently closes the transport");
    }

    private static async Task
        CheckAdmittedTerminalFailsClosedAfterStatusGateAsync()
    {
        await CheckDirectAdmittedTerminalBatchAsync();
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var transport = new SwitchableMedusaTransport();
        await using var session = await AttachControlledStatusSessionAsync(
            fixture,
            transport);
        var gateWasFree = false;
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
                    "MedusaAdmittedTerminalCheck");
            Check.True(
                snapshot is not null &&
                session.IsDisconnected &&
                gateWasFree &&
                fixture.Map.Snapshot().All(context =>
                    !ReferenceEquals(context.Session, session)),
                "an exact post-commit queue fault truthfully returns an owned snapshot, then lexically disconnects and removes only after the status gate is released");
        }
        finally
        {
            ExactStatusDisconnectHook.SetValue(
                fixture.Registry,
                null);
        }
    }

    private static async Task CheckDirectAdmittedTerminalBatchAsync()
    {
        var transport = new SwitchableMedusaTransport();
        await using var session = new ClientSession(
            transport,
            new NetworkRuntimeOptions
            {
                ReliableEgressQueueItems = 2,
                ReliableEgressQueueBytes =
                    LegacyProtocolLimits.MaxPacketLength * 2,
                ReliableEgressPendingItems = 4,
                ReliableEgressPendingBytes =
                    LegacyProtocolLimits.MaxPacketLength * 2,
                ReliableWriteTimeoutMilliseconds = 10_000
            },
            NetworkEndpointRole.Game);
        session.ProtocolCheckFailNextExactBatchAfterCommit();
        var outcome = session.TryAdmitExactBatchOutcome(
            [
                MedusaTestPacket(0x7C01),
                MedusaTestPacket(0x7C02)
            ],
            out var completion);
        await ObserveExpectedExactFailureAsync(completion);
        Check.True(
            outcome == ExactEgressAdmissionOutcome.AdmittedTerminal &&
            completion.IsCompleted && completion.IsFaulted &&
            transport.WriteCount == 0,
            "a two-packet post-commit fault truthfully returns AdmittedTerminal, settles its aggregate completion, and writes no physical prefix");
        session.Disconnect();
        Check.True(
            transport.IsDisconnected,
            "finalizing an AdmittedTerminal session closes its sealed egress");
    }

    private static async Task<ClientSession>
        AttachControlledStatusSessionAsync(
        MonsterPlayerHitFixture fixture,
        SwitchableMedusaTransport transport)
    {
        var ownership = fixture.Ownership;
        fixture.Registry.Remove(fixture.Socket.Session);
        var session = new ClientSession(
            transport,
            new NetworkRuntimeOptions
            {
                ReliableEgressQueueItems = 8,
                ReliableEgressQueueBytes =
                    LegacyProtocolLimits.MaxPacketLength * 8,
                ReliableEgressPendingItems = 16,
                ReliableEgressPendingBytes =
                    LegacyProtocolLimits.MaxPacketLength * 8,
                ReliableWriteTimeoutMilliseconds = 10_000
            },
            NetworkEndpointRole.Game);
        fixture.Registry.ReplaceAccountSession(
            fixture.Character.AccountId,
            session);
        Check.True(
            fixture.Registry.TryBindAccountSessionOwnership(
                fixture.Character.AccountId,
                session,
                ownership),
            "the controlled terminal fixture binds current ownership");
        fixture.Registry.JoinWorldInstance(
            session,
            fixture.Character.AccountId,
            fixture.Character,
            fixture.PlayerObjectId,
            fixture.Runtime.InstanceId,
            worldReady: true,
            joinedAt: DateTimeOffset.UtcNow);
        fixture.Context = fixture.Map.Snapshot().Single(context =>
            ReferenceEquals(context.Session, session));

        Check.True(
            fixture.Registry.TryAcceptCharacterUiStatsV1CapabilityProbe(
                session,
                DateTimeOffset.UtcNow),
            "the controlled terminal fixture creates a status state without publishing egress");
        return session;
    }
#endif
}
