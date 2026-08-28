using Godswar.Server.Application.Characters;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static async Task CheckMonsterPlayerHitTransactionAsync()
    {
        CheckProductionMechanicsCallsRequireEpoch();
        await CheckCurrentMonsterPlayerHitAndReplayAsync();
        await CheckControlStatusProcCanMissAsync();
        await CheckMonsterPlayerHitAuthorityRejectionsAsync();
        await CheckMonsterPlayerRollbackAndZeroAsync();
        await CheckMonsterPlayerBleedAndLethalAsync();
        await CheckMonsterPlayerStaleReconnectFenceAsync();
        await CheckWorldEmittedAttackEpochFenceAsync();
        await CheckTwoMonsterSnapshotHitsAsync();
        await CheckCommittedRideCompletionAfterLethalAsync();
        await CheckMonsterAttackPublicationTargetFenceAsync();
#if DEBUG
        await CheckAcceptedOwnerCommitBlocksEgressAsync();
        await CheckCaptureCommitVitalsRaceAsync();
        await CheckMonsterPlayerMissingLifeAuthorityAsync();
        await CheckLethalInvariantRecoveryAsync();
#endif
    }

    private static async Task CheckControlStatusProcCanMissAsync()
    {
        await CheckControlStatusProcCanMissAsync(
            "E1-Elite",
            MedusaEncounterEffectKind.Stun);
        await CheckControlStatusProcCanMissAsync(
            "E5-Elite",
            MedusaEncounterEffectKind.Freeze);
        await CheckControlStatusProcCanMissAsync(
            "Euryale",
            MedusaEncounterEffectKind.Shackle);
    }

    internal static Task RunControlStatusProcChecksAsync() =>
        CheckControlStatusProcCanMissAsync();

    private static async Task CheckControlStatusProcCanMissAsync(
        string rosterSpawnId,
        MedusaEncounterEffectKind expectedKind)
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync(rosterSpawnId);
        ulong missedEventId = 0;
        for (ulong candidate = 10_000;
             candidate < 110_000;
             candidate++)
        {
            var resolution = fixture.Resolve(candidate);
            if (resolution.Hit &&
                resolution.Damage > 0 &&
                !fixture.AuthoredEffectProcApplies(candidate))
            {
                missedEventId = candidate;
                break;
            }
        }
        Check.True(
            missedEventId != 0,
            $"Medusa {expectedKind} has a deterministic miss candidate");

        var expectedMissDamage = fixture.Resolve(missedEventId).Damage;
        var missed = await fixture.AttackAsync(
            fixture.CreateAttack(missedEventId));
        Check.True(
            missed.BeforeHealth - missed.AfterHealth ==
                expectedMissDamage &&
            fixture.Mechanics().ActiveEffects.Length == 0,
            $"a missed Medusa {expectedKind} proc keeps committed direct damage but applies no control status");

        var appliedEventId = FindAppliedControlEvent(
            fixture,
            missedEventId + 1);
        _ = await fixture.AttackAsync(
            fixture.CreateAttack(appliedEventId));
        Check.True(
            fixture.Mechanics().ActiveEffects.Single().Definition.Kind ==
                expectedKind,
            $"a successful later Medusa {expectedKind} proc applies the authored control status");
    }

    private static async Task CheckCurrentMonsterPlayerHitAndReplayAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale");
        var eventId = FindAppliedControlEvent(fixture, start: 100);
        var expected = fixture.Resolve(eventId);
        var attack = fixture.CreateAttack(eventId);
        var applied = await fixture.AttackAsync(attack);
        var mechanics = fixture.Mechanics();

        Check.True(
            applied.BeforeHealth - applied.AfterHealth ==
                expected.Damage &&
            applied.AfterVitalsRevision ==
                applied.BeforeVitalsRevision + 1 &&
            applied.LifeRevision == 0 &&
            mechanics.EffectTarget == new MedusaEncounterEffectTarget(
                fixture.Ownership,
                LifeRevision: 0,
                fixture.Context.WorldMembershipEpoch) &&
            mechanics.ActiveEffects.Single().Definition.Kind ==
                MedusaEncounterEffectKind.Shackle,
            "real registry/ECS Euryale hit commits explicit ATK, HP, and one life-fenced shackle");

        var beforeReplay = fixture.MechanicsSnapshot();
        var replay = await fixture.AttackAsync(attack);
        Check.True(
            replay.BeforeHealth == replay.AfterHealth &&
            replay.BeforeVitalsRevision == replay.AfterVitalsRevision &&
            MechanicsSnapshotsValueEqual(
                beforeReplay,
                replay.Mechanics),
            "real ECS replay is rejected before HP and cannot replace the finalized owner effect");
    }

    private static ulong FindAppliedControlEvent(
        MonsterPlayerHitFixture fixture,
        ulong start)
    {
        for (var candidate = start;
             candidate < start + 100_000;
             candidate++)
        {
            var resolution = fixture.Resolve(candidate);
            if (resolution.Hit &&
                resolution.Damage > 0 &&
                fixture.AuthoredEffectProcApplies(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "No deterministic applied control-status event was found.");
    }

    private static async Task CheckMonsterPlayerHitAuthorityRejectionsAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("Euryale");
        var eventId = fixture.FindEvent(
            start: 500,
            static resolution => resolution.Hit &&
                resolution.Damage > 0);
        var before = fixture.MechanicsSnapshot();

        var staleOwnership = await fixture.AttackAsync(
            fixture.CreateAttack(
                eventId,
                ownership: new PlayerOwnershipFence(
                    Guid.NewGuid(),
                    Generation: 1)));
        var staleWorld = await fixture.AttackAsync(
            fixture.CreateAttack(
                eventId + 1,
                worldRevision:
                    fixture.Map.Descriptor.Revision - 1));
        var staleLife = await fixture.AttackAsync(
            fixture.CreateAttack(
                eventId + 2,
                targetLifeRevision: 1));
        var forgedDefinition = fixture.Source with
        {
            Definition = fixture.Source.Definition with
            {
                MapId = 204
            }
        };
        var forgedSource = await fixture.AttackAsync(
            fixture.CreateAttack(
                eventId + 3,
                source: forgedDefinition));

        Check.True(
            staleOwnership.BeforeHealth == staleOwnership.AfterHealth &&
            staleWorld.BeforeHealth == staleWorld.AfterHealth &&
            staleLife.BeforeHealth == staleLife.AfterHealth &&
            forgedSource.BeforeHealth == forgedSource.AfterHealth &&
            MechanicsSnapshotsValueEqual(
                before,
                forgedSource.Mechanics),
            "ownership, world revision, target life, and full captured-source definition fences reject before HP/effects");
    }
}
