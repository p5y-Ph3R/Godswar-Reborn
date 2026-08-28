using Godswar.Server.Application.Characters;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaEncounterMechanicsRuntimeChecks
{
    private static void CheckPureActiveCharacterViews()
    {
        var runtime = CreateRuntime();
        var ownership = new PlayerOwnershipFence(
            new Guid("179da95c-f2f4-440c-80bc-1f8ded33b539"),
            Generation: 4);
        var replacement = new PlayerOwnershipFence(
            new Guid("9f36f7fd-378a-4b2d-ab23-ef47e843c62d"),
            Generation: 5);
        var start = runtime.StartedAt;
        var stun = Source(runtime, "E1-Elite");
        var pikeman = Source(runtime, "Final-Pikeman-1");
        _ = runtime.CommitMonsterHit(
            101,
            ownership,
            targetLifeRevision: 7,
            stun.ObjectId,
            stun.SpawnGeneration,
            start.AddSeconds(1));
        _ = runtime.CommitMonsterHit(
            101,
            ownership,
            targetLifeRevision: 7,
            pikeman.ObjectId,
            pikeman.SpawnGeneration,
            start.AddSeconds(2));
        var before = runtime.Snapshot();

        Check.True(
            runtime.TryGetActiveCharacterEffectView(
                101,
                ownership,
                targetLifeRevision: 7,
                start.AddSeconds(2),
                out var active) &&
            active.EffectTarget == new MedusaEncounterEffectTarget(
                ownership,
                LifeRevision: 7,
                WorldMembershipEpoch: 1) &&
            active.ActiveEffects.Length == 2 &&
            active.ControlRestriction ==
                MedusaEncounterControlRestriction.AllActions &&
            (active.ControlRestriction &
             MedusaEncounterControlRestriction.ItemUse) != 0 &&
            active.PhysicalOutgoingDamageMultiplier == 10 &&
            active.MagicalOutgoingDamageMultiplier == 1,
            "an exact ownership/life view aggregates active controls and amplifiers");
        Check.True(
            runtime.TryGetActiveCharacterEffectView(
                101,
                replacement,
                targetLifeRevision: 7,
                start.AddSeconds(2),
                out var staleOwnership) &&
            staleOwnership.ActiveEffects.IsEmpty &&
            staleOwnership.ControlRestriction ==
                MedusaEncounterControlRestriction.None &&
            runtime.TryGetActiveCharacterEffectView(
                101,
                ownership,
                targetLifeRevision: 8,
                start.AddSeconds(2),
                out var staleLife) &&
            staleLife.ActiveEffects.IsEmpty &&
            staleLife.PhysicalOutgoingDamageMultiplier == 1,
            "stale ownership and life authorities receive valid empty views");
        Check.True(
            runtime.TryGetActiveCharacterEffectView(
                101,
                ownership,
                targetLifeRevision: 7,
                start.AddSeconds(3),
                out var stunExpired) &&
            stunExpired.ControlRestriction ==
                MedusaEncounterControlRestriction.None &&
            stunExpired.ActiveEffects.Single().Definition.Kind ==
                MedusaEncounterEffectKind.OutgoingPhysicalAmplifier &&
            runtime.TryGetActiveCharacterEffectView(
                101,
                ownership,
                targetLifeRevision: 7,
                start.AddSeconds(32),
                out var allExpired) &&
            allExpired.ActiveEffects.IsEmpty &&
            !runtime.TryGetActiveCharacterEffectView(
                999,
                ownership,
                targetLifeRevision: 7,
                start.AddSeconds(32),
                out _),
            "effect expiration is exclusive and unknown characters do not resolve");

        var after = runtime.Snapshot();
        Check.True(
            after.LastObservedAt == before.LastObservedAt &&
            Character(after, 101).ActiveEffects.Length == 2,
            "active character views never advance clocks or mutate retained effects");
    }
}
