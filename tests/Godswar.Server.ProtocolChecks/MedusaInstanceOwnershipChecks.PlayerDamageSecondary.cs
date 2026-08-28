using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static void CheckCurrentBoundSecondaryDamageFences(
        GameSessionRegistry registry,
        WorldInstanceRuntime sourceRuntime,
        GameSessionContext source,
        MonsterRuntimeSnapshot target)
    {
        var before = RequiredMonster(sourceRuntime.Map, target.ObjectId);
        var (rebound, reflection) = InvokeSecondaries(
            registry,
            sourceRuntime,
            source,
            target,
            eventId: 0x5EC0_DA11);
        var after = RequiredMonster(sourceRuntime.Map, target.ObjectId);
        Check.True(
            !rebound.Claimed &&
            !rebound.Applied &&
            reflection == PveElementalCommitResult.Empty &&
            SameMonsterHealth(before, after) &&
            PrivateLedgerClaimCount(
                registry,
                "_pveMonsterReboundLedger") == 0 &&
            PrivateLedgerClaimCount(
                registry,
                "_monsterIncomingAttackReplay") == 0,
            "bound Medusa rebound and Gaia reflection reach the raw Map fence without HP or replay divergence");
    }

    private static void CheckStaleSecondaryDamageFences(
        GameSessionRegistry registry,
        WorldInstanceRuntime sourceRuntime,
        WorldInstanceRuntime destinationRuntime,
        GameSessionContext staleSource,
        MonsterRuntimeSnapshot sourceTarget,
        MonsterRuntimeSnapshot destinationTarget)
    {
        var sourceBefore = RequiredMonster(
            sourceRuntime.Map,
            sourceTarget.ObjectId);
        var destinationBefore = RequiredMonster(
            destinationRuntime.Map,
            destinationTarget.ObjectId);
        var (rebound, reflection) = InvokeSecondaries(
            registry,
            sourceRuntime,
            staleSource,
            sourceTarget,
            eventId: 0x5EC0_DA12);
        var sourceAfter = RequiredMonster(
            sourceRuntime.Map,
            sourceTarget.ObjectId);
        var destinationAfter = RequiredMonster(
            destinationRuntime.Map,
            destinationTarget.ObjectId);

        Check.True(
            !rebound.Claimed &&
            !rebound.Applied &&
            reflection == PveElementalCommitResult.Empty &&
            SameMonsterHealth(sourceBefore, sourceAfter) &&
            SameMonsterHealth(destinationBefore, destinationAfter) &&
            PrivateLedgerClaimCount(
                registry,
                "_pveMonsterReboundLedger") == 0 &&
            PrivateLedgerClaimCount(
                registry,
                "_monsterIncomingAttackReplay") == 0,
            "stale rebound and Gaia reflection neither reroute to a colliding map-200 target nor consume replay claims");
    }

    private static (
        PveMonsterReboundCommit Rebound,
        PveElementalCommitResult Reflection) InvokeSecondaries(
        GameSessionRegistry registry,
        WorldInstanceRuntime sourceRuntime,
        GameSessionContext source,
        MonsterRuntimeSnapshot target,
        ulong eventId)
    {
        var rebound = InvokePrivate<PveMonsterReboundCommit>(
            registry,
            "CommitMonsterRebound",
            sourceRuntime,
            source,
            target,
            eventId,
            10u,
            5u);
        var reflection = InvokePrivate<PveElementalCommitResult>(
            registry,
            "CommitMonsterIncomingElementalReflection",
            sourceRuntime,
            source,
            target,
            (ResonanceDamageIntent?)new(
                ResonanceDamageKind.GaiaReflection,
                source.CharacterId,
                target.ObjectId,
                eventId,
                Damage: 5,
                CombatEventProvenance.Reflection));
        return (rebound, reflection);
    }

    private static TResult InvokePrivate<TResult>(
        object target,
        string methodName,
        params object?[] arguments)
    {
        var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                $"Private method {methodName} was not found.");
        return method.Invoke(target, arguments) is TResult result
            ? result
            : throw new InvalidOperationException(
                $"Private method {methodName} returned no result.");
    }

    private static int PrivateLedgerClaimCount(
        GameSessionRegistry registry,
        string fieldName)
    {
        var ledger = typeof(GameSessionRegistry).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(registry) ??
            throw new InvalidOperationException(
                $"Replay ledger {fieldName} was not found.");
        var claimed = ledger.GetType().GetField(
                "_claimed",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(ledger) ??
            throw new InvalidOperationException(
                $"Replay ledger {fieldName} has no claim set.");
        return (int)(claimed.GetType().GetProperty("Count")
                ?.GetValue(claimed) ??
            throw new InvalidOperationException(
                $"Replay ledger {fieldName} has no count."));
    }
}
