using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckWorldEmittedAttackEpochFenceAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite");
        var tick = fixture.Runtime.Owner.Invoke(
            map => map.AdvanceMonsters(
                DateTimeOffset.UtcNow.AddMinutes(1),
                session => fixture.Registry.TryGetPlayerLifeRevision(
                    session,
                    out var life)
                    ? life
                    : null),
            TimeSpan.FromSeconds(3));
        var emitted = tick.Updates.Single(update =>
            update.Kind == MonsterRuntimeUpdateKind.Attacked &&
            update.Monster.ObjectId == fixture.Source.ObjectId &&
            update.TargetCharacterId == fixture.Character.Id);
        var oldEpoch = fixture.Context.WorldMembershipEpoch;
        var eventId = fixture.FindEvent(
            start: 55_000,
            static resolution => resolution.Hit &&
                resolution.Damage > 0);
        emitted = emitted with { AttackEventId = eventId };
        var beforeHealth = fixture.Character.CurrentHp;
        var beforeVitals = fixture.Character.VitalsRevision;
        var originalContext = fixture.Context;
        var originalSource = fixture.Source;
        var originalLife = fixture.Registry.GetPlayerLifeRevision(
            fixture.Socket.Session);

        await fixture.RejoinSameAuthorityAsync();
        var replaced = fixture.Context;
        var beforeReplay = fixture.Registry
            .GetPlayerVitalsDamageEcsDiagnostics(
                fixture.Socket.Session);
        var beforePackets = fixture.Socket.Available;
        var beforeMechanics = fixture.MechanicsSnapshot();
        await fixture.Registry.ProcessMonsterAttackForSessionAsync(
            fixture.Socket.Session,
            emitted,
            CancellationToken.None);

        Check.True(
            emitted.Monster == originalSource &&
            emitted.TargetCharacterId == originalContext.CharacterId &&
            emitted.TargetObjectId == originalContext.ObjectId &&
            emitted.TargetOwnership == originalContext.Ownership &&
            emitted.TargetWorldInstanceId ==
                originalContext.WorldInstanceId &&
            emitted.TargetWorldRevision ==
                originalContext.WorldRevision &&
            emitted.TargetLifeRevision == originalLife &&
            emitted.TargetWorldMembershipEpoch == oldEpoch &&
            replaced.WorldMembershipEpoch != oldEpoch &&
            (replaced with { WorldMembershipEpoch = oldEpoch }) ==
                originalContext &&
            fixture.Source == originalSource &&
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session) == originalLife &&
            fixture.Character.CurrentHp == beforeHealth &&
            fixture.Character.VitalsRevision == beforeVitals &&
            fixture.Mechanics().ActiveEffects.IsEmpty &&
            Equals(
                beforeReplay,
                fixture.Registry.GetPlayerVitalsDamageEcsDiagnostics(
                    fixture.Socket.Session)) &&
            fixture.Socket.Available == beforePackets &&
            MechanicsSnapshotsValueEqual(
                beforeMechanics,
                fixture.MechanicsSnapshot()),
            "a real world-simulated attack retains its exact emitted tuple " +
            "and cannot cross a same-instance membership replacement");
    }
}
