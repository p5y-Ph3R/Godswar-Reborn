using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaEncounterMechanicsRuntimeChecks
{
    public const string CheckName =
        "Medusa committed-hit encounter mechanics runtime";

    public static Task RunAsync()
    {
        CheckAuthoredDefinitionsAndProjection();
        CheckHitPreviewIsPure();
        CheckSourceAuthorization();
        CheckControlRefreshAndIsolation();
        CheckBleedCadenceAndRefresh();
        CheckPeriodicReservationDispositions();
        CheckPeriodicMutatorBarriers();
        CheckPeriodicDeadlineBoundaries();
        CheckOutgoingAmplifiers();
        CheckPureActiveCharacterViews();
        CheckEffectLifeRevisionFences();
        CheckMonotonicFailureBoundaries();
        CheckSourceRetirement();
        CheckConstructionBoundary();
        return Task.CompletedTask;
    }

    private static void CheckEffectLifeRevisionFences()
    {
        var runtime = CreateRuntime();
        var source = Source(runtime, "Final-Pikeman-1");
        var ownership = new PlayerOwnershipFence(
            new Guid("44b78ed7-ea64-4b1e-92fc-55e81df8ab17"),
            Generation: 7);
        var replacementOwnership = new PlayerOwnershipFence(
            new Guid("f213555c-f141-40bd-bbf1-e10d2c0c6941"),
            Generation: 8);
        var hit = runtime.CommitMonsterHit(
            targetCharacterId: 101,
            targetOwnership: ownership,
            targetLifeRevision: 7,
            source.ObjectId,
            source.SpawnGeneration,
            runtime.StartedAt.AddSeconds(1));
        var physical = Resolution(
            Godswar.Server.World.Systems.Combat.CombatDamageChannel.Physical,
            Godswar.Server.World.Systems.Combat.CombatHitOutcome.Normal,
            damage: 100);
        var sameLife = runtime.PreviewOutgoingDamage(
            101,
            ownership,
            attackingLifeRevision: 7,
            physical);
        var revivedLife = runtime.PreviewOutgoingDamage(
            101,
            ownership,
            attackingLifeRevision: 8,
            physical);
        var reconnectedAlias = runtime.PreviewOutgoingDamage(
            101,
            replacementOwnership,
            attackingLifeRevision: 7,
            physical);
        var retained = Character(runtime.Snapshot(), 101);

        Check.True(
            hit.Effect?.TargetLifeRevision == 7 &&
            hit.Effect?.TargetOwnership == ownership &&
            retained.EffectTarget == new MedusaEncounterEffectTarget(
                ownership,
                LifeRevision: 7,
                WorldMembershipEpoch: 1) &&
            retained.ActiveEffects.Single().TargetLifeRevision == 7 &&
            sameLife.AppliedMultiplier == 10 &&
            revivedLife.AppliedMultiplier == 1 &&
            reconnectedAlias.AppliedMultiplier == 1,
            "published effects and outgoing amplifiers are ownership-and-life fenced");

        runtime.ClearCharacterLife(
            101,
            ownership,
            targetLifeRevision: 7);
        var cleared = Character(runtime.Snapshot(), 101);
        Check.True(
            cleared.EffectTarget is null &&
            cleared.ActiveEffects.IsEmpty &&
            runtime.PreviewOutgoingDamage(
                    101,
                    ownership,
                    attackingLifeRevision: 7,
                    physical)
                .AppliedMultiplier == 1,
            "lethal-life cleanup removes every effect from only that life");

        _ = runtime.CommitMonsterHit(
            targetCharacterId: 101,
            targetOwnership: replacementOwnership,
            targetLifeRevision: 9,
            source.ObjectId,
            source.SpawnGeneration,
            runtime.StartedAt.AddSeconds(2));
        Check.True(
            runtime.PreviewOutgoingDamage(
                    101,
                    replacementOwnership,
                    attackingLifeRevision: 9,
                    physical)
                .AppliedMultiplier == 10,
            "a new ownership receives only effects authored for that ownership and life");
    }

    private static void CheckHitPreviewIsPure()
    {
        var runtime = CreateRuntime();
        var source = Source(runtime, "E1-Elite");
        var before = runtime.Snapshot();
        var preview = runtime.PreviewMonsterHit(
            101,
            source.ObjectId,
            source.SpawnGeneration,
            runtime.StartedAt.AddSeconds(10));
        var after = runtime.Snapshot();

        Check.True(
            preview == MedusaMechanicHitOutcome.Applied &&
            before.LastObservedAt == after.LastObservedAt &&
            before.WorldInstanceId == after.WorldInstanceId &&
            after.Characters.Single().ActiveEffects.IsEmpty,
            "hit preview validates without advancing or applying state");
    }

    private static void CheckAuthoredDefinitionsAndProjection()
    {
        foreach (var (mapId, nativeSceneId) in new[]
                 {
                     (MapId: (short)200, NativeSceneId: (short)209),
                     (MapId: (short)204, NativeSceneId: (short)223)
                 })
        {
            var stun = Definition(
                MedusaIslandRosterMechanic.Stun,
                mapId);
            var freeze = Definition(
                MedusaIslandRosterMechanic.Freeze,
                mapId);
            var bleed = Definition(
                MedusaIslandRosterMechanic.Bleed,
                mapId);
            var shackle = Definition(
                MedusaIslandRosterMechanic.Shackle,
                mapId);
            var physical = Definition(
                MedusaIslandRosterMechanic.OutgoingPhysicalAmplifier,
                mapId);
            var magical = Definition(
                MedusaIslandRosterMechanic.OutgoingMagicalAmplifier,
                mapId);

            Check.True(
                stun.Duration == TimeSpan.FromSeconds(2) &&
                freeze.Duration == TimeSpan.FromSeconds(3) &&
                shackle.Duration == TimeSpan.FromSeconds(3) &&
                stun.ControlRestriction ==
                    MedusaEncounterControlRestriction.AllActions &&
                freeze.ControlRestriction ==
                    MedusaEncounterControlRestriction.AllActions &&
                shackle.ControlRestriction ==
                    MedusaEncounterControlRestriction.AllActions,
                $"map {mapId} retains stock control windows");
            Check.True(
                bleed.Duration == TimeSpan.FromSeconds(15) &&
                bleed.Bleed is
                {
                    DamageKind: MedusaPeriodicDamageKind.DirectHealthLoss,
                    DamagePerTick: 200,
                    TickInterval.Ticks: var intervalTicks,
                    MaximumTicks: 7,
                    TicksImmediately: false,
                    TicksAtExpiration: false
                } &&
                intervalTicks == TimeSpan.FromSeconds(2).Ticks,
                $"map {mapId} authors status-18 numeric tick evidence");
            CheckAmplifierDefinition(
                physical,
                MedusaDamageChannel.Physical,
                statusId: 236,
                nativeSceneId);
            CheckAmplifierDefinition(
                magical,
                MedusaDamageChannel.Magical,
                statusId: 235,
                nativeSceneId);
            Check.True(
                new[] { stun, freeze, bleed, shackle, physical, magical }
                    .All(static definition =>
                        definition.IsServerAuthoritative &&
                        !definition.UsesNativeStatusOddsAsProbability),
                "all effects are server authoritative and ignore StatusOdds probability");
        }

        Check.True(
            !MedusaEncounterMechanicsPolicy.TryGetEffectDefinition(
                MedusaIslandRosterMechanic.Stun,
                201,
                out _) &&
            !MedusaEncounterMechanicsPolicy.TryGetEffectDefinition(
                (MedusaIslandRosterMechanic)byte.MaxValue,
                200,
                out _),
            "unknown maps and mechanics fail closed");
    }

    private static void CheckSourceAuthorization()
    {
        var runtime = CreateRuntime();
        var start = runtime.StartedAt;
        CheckHitKind(runtime, "E1-Elite", 101, start.AddSeconds(1),
            MedusaEncounterEffectKind.Stun);
        CheckHitKind(runtime, "E5-Elite", 101, start.AddSeconds(2),
            MedusaEncounterEffectKind.Freeze);
        CheckHitKind(runtime, "E9-Elite", 101, start.AddSeconds(3),
            MedusaEncounterEffectKind.Bleed);
        CheckHitKind(runtime, "Euryale", 101, start.AddSeconds(4),
            MedusaEncounterEffectKind.Shackle);
        CheckHitKind(runtime, "Chrysaor", 101, start.AddSeconds(5),
            MedusaEncounterEffectKind.Bleed);
        CheckHitKind(runtime, "Final-Pikeman-1", 101,
            start.AddSeconds(6),
            MedusaEncounterEffectKind.OutgoingPhysicalAmplifier);
        CheckHitKind(runtime, "Final-Axeman-1", 101,
            start.AddSeconds(7),
            MedusaEncounterEffectKind.OutgoingMagicalAmplifier);

        DrainPeriodicDamage(runtime, start.AddSeconds(30));
        var noMechanic = Source(runtime, "E13-Elite");
        var before = runtime.Snapshot();
        var rejected = runtime.CommitMonsterHit(
            101,
            noMechanic.ObjectId,
            noMechanic.SpawnGeneration,
            start.AddSeconds(30));
        Check.True(
            rejected.Outcome ==
                MedusaMechanicHitOutcome.MonsterHasNoAuthoredMechanic &&
            runtime.Snapshot().LastObservedAt == before.LastObservedAt,
            "a roster monster without a mechanic cannot advance the clock");
    }

    private static void CheckControlRefreshAndIsolation()
    {
        var runtime = CreateRuntime([101, 102]);
        var stunOne = Source(runtime, "E1-Elite");
        var stunTwo = Source(runtime, "E2-Elite");
        var start = runtime.StartedAt;

        var applied = Hit(runtime, stunOne, 101, start.AddSeconds(1));
        var historical = runtime.Snapshot();
        var refreshed = Hit(runtime, stunTwo, 101, start.AddSeconds(2));
        var current = runtime.Snapshot();

        Check.True(
            applied.Outcome == MedusaMechanicHitOutcome.Applied &&
            applied.Effect?.ExpiresAt == start.AddSeconds(3) &&
            refreshed.Outcome == MedusaMechanicHitOutcome.Refreshed &&
            refreshed.Effect?.ExpiresAt == start.AddSeconds(4) &&
            refreshed.Effect?.ApplicationSequence >
                applied.Effect?.ApplicationSequence,
            "each committed control hit refreshes from the new hit time");
        Check.True(
            Character(current, 101).ControlRestriction ==
                MedusaEncounterControlRestriction.AllActions &&
            Character(current, 102).ControlRestriction ==
                MedusaEncounterControlRestriction.None,
            "control state is isolated per admitted character");
        Check.True(
            Character(historical, 101).ActiveEffects.Single().ExpiresAt ==
                start.AddSeconds(3),
            "an earlier snapshot remains immutable after refresh");

        var expired = runtime.ObserveTime(start.AddSeconds(4));
        Check.True(
            expired.Outcome == MedusaMechanicsClockOutcome.Advanced &&
            Character(runtime.Snapshot(), 101).ActiveEffects.IsEmpty,
            "control expires at the exact exclusive window boundary");
    }

    private static void CheckConstructionBoundary()
    {
        var abandoned = MedusaRunRuntimeCheckFixture.Create();
        _ = abandoned.AbandonRun(
            101,
            abandoned.StartedAt.AddSeconds(1));
        Check.Throws<ArgumentException>(
            () => _ = new MedusaEncounterMechanicsRuntime(abandoned),
            "terminal runs cannot acquire mechanics state");

        var progressed = MedusaRunRuntimeCheckFixture.Create();
        var first = progressed.Snapshot().Spawns[0];
        _ = progressed.ClaimDefeat(
            101,
            first.ObjectId,
            first.SpawnGeneration,
            progressed.StartedAt.AddSeconds(1));
        Check.Throws<ArgumentException>(
            () => _ = new MedusaEncounterMechanicsRuntime(progressed),
            "mechanics must bind before the first roster defeat");
    }

    private static MedusaEncounterEffectDefinition Definition(
        MedusaIslandRosterMechanic mechanic,
        short mapId)
    {
        Check.True(
            MedusaEncounterMechanicsPolicy.TryGetEffectDefinition(
                mechanic,
                mapId,
                out var definition),
            $"{mechanic} resolves on map {mapId}");
        return definition;
    }

    private static void CheckAmplifierDefinition(
        MedusaEncounterEffectDefinition definition,
        MedusaDamageChannel channel,
        uint statusId,
        short nativeSceneId)
    {
        Check.True(
            definition.Duration == TimeSpan.FromSeconds(30) &&
            definition.OutgoingDamageChannel == channel &&
            definition.OutgoingDamageMultiplier == 10 &&
            definition.ClientProjection.Mode ==
                MedusaEncounterClientProjectionMode
                    .NativeProjectionSupported &&
            definition.ClientProjection.NativeReferenceStatusId == statusId &&
            definition.ClientProjection.EmittableStatusId == statusId &&
            definition.ClientProjection.MatchedNativeClientSceneId ==
                nativeSceneId &&
            definition.ClientProjection.NativeAffectedClientSceneIds
                .SequenceEqual([(short)209, (short)223]) &&
            definition.ClientProjection.MayEmitNativeReferenceStatus &&
            !definition.ClientProjection.RequiresCompatibilityDecision,
            $"status {statusId} projects through Medusa scene {nativeSceneId}");
    }

    private static void CheckHitKind(
        MedusaEncounterMechanicsRuntime runtime,
        string rosterSpawnId,
        int characterId,
        DateTimeOffset at,
        MedusaEncounterEffectKind expected)
    {
        var source = Source(runtime, rosterSpawnId);
        var result = Hit(runtime, source, characterId, at);
        while (result.Outcome ==
               MedusaMechanicHitOutcome.PeriodicDamageRequired)
        {
            _ = runtime.CompletePeriodicDamageApplied(
                result.PeriodicDamage);
            result = Hit(runtime, source, characterId, at);
        }
        Check.True(
            result.Outcome is MedusaMechanicHitOutcome.Applied or
                MedusaMechanicHitOutcome.Refreshed &&
            result.Effect?.Definition.Kind == expected,
            $"{rosterSpawnId} owns only {expected}");
    }

    private static void DrainPeriodicDamage(
        MedusaEncounterMechanicsRuntime runtime,
        DateTimeOffset observedAt)
    {
        while (runtime.ObserveTime(observedAt).PeriodicDamage is
               { } periodic)
        {
            _ = runtime.CompletePeriodicDamageApplied(periodic);
        }
    }

    private static MedusaMechanicHitResult Hit(
        MedusaEncounterMechanicsRuntime runtime,
        MedusaRunSpawnSnapshot source,
        int characterId,
        DateTimeOffset at) => runtime.CommitMonsterHit(
        characterId,
        source.ObjectId,
        source.SpawnGeneration,
        at);

    private static MedusaEncounterMechanicsRuntime CreateRuntime(
        IReadOnlyCollection<int>? characters = null) => new(
        MedusaRunRuntimeCheckFixture.Create(
            characters: characters ?? [101]));

    private static MedusaRunSpawnSnapshot Source(
        MedusaEncounterMechanicsRuntime runtime,
        string rosterSpawnId)
    {
        var run = MedusaRunRuntimeCheckFixture.Create(
            runtime.Difficulty,
            characters: runtime.Snapshot().Characters
                .Select(static character => character.CharacterId)
                .ToArray());
        return run.Snapshot().Spawns.Single(spawn =>
            spawn.RosterSpawnId == rosterSpawnId);
    }

    private static MedusaEncounterCharacterMechanicsSnapshot Character(
        MedusaEncounterMechanicsSnapshot snapshot,
        int characterId) => snapshot.Characters.Single(character =>
        character.CharacterId == characterId);
}
