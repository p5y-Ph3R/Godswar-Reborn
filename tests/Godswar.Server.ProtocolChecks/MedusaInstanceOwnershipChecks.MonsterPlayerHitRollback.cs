using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckMonsterPlayerRollbackAndZeroAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale");
        var missEvent = fixture.FindEvent(
            start: 1_000,
            static resolution => !resolution.Hit &&
                resolution.Damage == 0);
        var attack = fixture.CreateAttack(missEvent);
        var before = fixture.MechanicsSnapshot();
        var acceptedZero = await fixture.AttackAsync(attack);
        var afterAcceptedZero = fixture.MechanicsSnapshot();
        var replay = await fixture.AttackAsync(attack);
        Check.True(
            fixture.Map.TryGetMedusaOwnershipSnapshot(out var owner),
            "accepted-zero fixture exposes coupled owner clocks");

        Check.True(
            acceptedZero.BeforeHealth == acceptedZero.AfterHealth &&
            acceptedZero.BeforeVitalsRevision ==
                acceptedZero.AfterVitalsRevision &&
            replay.BeforeHealth == replay.AfterHealth &&
            replay.BeforeVitalsRevision == replay.AfterVitalsRevision &&
            fixture.Mechanics().ActiveEffects.IsEmpty &&
            afterAcceptedZero.LastObservedAt > before.LastObservedAt &&
            owner.Run.LastObservedAt ==
                afterAcceptedZero.LastObservedAt &&
            MechanicsSnapshotsValueEqual(
                afterAcceptedZero,
                replay.Mechanics),
            "real ECS zero-resolution hit advances coupled clocks once, publishes no effect, and is final in both replay ledgers");
    }

    private static async Task CheckMonsterPlayerBleedAndLethalAsync()
    {
        await using (var bleed =
                     await MonsterPlayerHitFixture.CreateAsync("Chrysaor"))
        {
            var eventId = bleed.FindEvent(
                start: 2_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            var expected = bleed.Resolve(eventId);
            var applied = await bleed.AttackAsync(
                bleed.CreateAttack(eventId));
            var effect = bleed.Mechanics().ActiveEffects.Single();
            Check.True(
                applied.BeforeHealth - applied.AfterHealth ==
                    expected.Damage &&
                effect.Definition.Kind ==
                    MedusaEncounterEffectKind.Bleed &&
                effect.TargetOwnership == bleed.Ownership &&
                effect.TargetLifeRevision == 0 &&
                effect.TargetWorldMembershipEpoch ==
                    bleed.Context.WorldMembershipEpoch &&
                effect.SourceRosterSpawnId == "Chrysaor" &&
                effect.SourceObjectId == bleed.Source.ObjectId &&
                effect.SourceSpawnGeneration ==
                    bleed.Source.SpawnGeneration &&
                effect.NextPeriodicTickAt ==
                    effect.AppliedAt.AddSeconds(2) &&
                effect.EmittedPeriodicTicks == 0,
                "real Chrysaor damage commits one exact target/source Bleed application with its first tick reserved for the live periodic phase");
        }

        await using (var amplifier =
                     await MonsterPlayerHitFixture.CreateAsync(
                         "Final-Pikeman-1"))
        {
            var eventId = amplifier.FindEvent(
                start: 3_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            var before = DateTimeOffset.UtcNow;
            _ = await amplifier.AttackAsync(
                amplifier.CreateAttack(eventId));
            var effect = amplifier.Mechanics().ActiveEffects.Single();
            Check.True(
                effect.Definition.Kind ==
                    MedusaEncounterEffectKind
                        .OutgoingPhysicalAmplifier &&
                effect.TargetOwnership == amplifier.Ownership &&
                effect.TargetLifeRevision == 0 &&
                effect.ExpiresAt >= before.AddSeconds(29) &&
                effect.ExpiresAt <=
                    DateTimeOffset.UtcNow.AddSeconds(31),
                "real Pikeman hit records one exact-life 30-second physical amplifier");
        }

        await using (var lethal =
                     await MonsterPlayerHitFixture.CreateAsync("Euryale"))
        {
            var firstEvent = lethal.FindEvent(
                start: 4_000,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            _ = await lethal.AttackAsync(
                lethal.CreateAttack(firstEvent));
            Check.True(
                lethal.Mechanics().ActiveEffects.Single().Definition.Kind ==
                    MedusaEncounterEffectKind.Shackle,
                "lethal fixture records its old-life effect first");

            lethal.SetHealth(1);
            var lethalEvent = lethal.FindEvent(
                firstEvent + 1,
                static resolution => resolution.Hit &&
                    resolution.Damage > 0);
            var killed = await lethal.AttackAsync(
                lethal.CreateAttack(lethalEvent));
            Check.True(
                killed.AfterHealth == 0 &&
                killed.LifeRevision == 1 &&
                lethal.Mechanics().EffectTarget is null &&
                lethal.Mechanics().ActiveEffects.IsEmpty,
                "real lethal ECS hit advances life once and clears every effect from the dead ownership/life");
        }
    }
}
