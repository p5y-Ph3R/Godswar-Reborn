using Godswar.Server.Game;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static async Task
        CheckMedusaNativePrefixFaultsFailClosedAsync()
    {
        await CheckMedusaSelfPrefixFaultAsync("SelfImpact");
        await CheckMedusaSelfPrefixFaultAsync("SelfDamage");
        await CheckMedusaObserverPrefixFaultAsync("WorldImpact");
        await CheckMedusaObserverPrefixFaultAsync("WorldDamage");
    }

    private static async Task CheckMedusaSelfPrefixFaultAsync(
        string faultStage)
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        MedusaNativePrefixHook.SetValue(
            fixture.Hit.Registry,
            (Action<string>)(stage =>
            {
                if (string.Equals(
                        stage,
                        faultStage,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "simulated Medusa native prefix fault");
                }
            }));

        try
        {
            _ = await fixture.Hit.AttackAsync(
                fixture.Hit.CreateAttack(
                    fixture.Hit.FindEvent(
                        faultStage == "SelfImpact"
                            ? 8_081_000UL
                            : 8_082_000UL,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            var packets = await ReadAvailableMedusaPacketsAsync(
                fixture.Hit.Socket);
            Check.True(
                fixture.Hit.Socket.Session.IsDisconnected &&
                !MedusaHasPendingCast(fixture.Handler) &&
                packets.All(packet =>
                    MedusaPacketOpcode(packet) is not
                        MedusaStatusOpcode and not
                        Godswar.Server.Protocol.Opcodes
                            .SkillCastInterrupt),
                $"{faultStage} construction failure exact-claims the committed target, admits no status/interrupt suffix, and releases the cast generation");
        }
        finally
        {
            MedusaNativePrefixHook.SetValue(
                fixture.Hit.Registry,
                null);
        }
    }

    private static async Task CheckMedusaObserverPrefixFaultAsync(
        string faultStage)
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
        await DrainMedusaPacketsAsync(observerSocket);
        await InvokeMedusaPacketAsync(
            handler,
            MedusaSkillPacket(fixture.Character, fixture.Source));
        _ = await fixture.Socket.ReadPacketAsync();
        _ = await observerSocket.ReadPacketAsync();
        MedusaNativePrefixHook.SetValue(
            fixture.Registry,
            (Action<string>)(stage =>
            {
                if (string.Equals(
                        stage,
                        faultStage,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "simulated observer prefix fault");
                }
            }));

        try
        {
            _ = await fixture.AttackAsync(
                fixture.CreateAttack(
                    fixture.FindEvent(
                        faultStage == "WorldImpact"
                            ? 8_083_000UL
                            : 8_084_000UL,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            _ = await ReadMedusaInterruptedSequenceAsync(
                fixture.Socket,
                localTarget: true,
                fixture.Source.ObjectId,
                MedusaHandlerLocalObjectId,
                expectedSkillId: 2002,
                expectedStatusId: 330,
                expectedDuration: 2,
                PacketBuilder.PlayerStatusUpdate(
                    fixture.Character,
                    Godswar.Server.State.ClientStatusAggregate.Empty));
            Check.True(
                !fixture.Socket.Session.IsDisconnected &&
                observerSocket.Session.IsDisconnected &&
                !MedusaHasPendingCast(handler),
                $"{faultStage} construction failure exact-pair-claims only the captured observer while self retains the complete prefix/status/interrupt sequence");
        }
        finally
        {
            MedusaNativePrefixHook.SetValue(
                fixture.Registry,
                null);
            UnregisterMedusaCastInterruption(handler);
            await StopMedusaPendingCastsAsync(handler);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }

    private static async Task
        CheckPostClaimInvariantPrefixFaultFailsClosedAsync()
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        MedusaFinalizeEffectFaultField.SetValue(fixture.Hit.Map, 1);
        MedusaNativePrefixHook.SetValue(
            fixture.Hit.Registry,
            (Action<string>)(stage =>
            {
                if (stage == "SelfImpact")
                {
                    throw new InvalidOperationException(
                        "simulated invariant prefix fault");
                }
            }));

        try
        {
            _ = await fixture.Hit.AttackAsync(
                fixture.Hit.CreateAttack(
                    fixture.Hit.FindEvent(
                        8_085_000,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            var packets = await ReadAvailableMedusaPacketsAsync(
                fixture.Hit.Socket);
            Check.True(
                fixture.Hit.Socket.Session.IsDisconnected &&
                !MedusaHasPendingCast(fixture.Handler) &&
                packets.All(packet =>
                    MedusaPacketOpcode(packet) !=
                        Godswar.Server.Protocol.Opcodes
                            .SkillCastInterrupt),
                "a post-claim mechanics invariant still requires the native prefix; its construction fault disconnects without a forged 10171 and releases the reservation");
        }
        finally
        {
            MedusaNativePrefixHook.SetValue(
                fixture.Hit.Registry,
                null);
            MedusaFinalizeEffectFaultField.SetValue(
                fixture.Hit.Map,
                0);
        }
    }

    private static async Task
        CheckMedusaNativePrefixMembershipTransitionsAsync()
    {
        await CheckMedusaSelfPrefixMembershipTransitionAsync();
        await CheckMedusaObserverPrefixMembershipTransitionAsync();
    }

    private static async Task
        CheckMedusaSelfPrefixMembershipTransitionAsync()
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        var oldEpoch = fixture.Hit.Context.WorldMembershipEpoch;
        var replaced = false;
        MedusaNativePrefixHook.SetValue(
            fixture.Hit.Registry,
            (Action<string>)(stage =>
            {
                if (stage != "SelfDamage")
                {
                    return;
                }
                MedusaNativePrefixHook.SetValue(
                    fixture.Hit.Registry,
                    null);
                fixture.Hit.Registry.Remove(fixture.Hit.Socket.Session);
                fixture.Hit.Registry.JoinWorldInstance(
                    fixture.Hit.Socket.Session,
                    fixture.Hit.Character.AccountId,
                    fixture.Hit.Character,
                    fixture.Hit.PlayerObjectId,
                    fixture.Hit.Runtime.InstanceId,
                    worldReady: true,
                    joinedAt: DateTimeOffset.UtcNow);
                fixture.Hit.Context = fixture.Hit.Map.Snapshot().Single(
                    context => ReferenceEquals(
                        context.Session,
                        fixture.Hit.Socket.Session));
                replaced = fixture.Hit.Context.WorldMembershipEpoch !=
                    oldEpoch;
            }));

        try
        {
            _ = await fixture.Hit.AttackAsync(
                fixture.Hit.CreateAttack(
                    fixture.Hit.FindEvent(
                        8_086_000UL,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            var packets = await ReadAvailableMedusaPacketsAsync(
                fixture.Hit.Socket);
            Check.True(
                replaced &&
                !fixture.Hit.Socket.Session.IsDisconnected &&
                !MedusaHasPendingCast(fixture.Handler) &&
                packets.All(packet => MedusaPacketOpcode(packet) is not
                    MedusaImpactOpcode and not
                    MedusaPhysicalDamageOpcode and not
                    MedusaStatusOpcode and not
                    Godswar.Server.Protocol.Opcodes.SkillCastInterrupt),
                "a target remove/rejoin between prefix construction stages admits neither impact nor damage to the new epoch and releases the old cast reservation");
        }
        finally
        {
            MedusaNativePrefixHook.SetValue(
                fixture.Hit.Registry,
                null);
        }
    }

    private static async Task
        CheckMedusaObserverPrefixMembershipTransitionAsync()
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
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            fixture.Socket.Session,
            fixture.Character);
        var oldContext = fixture.Map.Snapshot().Single(context =>
            ReferenceEquals(context.Session, observerSocket.Session));
        var handler = CreateMedusaHandler(
            fixture.Socket.Session,
            fixture.Registry,
            fixture.Character,
            new MedusaHandlerStore(fixture.Character));
        RegisterMedusaCastInterruption(handler);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);
        await InvokeMedusaPacketAsync(
            handler,
            MedusaSkillPacket(fixture.Character, fixture.Source));
        _ = await fixture.Socket.ReadPacketAsync();
        _ = await observerSocket.ReadPacketAsync();
        var replaced = false;
        MedusaNativePrefixHook.SetValue(
            fixture.Registry,
            (Action<string>)(stage =>
            {
                if (stage != "WorldDamage")
                {
                    return;
                }
                MedusaNativePrefixHook.SetValue(fixture.Registry, null);
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
            _ = await fixture.AttackAsync(
                fixture.CreateAttack(
                    fixture.FindEvent(
                        8_087_000UL,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            _ = await ReadMedusaInterruptedSequenceAsync(
                fixture.Socket,
                localTarget: true,
                fixture.Source.ObjectId,
                MedusaHandlerLocalObjectId,
                expectedSkillId: 2002,
                expectedStatusId: 330,
                expectedDuration: 2,
                PacketBuilder.PlayerStatusUpdate(
                    fixture.Character,
                    Godswar.Server.State.ClientStatusAggregate.Empty));
            var observerPackets = await ReadAvailableMedusaPacketsAsync(
                observerSocket);
            Check.True(
                replaced &&
                !fixture.Socket.Session.IsDisconnected &&
                !observerSocket.Session.IsDisconnected &&
                !MedusaHasPendingCast(handler) &&
                observerPackets.All(packet =>
                    MedusaPacketOpcode(packet) is not
                        MedusaImpactOpcode and not
                        MedusaPhysicalDamageOpcode and not
                        Godswar.Server.Protocol.Opcodes.SkillCastInterrupt),
                "an observer remove/rejoin between prefix construction " +
                "stages admits no lone native prefix or old interrupt to " +
                $"the new membership epoch (replaced={replaced}, " +
                $"target-disconnected={fixture.Socket.Session.IsDisconnected}, " +
                $"observer-disconnected={observerSocket.Session.IsDisconnected}, " +
                $"pending={MedusaHasPendingCast(handler)}, opcodes=[" +
                string.Join(",", observerPackets.Select(
                    packet => MedusaPacketOpcode(packet))) + "])");
        }
        finally
        {
            MedusaNativePrefixHook.SetValue(fixture.Registry, null);
            UnregisterMedusaCastInterruption(handler);
            await StopMedusaPendingCastsAsync(handler);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }
#endif
}
