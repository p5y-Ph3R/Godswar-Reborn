using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static async Task
        CheckSlowObserverDoesNotDelayMedusaInterruptionAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite", 102);
        var observerTransport = new SwitchableMedusaTransport();
        var options = new NetworkRuntimeOptions
        {
            ReliableEgressQueueItems = 32,
            ReliableEgressQueueBytes =
                LegacyProtocolLimits.MaxPacketLength * 32,
            ReliableEgressPendingItems = 64,
            ReliableEgressPendingBytes =
                LegacyProtocolLimits.MaxPacketLength * 32,
            ReliableWriteTimeoutMilliseconds = 10_000
        };
        await using var observerSession = new ClientSession(
            observerTransport,
            options,
            NetworkEndpointRole.Game);
        var observer = JoinMedusaHandlerMember(
            fixture,
            observerSession,
            characterId: 102);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            observerSession,
            observer);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            fixture.Socket.Session,
            fixture.Character);

        var handler = CreateMedusaHandler(
            fixture.Socket.Session,
            fixture.Registry,
            fixture.Character,
            new MedusaHandlerStore(fixture.Character));
        RegisterMedusaCastInterruption(handler);
        await DrainMedusaPacketsAsync(fixture.Socket);
        var observerPacketOffset = DecryptMedusaOpcodes(
            observerTransport.WrittenBytes).Count;
        observerTransport.BlockWrites();

        try
        {
            await InvokeMedusaPacketAsync(
                    handler,
                    MedusaSkillPacket(
                        fixture.Character,
                        fixture.Source))
                .WaitAsync(TimeSpan.FromSeconds(1));
            Check.True(
                MedusaPacketOpcode(
                    await fixture.Socket.ReadPacketAsync()) ==
                        Opcodes.SkillCast,
                "the target cast start is admitted before a blocked observer drains");

            var eventId = fixture.FindEvent(
                8_075_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            var attack = fixture.AttackAsync(
                fixture.CreateAttack(eventId));
            Check.True(
                await Task.WhenAny(
                    attack,
                    Task.Delay(TimeSpan.FromSeconds(1))) == attack,
                "a physically blocked observer cannot hold a two-second Medusa cast generation");
            _ = await attack;
            _ = await ReadMedusaInterruptedSequenceAsync(
                fixture.Socket,
                localTarget: true,
                fixture.Source.ObjectId,
                MedusaHandlerLocalObjectId,
                expectedSkillId: 2002,
                expectedStatusId: 330,
                expectedDuration: 2,
                Godswar.Server.Packets.PacketBuilder.PlayerStatusUpdate(
                    fixture.Character,
                    Godswar.Server.State.ClientStatusAggregate.Empty));
            Check.True(
                !MedusaHasPendingCast(handler) &&
                !fixture.Socket.Session.IsDisconnected &&
                !observerSession.IsDisconnected,
                "10171 admission releases the target generation without waiting for observer transport completion");

            observerTransport.ReleaseWrites();
            Check.True(
                SpinWait.SpinUntil(
                    () => TryReadSlowObserverSequence(
                        observerTransport,
                        observerPacketOffset,
                        out _),
                    TimeSpan.FromSeconds(5)) &&
                TryReadSlowObserverSequence(
                    observerTransport,
                    observerPacketOffset,
                    out var sequence) &&
                sequence.Take(7).SequenceEqual(
                [
                    Opcodes.SkillCast,
                    MedusaImpactOpcode,
                    MedusaImpactOpcode,
                    MedusaPhysicalDamageOpcode,
                    MedusaStatusOpcode,
                    MedusaStatusOpcode,
                    Opcodes.SkillCastInterrupt
                ]) &&
                sequence.Count(opcode =>
                    opcode == Opcodes.SkillCastInterrupt) == 1,
                "the slow observer drains cast-start, impact, status, and one interruption in FIFO order");
        }
        finally
        {
            observerTransport.ReleaseWrites();
            UnregisterMedusaCastInterruption(handler);
            await StopMedusaPendingCastsAsync(handler);
            fixture.Registry.Remove(observerSession);
        }
    }

    private static bool TryReadSlowObserverSequence(
        SwitchableMedusaTransport transport,
        int packetOffset,
        out IReadOnlyList<ushort> sequence)
    {
        try
        {
            sequence = DecryptMedusaOpcodes(
                    transport.WrittenBytes)
                .Skip(packetOffset)
                .ToArray();
            return sequence.Contains(Opcodes.SkillCastInterrupt);
        }
        catch (Exception)
        {
            sequence = [];
            return false;
        }
    }

    private static async Task
        CheckHeldViewerTransitionDoesNotDelayCastStartAsync()
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
            fixture.Socket.Session,
            fixture.Character);
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            observerSocket.Session,
            observer);
        var heldTransition = await fixture.Registry
            .BeginMonsterVisibilityTransitionAsync(
                observerSocket.Session,
                observer.CurrentMap,
                observer.PositionX,
                observer.PositionZ,
                CancellationToken.None)
            ?? throw new InvalidOperationException(
                "The observer transition could not be held.");
        var transitionReleased = false;
        var handler = CreateMedusaHandler(
            fixture.Socket.Session,
            fixture.Registry,
            fixture.Character,
            new MedusaHandlerStore(fixture.Character));
        RegisterMedusaCastInterruption(handler);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);

        try
        {
            var cast = InvokeMedusaPacketAsync(
                handler,
                MedusaSkillPacket(
                    fixture.Character,
                    fixture.Source));
            Check.True(
                await Task.WhenAny(
                    cast,
                    Task.Delay(TimeSpan.FromSeconds(1))) == cast,
                "a held observer transition cannot invert the registry/visibility lock order or delay target cast admission");
            await cast;
            Check.True(
                MedusaPacketOpcode(
                    await fixture.Socket.ReadPacketAsync()) ==
                    Opcodes.SkillCast &&
                !fixture.Socket.Session.IsDisconnected &&
                observerSocket.Session.IsDisconnected,
                "busy captured observer visibility fails closed while the exact target cast start remains live");

            var remove = Task.Run(() =>
                fixture.Registry.Remove(observerSocket.Session));
            await Task.Delay(25);
            await heldTransition.DisposeAsync();
            transitionReleased = true;
            await remove.WaitAsync(TimeSpan.FromSeconds(1));
            Check.True(
                remove.IsCompletedSuccessfully,
                "observer removal completes after the held transition releases without a registry/transition deadlock");
        }
        finally
        {
            if (!transitionReleased)
            {
                await heldTransition.DisposeAsync();
            }
            UnregisterMedusaCastInterruption(handler);
            await StopMedusaPendingCastsAsync(handler);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }
#endif
}
