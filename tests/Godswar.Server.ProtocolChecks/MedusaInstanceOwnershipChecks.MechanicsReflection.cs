using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    /// <summary>
    /// Owner-invariant tests reflect only to invoke the real private owner
    /// transaction. They do not reproduce its clock or mechanic sequencing.
    /// </summary>
    private static bool TryCommitOwnerMechanicForInvariantTest(
        this MapInstance map,
        int targetCharacterId,
        uint sourceObjectId,
        uint sourceSpawnGeneration,
        DateTimeOffset committedAt,
        out MedusaOwnedMechanicHitResult result)
    {
        var owner = typeof(MapInstance).GetField(
                "_medusaInstanceOwner",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(map);
        if (owner is null)
        {
            result = default;
            return false;
        }

        var target = map.Snapshot().SingleOrDefault(context =>
            context.CharacterId == targetCharacterId);
        var ownership = target?.Ownership ??
            MedusaEncounterMechanicsRuntime.CompatibilityOwnership;
        var epoch = target?.WorldMembershipEpoch ?? 1;
        var ownerType = owner.GetType();
        var reserve = ownerType.GetMethod(
                "ReserveMonsterPlayerEffect",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The production Medusa owner reservation is unavailable.");
        var prepared = reserve.Invoke(
                owner,
                [
                    targetCharacterId,
                    ownership,
                    0L,
                    epoch,
                    sourceObjectId,
                    sourceSpawnGeneration,
                    committedAt,
                    true
                ])
            ?? throw new InvalidOperationException(
                "The production Medusa owner returned no reservation result.");
        var preparedType = prepared.GetType();
        var outcome = (MedusaMonsterPlayerHitCaptureOutcome)(
            preparedType.GetProperty("Outcome")?.GetValue(prepared) ??
            throw new InvalidOperationException(
                "The production reservation omitted its outcome."));

        if (outcome != MedusaMonsterPlayerHitCaptureOutcome.Captured)
        {
            if ((outcome is
                    MedusaMonsterPlayerHitCaptureOutcome
                        .PeriodicDamageHandoffUnavailable or
                    MedusaMonsterPlayerHitCaptureOutcome
                        .DeadlineBoundaryUnresolved or
                    MedusaMonsterPlayerHitCaptureOutcome.TimedOut) &&
                map.TryObserveMedusaTime(committedAt, out var observed))
            {
                result = new(
                    observed.GateOutcome,
                    observed.RunOutcome,
                    observed.MechanicsResult,
                    outcome == MedusaMonsterPlayerHitCaptureOutcome
                        .PeriodicDamageHandoffUnavailable
                        ? new(
                            MedusaMechanicHitOutcome
                                .PeriodicDamageRequired,
                            Effect: null,
                            observed.MechanicsResult?.PeriodicDamage)
                        : null);
                return true;
            }

            result = new(
                GateForCapture(outcome),
                RunClockForCapture(outcome),
                MechanicsClockResult: null,
                outcome ==
                    MedusaMonsterPlayerHitCaptureOutcome.RunNotActive
                    ? null
                    : new(
                        MechanicsOutcomeForCapture(outcome),
                        Effect: null,
                        PeriodicDamage: null));
            return true;
        }

        var effectKind = (MedusaEncounterEffectKind?)preparedType
            .GetProperty("EffectKind")?.GetValue(prepared);
        if (effectKind is null)
        {
            result = new(
                MedusaOwnedOperationGateOutcome.Delegated,
                MedusaRunClockOutcome.Active,
                new(
                    MedusaMechanicsClockOutcome.Advanced,
                    PeriodicDamage: null),
                new(
                    MedusaMechanicHitOutcome
                        .MonsterHasNoAuthoredMechanic,
                    Effect: null,
                    PeriodicDamage: null));
            return true;
        }

        var finalize = ownerType.GetMethod(
                "FinalizeMonsterPlayerEffect",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The production Medusa owner finalizer is unavailable.");
        var mechanics = (MedusaMechanicHitResult)(finalize.Invoke(
                owner,
                [prepared]) ??
            throw new InvalidOperationException(
                "The production Medusa owner finalizer returned no result."));
        result = new(
            MedusaOwnedOperationGateOutcome.Delegated,
            MedusaRunClockOutcome.Active,
            new(
                MedusaMechanicsClockOutcome.Advanced,
                PeriodicDamage: null),
            mechanics);
        return true;
    }

    private static MedusaOwnedOperationGateOutcome GateForCapture(
        MedusaMonsterPlayerHitCaptureOutcome outcome) => outcome switch
        {
            MedusaMonsterPlayerHitCaptureOutcome.TimestampMovedBackward =>
                MedusaOwnedOperationGateOutcome.TimestampMovedBackward,
            MedusaMonsterPlayerHitCaptureOutcome
                .DeadlineBoundaryUnresolved =>
                MedusaOwnedOperationGateOutcome
                    .DeadlineBoundaryUnresolved,
            MedusaMonsterPlayerHitCaptureOutcome.TimedOut =>
                MedusaOwnedOperationGateOutcome.TimedOut,
            _ => MedusaOwnedOperationGateOutcome.RunNotActive
        };

    private static MedusaRunClockOutcome? RunClockForCapture(
        MedusaMonsterPlayerHitCaptureOutcome outcome) => outcome switch
        {
            MedusaMonsterPlayerHitCaptureOutcome.TimestampMovedBackward =>
                MedusaRunClockOutcome.TimestampMovedBackward,
            MedusaMonsterPlayerHitCaptureOutcome
                .DeadlineBoundaryUnresolved =>
                MedusaRunClockOutcome.DeadlineBoundaryUnresolved,
            MedusaMonsterPlayerHitCaptureOutcome.TimedOut =>
                MedusaRunClockOutcome.TimedOut,
            MedusaMonsterPlayerHitCaptureOutcome.RunNotActive =>
                MedusaRunClockOutcome.RunNotActive,
            _ => null
        };

    private static MedusaMechanicHitOutcome MechanicsOutcomeForCapture(
        MedusaMonsterPlayerHitCaptureOutcome outcome) => outcome switch
        {
            MedusaMonsterPlayerHitCaptureOutcome.CharacterNotAdmitted =>
                MedusaMechanicHitOutcome.CharacterNotAdmitted,
            MedusaMonsterPlayerHitCaptureOutcome.UnknownMonster =>
                MedusaMechanicHitOutcome.UnknownMonster,
            MedusaMonsterPlayerHitCaptureOutcome.StaleMonsterGeneration =>
                MedusaMechanicHitOutcome.StaleMonsterGeneration,
            MedusaMonsterPlayerHitCaptureOutcome.MonsterNotAttackable =>
                MedusaMechanicHitOutcome.MonsterRetired,
            MedusaMonsterPlayerHitCaptureOutcome.TimestampMovedBackward =>
                MedusaMechanicHitOutcome.TimestampMovedBackward,
            _ => MedusaMechanicHitOutcome.MonsterHasNoAuthoredMechanic
        };

    private static MedusaOwnedDefeatResult InvokeOwnerDefeatForInvariantTest(
        MapInstance map,
        int characterId,
        uint objectId,
        uint spawnGeneration,
        DateTimeOffset occurredAt)
    {
        var owner = RequiredOwner(map);
        var method = owner.GetType().GetMethod(
                "ClaimDefeat",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "The production Medusa owner defeat operation is unavailable.");
        return (MedusaOwnedDefeatResult)(method.Invoke(
                owner,
                [characterId, objectId, spawnGeneration, occurredAt]) ??
            throw new InvalidOperationException(
                "The production Medusa owner returned no defeat result."));
    }

    private static MedusaEncounterMechanicsRuntime
        RequiredOwnerMechanicsForInvariantTest(MapInstance map)
    {
        var owner = RequiredOwner(map);
        return owner.GetType().GetField(
                "_mechanics",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(owner) as MedusaEncounterMechanicsRuntime ??
            throw new InvalidOperationException(
                "The production Medusa owner mechanics are unavailable.");
    }

    private static object RequiredOwner(MapInstance map) =>
        typeof(MapInstance).GetField(
                "_medusaInstanceOwner",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(map) ??
        throw new InvalidOperationException(
            "The production Medusa owner is unavailable.");
}
