using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static bool IsMedusaStatusGateFree(
        GameSessionRegistry registry,
        ClientSession session)
    {
        var gate = GetMedusaStatusGate(registry, session);
        if (!gate.Wait(0))
        {
            return false;
        }

        gate.Release();
        return true;
    }

    private static object GetMedusaStatusState(
        GameSessionRegistry registry,
        ClientSession session)
    {
        var states = PlayerStatusStatesField.GetValue(registry) ??
            throw new InvalidOperationException(
                "Player status states are unavailable.");
        var arguments = new object?[] { session, null };
        var found = (bool)(states.GetType().GetMethod("TryGetValue")?
            .Invoke(states, arguments) ?? false);
        return found && arguments[1] is { } state
            ? state
            : throw new InvalidOperationException(
                "Player status state was not found.");
    }

    private static SemaphoreSlim GetMedusaStatusGate(
        GameSessionRegistry registry,
        ClientSession session)
    {
        var state = GetMedusaStatusState(registry, session);
        return state.GetType().GetProperty("Gate")?
            .GetValue(state) as SemaphoreSlim ??
            throw new InvalidOperationException(
                "Player status gate is unavailable.");
    }

    private static async Task CheckExactBatchAdmissionIsAtomicAsync()
    {
        var transport = new ControlledLegacyByteTransport(
            blockWrites: true);
        var options = new NetworkRuntimeOptions
        {
            ReliableEgressQueueItems = 2,
            ReliableEgressQueueBytes =
                LegacyProtocolLimits.MaxPacketLength * 2,
            ReliableEgressPendingItems = 4,
            ReliableEgressPendingBytes =
                LegacyProtocolLimits.MaxPacketLength * 2,
            ReliableWriteTimeoutMilliseconds = 10_000
        };
        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Game);
        var first = session.SendAsync(
            MedusaTestPacket(0x7A01),
            CancellationToken.None);
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = session.SendAsync(
            MedusaTestPacket(0x7A02),
            CancellationToken.None);

        var admitted = session.TryAdmitExactBatch(
            [
                MedusaTestPacket(0x7A03),
                MedusaTestPacket(0x7A04)
            ],
            out var completion);
        Check.True(
            !admitted && completion.IsCompleted &&
            transport.WriteCount == 0,
            "a full reliable egress synchronously rejects an exact batch without admitting a prefix");

        transport.ReleaseWrites();
        await Task.WhenAll(first, queued).WaitAsync(
            TimeSpan.FromSeconds(5));
        Check.True(
            DecryptMedusaOpcodes(transport.WrittenBytes)
                .SequenceEqual(new ushort[] { 0x7A01, 0x7A02 }),
            "releasing a full queue writes only its two prior packets and never a rejected exact-batch prefix");
    }

    private static async Task
        CheckLiveMedusaFullEgressFailsClosedAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var oldOwnership = fixture.Ownership;
        fixture.Registry.Remove(fixture.Socket.Session);

        var transport = new SwitchableMedusaTransport();
        var options = new NetworkRuntimeOptions
        {
            ReliableEgressQueueItems = 3,
            ReliableEgressQueueBytes =
                LegacyProtocolLimits.MaxPacketLength * 3,
            ReliableEgressPendingItems = 6,
            ReliableEgressPendingBytes =
                LegacyProtocolLimits.MaxPacketLength * 3,
            ReliableWriteTimeoutMilliseconds = 10_000
        };
        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Game);
        fixture.Registry.ReplaceAccountSession(
            fixture.Character.AccountId,
            session);
        Check.True(
            fixture.Registry.TryBindAccountSessionOwnership(
                fixture.Character.AccountId,
                session,
                oldOwnership),
            "controlled exact-egress target binds current ownership");
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
        var liveSource = await ReacquireMedusaSourceAsync(
            fixture,
            session);

        var handler = CreateMedusaHandler(
            session,
            fixture.Registry,
            fixture.Character,
            new MedusaHandlerStore(fixture.Character));
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            session,
            fixture.Character);
        RegisterMedusaCastInterruption(handler);
        await InvokeMedusaPacketAsync(
            handler,
            MedusaSkillPacket(fixture.Character, liveSource));

        var attackSegmentPacketOffset = DecryptMedusaOpcodes(
            transport.WrittenBytes).Count;
        AfterMedusaStatusSelfAdmissionHook.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                AfterMedusaStatusSelfAdmissionHook.SetValue(
                    fixture.Registry,
                    null);
                Check.True(
                    SpinWait.SpinUntil(
                        () => DecryptMedusaOpcodes(
                                transport.WrittenBytes)
                            .Skip(attackSegmentPacketOffset)
                            .Count(opcode =>
                                opcode == MedusaStatusOpcode) == 1,
                        TimeSpan.FromSeconds(5)),
                    "the live saturated-path fixture drains the one initial status before filling egress");
            }));

        Task? blocked = null;
        Task? queued = null;
        var gateChecks = 0;
        var everyGateCheckWasFree = true;
        ExactStatusDisconnectHook.SetValue(
            fixture.Registry,
            (Action<ClientSession>)(recipient =>
            {
                if (ReferenceEquals(recipient, session))
                {
                    Interlocked.Increment(ref gateChecks);
                    everyGateCheckWasFree &= IsMedusaStatusGateFree(
                        fixture.Registry,
                        session);
                }
            }));
        BeforeMedusaInterruptHook.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                BeforeMedusaInterruptHook.SetValue(
                    fixture.Registry,
                    null);
                transport.BlockWrites();
                blocked = session.SendAsync(
                    MedusaTestPacket(0x7B01),
                    CancellationToken.None);
                transport.WriteStarted.GetAwaiter().GetResult();
                queued = session.SendAsync(
                    MedusaTestPacket(0x7B02),
                    CancellationToken.None);
            }));

        try
        {
            int beforeHp;
            lock (fixture.Character.VitalsSync)
            {
                beforeHp = fixture.Character.CurrentHp;
            }
            var eventId = fixture.FindEvent(
                8_580_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            await fixture.Registry.ProcessMonsterAttackForSessionAsync(
                session,
                fixture.CreateAttack(
                    eventId,
                    source: liveSource,
                    targetLifeRevision: fixture.Registry
                        .GetPlayerLifeRevision(session)),
                CancellationToken.None);
            await Task.Delay(50);

            int afterHp;
            lock (fixture.Character.VitalsSync)
            {
                afterHp = fixture.Character.CurrentHp;
            }
            var opcodes = DecryptMedusaOpcodes(
                    transport.WrittenBytes)
                .Skip(attackSegmentPacketOffset)
                .ToArray();
            Check.True(
                afterHp < beforeHp &&
                fixture.Mechanics().ActiveEffects.Length == 0 &&
                session.IsDisconnected &&
                gateChecks > 0 && everyGateCheckWasFree &&
                !MedusaHasPendingCast(handler) &&
                opcodes.Count(opcode =>
                    opcode == MedusaStatusOpcode) == 1 &&
                opcodes.Count(opcode =>
                    opcode == 10166) == 0 &&
                opcodes.Count(opcode =>
                    opcode == Opcodes.SkillCastInterrupt) == 0,
                $"a committed Medusa control with a full final egress admits no partial 10167/10166/10171 batch, disconnects after the status gate, and releases its cast reservation (hp={beforeHp}->{afterHp}, effects={fixture.Mechanics().ActiveEffects.Length}, disconnected={session.IsDisconnected}, gate-checks={gateChecks}, gates-free={everyGateCheckWasFree}, pending={MedusaHasPendingCast(handler)}, opcodes={string.Join(',', opcodes)})");
        }
        finally
        {
            AfterMedusaStatusSelfAdmissionHook.SetValue(
                fixture.Registry,
                null);
            BeforeMedusaInterruptHook.SetValue(
                fixture.Registry,
                null);
            ExactStatusDisconnectHook.SetValue(
                fixture.Registry,
                null);
            if (blocked is not null)
            {
                await ObserveExpectedExactFailureAsync(blocked);
            }
            if (queued is not null)
            {
                await ObserveExpectedExactFailureAsync(queued);
            }
            UnregisterMedusaCastInterruption(handler);
            await StopMedusaPendingCastsAsync(handler);
            fixture.Registry.Remove(session);
        }
    }

    private static async Task<MonsterRuntimeSnapshot>
        ReacquireMedusaSourceAsync(
            MonsterPlayerHitFixture fixture,
            ClientSession session)
    {
        var advanceAt = DateTimeOffset.UtcNow.AddMinutes(1);
        _ = fixture.Runtime.Owner.Invoke(
            map => map.AdvanceMonsters(
                advanceAt,
                memberSession => fixture.Registry
                    .GetPlayerLifeRevision(memberSession)),
            TimeSpan.FromSeconds(3));
        var initial = FindMonster(
            fixture.Map,
            fixture.RosterSpawnId);
        Check.True(
            fixture.Registry.TryCapturePlayerMonsterTarget(
                session,
                mapId: 200,
                initial.ObjectId,
                out var target,
                out var authority) &&
            fixture.Registry.TryCommitPlayerMonsterDamageGuarded(
                session,
                mapId: 200,
                target.ObjectId,
                target.RuntimeInstanceId,
                fixture.Character.Id,
                target.SpawnGeneration,
                target.HealthRevision,
                authority,
                DateTimeOffset.UtcNow,
                Resolution(
                    CombatDamageChannel.Physical,
                    damage: 1),
                out var aggro) &&
            aggro.DamageResult is { Killed: false },
            "controlled exact-egress target reacquires its Medusa source");

        for (var index = 1; index <= 160; index++)
        {
            _ = fixture.Runtime.Owner.Invoke(
                map => map.AdvanceMonsters(
                    advanceAt.AddMilliseconds(index * 100),
                    memberSession => fixture.Registry
                        .GetPlayerLifeRevision(memberSession)),
                TimeSpan.FromSeconds(3));
            var current = RequiredMonster(
                fixture.Map,
                initial.ObjectId);
            if (current.CombatPhase == MonsterCombatPhase.Attacking)
            {
                return await Task.FromResult(current);
            }
        }

        throw new InvalidOperationException(
            "The controlled exact-egress source did not reacquire attack phase.");
    }

    private static async Task
        CheckDiagnosticExactFailureDoesNotOwnDisconnectAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        _ = await fixture.AttackAsync(
            fixture.CreateAttack(
                fixture.FindEvent(
                    8_575_000,
                    static resolution => resolution.Hit &&
                        resolution.Damage > 0)));
        var state = GetMedusaStatusState(
            fixture.Registry,
            fixture.Socket.Session);
        var gate = GetMedusaStatusGate(
            fixture.Registry,
            fixture.Socket.Session);
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            var physicalCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _ = ExactCompletionObserverMethod.Invoke(
                fixture.Registry,
                [
                    state,
                    fixture.Socket.Session,
                    physicalCompletion.Task,
                    "MedusaDelayedExactFailureCheck"
                ]);
            physicalCompletion.TrySetException(
                new IOException("simulated delayed exact write failure"));
            await Task.Delay(50);
            Check.True(
                !fixture.Socket.Session.IsDisconnected,
                "the diagnostic completion observer never owns logical disconnect while a later status publication owns the gate");
        }
        finally
        {
            gate.Release();
        }
        await Task.Delay(25);
        Check.True(
            !fixture.Socket.Session.IsDisconnected,
            "physical-failure correctness belongs to the egress terminalizer, not a discarded diagnostic observer task");
    }

    private static byte[] MedusaTestPacket(ushort opcode)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
        return packet;
    }

    private static IReadOnlyList<ushort> DecryptMedusaOpcodes(
        byte[] encrypted)
    {
        var clear = (byte[])encrypted.Clone();
        new PacketCipher().Transform(clear);
        var opcodes = new List<ushort>();
        var offset = 0;
        while (offset < clear.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                clear.AsSpan(offset));
            Check.True(
                length >= 4 && offset <= clear.Length - length,
                "controlled exact egress contains complete packet frames");
            opcodes.Add(BinaryPrimitives.ReadUInt16LittleEndian(
                clear.AsSpan(offset + 2)));
            offset += length;
        }
        return opcodes;
    }

    private static async Task ObserveExpectedExactFailureAsync(
        Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }

#endif
}
