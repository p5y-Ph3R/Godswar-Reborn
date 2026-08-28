using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static readonly FieldInfo MedusaLocalAggregateCaptureHook =
        RequiredPrivateField(
            typeof(GameSessionRegistry),
            "_protocolCheckAfterMedusaLocalAggregateCapture");

    private static async Task
        CheckMedusaLocalAggregateFenceRecomposesAsync()
    {
        await using var fixture =
            await MedusaPendingCastFixture.CreateAsync("E1-Elite");
        var hookCalls = 0;
        var galeApplied = false;
        MedusaLocalAggregateCaptureHook.SetValue(
            fixture.Hit.Registry,
            (Action)(() =>
            {
                if (Interlocked.Increment(ref hookCalls) != 1)
                {
                    return;
                }

                var target = fixture.Hit.Character;
                var appliedAt = DateTimeOffset.UtcNow
                    .ToUnixTimeMilliseconds();
                const ulong elementalEventId = 9_950_001;
                var sourceCharacterId = checked(target.Id + 900_000);
                var combatEvent = new DeterministicCombatEventContext(
                    elementalEventId,
                    target.CurrentMap,
                    sourceCharacterId,
                    target.Id,
                    appliedAt,
                    CombatEventProvenance.ElementalStatus,
                    Committed: true,
                    IsPvp: false,
                    default);
                var gale = new ElementalEffectApplication(
                    ElementKind.Wind,
                    ElementalEffectKind.Gale,
                    sourceCharacterId,
                    target.Id,
                    elementalEventId,
                    appliedAt,
                    checked(appliedAt + 10_000),
                    EffectivePotencyBasisPoints: 1_000,
                    ApplicationChanceBasisPoints: 10_000,
                    TargetResistanceBasisPoints: 0,
                    PeriodicDamageTotal: 0,
                    PeriodicTickCount: 0,
                    CombatEventProvenance.ElementalStatus);
                galeApplied = fixture.Hit.Registry
                    .TryApplyElementalApplication(
                        fixture.Hit.Socket.Session,
                        new ElementalCombatSessionFence(
                            target.Id,
                            target.CurrentMap,
                            fixture.Hit.Context.Ownership),
                        combatEvent,
                        gale);
            }));

        try
        {
            _ = await fixture.Hit.AttackAsync(
                fixture.Hit.CreateAttack(
                    fixture.Hit.FindEvent(
                        9_950_100,
                        static resolution => resolution.Hit &&
                            resolution.Damage > 0)));
            var projected = fixture.Hit.Registry
                .ProjectElementalMovementStatus(
                    fixture.Hit.Socket.Session,
                    fixture.Hit.Character,
                    fixture.Hit.Context.Ownership,
                    ClientStatusAggregate.Empty,
                    DateTimeOffset.UtcNow);
            _ = await ReadMedusaInterruptedSequenceAsync(
                fixture.Hit.Socket,
                localTarget: true,
                fixture.Hit.Source.ObjectId,
                MedusaHandlerLocalObjectId,
                expectedSkillId: 2002,
                expectedStatusId: 330,
                expectedDuration: 2,
                PacketBuilder.PlayerStatusUpdate(
                    fixture.Hit.Character,
                    projected));
            Check.True(
                galeApplied &&
                hookCalls >= 2 &&
                projected.MovementSpeedMultiplier >
                    ClientStatusAggregate.Empty
                        .MovementSpeedMultiplier &&
                !fixture.Hit.Socket.Session.IsDisconnected &&
                !MedusaHasPendingCast(fixture.Handler),
                "a movement mutation after 10166 construction rejects the stale batch, recomposes current GameData, and admits one exact interrupt");
        }
        finally
        {
            MedusaLocalAggregateCaptureHook.SetValue(
                fixture.Hit.Registry,
                null);
        }
    }
#endif
}
