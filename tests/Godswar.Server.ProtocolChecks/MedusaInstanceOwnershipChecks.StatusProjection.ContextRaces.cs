using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static async Task
        CheckMedusaValueEqualContextRefreshAfterCommitAsync()
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
        var observerContext = fixture.Map.Snapshot().Single(context =>
            ReferenceEquals(context.Session, observerSocket.Session));
        var handler = CreateMedusaHandler(
            fixture.Socket.Session,
            fixture.Registry,
            fixture.Character,
            new MedusaHandlerStore(fixture.Character));
        await PrepareMedusaMonsterVisibilityAsync(
            fixture.Registry,
            fixture.Socket.Session,
            fixture.Character);
        RegisterMedusaCastInterruption(handler);
        await DrainMedusaPacketsAsync(fixture.Socket);
        await DrainMedusaPacketsAsync(observerSocket);
        await InvokeMedusaPacketAsync(
            handler,
            MedusaSkillPacket(fixture.Character, fixture.Source));
        Check.True(
            MedusaPacketOpcode(
                await fixture.Socket.ReadPacketAsync()) ==
                    Opcodes.SkillCast &&
            MedusaPacketOpcode(
                await observerSocket.ReadPacketAsync()) ==
                    Opcodes.SkillCast,
            "value-equal refresh fixture publishes the cast start first");

        var contextsAdvancedWithinMembership = false;
        var admissionContextsAdvanced = false;
        var finalAdmissionContextsAdvanced = false;
        var noOpContextsPreserved = false;
        AfterMedusaTransactionHook.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                AfterMedusaTransactionHook.SetValue(
                    fixture.Registry,
                    null);
                fixture.Registry.UpdateCharacter(
                    fixture.Socket.Session,
                    fixture.Character,
                    advanceWorldRevision: false);
                fixture.Registry.UpdateCharacter(
                    observerSocket.Session,
                    observer,
                    advanceWorldRevision: false);
                var afterNoOp = fixture.Map.Snapshot();
                noOpContextsPreserved =
                    ReferenceEquals(
                        afterNoOp.Single(context => ReferenceEquals(
                            context.Session,
                            fixture.Socket.Session)),
                        fixture.Context) &&
                    ReferenceEquals(
                        afterNoOp.Single(context => ReferenceEquals(
                            context.Session,
                            observerSocket.Session)),
                        observerContext);
                fixture.Registry.UpdateCharacter(
                    fixture.Socket.Session,
                    fixture.Character,
                    advanceWorldRevision: true);
                fixture.Registry.UpdateCharacter(
                    observerSocket.Session,
                    observer,
                    advanceWorldRevision: true);
                var current = fixture.Map.Snapshot();
                var currentTarget = current.Single(context =>
                    ReferenceEquals(
                        context.Session,
                        fixture.Socket.Session));
                var currentObserver = current.Single(context =>
                    ReferenceEquals(
                        context.Session,
                        observerSocket.Session));
                contextsAdvancedWithinMembership =
                    !ReferenceEquals(currentTarget, fixture.Context) &&
                    !ReferenceEquals(currentObserver, observerContext) &&
                    currentTarget.WorldRevision ==
                        fixture.Context.WorldRevision + 1 &&
                    currentObserver.WorldRevision ==
                        observerContext.WorldRevision + 1 &&
                    currentTarget.WorldMembershipEpoch ==
                        fixture.Context.WorldMembershipEpoch &&
                    currentObserver.WorldMembershipEpoch ==
                        observerContext.WorldMembershipEpoch;
                fixture.Context = currentTarget;
            }));
        AfterMedusaStatusCaptureHook.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                AfterMedusaStatusCaptureHook.SetValue(
                    fixture.Registry,
                    null);
                var before = fixture.Map.Snapshot();
                var beforeTarget = before.Single(context =>
                    ReferenceEquals(
                        context.Session,
                        fixture.Socket.Session));
                var beforeObserver = before.Single(context =>
                    ReferenceEquals(
                        context.Session,
                        observerSocket.Session));
                fixture.Registry.UpdateCharacter(
                    fixture.Socket.Session,
                    fixture.Character,
                    advanceWorldRevision: true);
                fixture.Registry.UpdateCharacter(
                    observerSocket.Session,
                    observer,
                    advanceWorldRevision: true);
                var after = fixture.Map.Snapshot();
                var afterTarget = after.Single(context =>
                    ReferenceEquals(
                        context.Session,
                        fixture.Socket.Session));
                var afterObserver = after.Single(context =>
                    ReferenceEquals(
                        context.Session,
                        observerSocket.Session));
                admissionContextsAdvanced =
                    afterTarget.WorldRevision ==
                        beforeTarget.WorldRevision + 1 &&
                    afterObserver.WorldRevision ==
                        beforeObserver.WorldRevision + 1 &&
                    afterTarget.WorldMembershipEpoch ==
                        beforeTarget.WorldMembershipEpoch &&
                    afterObserver.WorldMembershipEpoch ==
                        beforeObserver.WorldMembershipEpoch;
                fixture.Context = afterTarget;
            }));
        BeforeMedusaInterruptHook.SetValue(
            fixture.Registry,
            (Action)(() =>
            {
                BeforeMedusaInterruptHook.SetValue(
                    fixture.Registry,
                    null);
                AfterMedusaStatusCaptureHook.SetValue(
                    fixture.Registry,
                    (Action)(() =>
                    {
                        AfterMedusaStatusCaptureHook.SetValue(
                            fixture.Registry,
                            null);
                        var before = fixture.Map.Snapshot();
                        var beforeTarget = before.Single(context =>
                            ReferenceEquals(
                                context.Session,
                                fixture.Socket.Session));
                        var beforeObserver = before.Single(context =>
                            ReferenceEquals(
                                context.Session,
                                observerSocket.Session));
                        fixture.Registry.UpdateCharacter(
                            fixture.Socket.Session,
                            fixture.Character,
                            advanceWorldRevision: true);
                        fixture.Registry.UpdateCharacter(
                            observerSocket.Session,
                            observer,
                            advanceWorldRevision: true);
                        var after = fixture.Map.Snapshot();
                        var afterTarget = after.Single(context =>
                            ReferenceEquals(
                                context.Session,
                                fixture.Socket.Session));
                        var afterObserver = after.Single(context =>
                            ReferenceEquals(
                                context.Session,
                                observerSocket.Session));
                        finalAdmissionContextsAdvanced =
                            afterTarget.WorldRevision ==
                                beforeTarget.WorldRevision + 1 &&
                            afterObserver.WorldRevision ==
                                beforeObserver.WorldRevision + 1 &&
                            afterTarget.WorldMembershipEpoch ==
                                beforeTarget.WorldMembershipEpoch &&
                            afterObserver.WorldMembershipEpoch ==
                                beforeObserver.WorldMembershipEpoch;
                        fixture.Context = afterTarget;
                    }));
            }));

        try
        {
            var eventId = fixture.FindEvent(
                8_050_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            _ = await fixture.AttackAsync(
                fixture.CreateAttack(eventId));
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
                    ClientStatusAggregate.Empty));
            _ = await ReadMedusaInterruptedSequenceAsync(
                observerSocket,
                localTarget: false,
                fixture.Source.ObjectId,
                fixture.Context.ObjectId,
                expectedSkillId: 2002,
                expectedStatusId: 330,
                expectedDuration: 2,
                expectedLocalGameData: null);

            var firstSequence = fixture.Mechanics()
                .ActiveEffects.Single().ApplicationSequence;
            var secondEvent = fixture.FindEvent(
                8_060_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            var second = await fixture.AttackAsync(
                fixture.CreateAttack(secondEvent));
            var refreshed = fixture.Mechanics()
                .ActiveEffects.Single();
            Check.True(
                contextsAdvancedWithinMembership &&
                admissionContextsAdvanced &&
                finalAdmissionContextsAdvanced &&
                noOpContextsPreserved &&
                second.AfterHealth < second.BeforeHealth &&
                second.AfterVitalsRevision ==
                    second.BeforeVitalsRevision + 1 &&
                refreshed.ApplicationSequence > firstSequence &&
                !fixture.Socket.Session.IsDisconnected &&
                !observerSocket.Session.IsDisconnected &&
                !MedusaHasPendingCast(handler),
                "routine target and observer revisions retain membership lineage and rebase the full impact/damage/status/interrupt sequence");
        }
        finally
        {
            AfterMedusaTransactionHook.SetValue(
                fixture.Registry,
                null);
            AfterMedusaStatusCaptureHook.SetValue(
                fixture.Registry,
                null);
            BeforeMedusaInterruptHook.SetValue(
                fixture.Registry,
                null);
            UnregisterMedusaCastInterruption(handler);
            await StopMedusaPendingCastsAsync(handler);
            fixture.Registry.Remove(observerSocket.Session);
        }
    }
#endif
}
