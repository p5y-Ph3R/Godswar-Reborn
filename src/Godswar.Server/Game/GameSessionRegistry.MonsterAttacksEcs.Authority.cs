using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly record struct MonsterAttackEcsAuthorityCapture(
        GameSessionContext? TargetContext,
        PlayerRecoveryDeadline? RecoveryDeadline,
        RuntimeIncomingDamageMitigation RuntimeMitigation,
        MedusaMonsterPlayerHitCapture MedusaCapture,
        MedusaRunTerminalClearWorkItem? TerminalClear,
        MedusaMonsterPlayerTargetAuthority TargetAuthority,
        bool AuthorityRejected);

    /// <summary>
    /// Captures the complete current target/life/Medusa authority while the
    /// registry gate is held. It deliberately performs no player HP mutation.
    /// </summary>
    private MonsterAttackEcsAuthorityCapture
        CaptureMonsterAttackEcsAuthorityLocked(
            WorldInstanceRuntime runtime,
            MonsterRuntimeUpdate attack,
            IReadOnlyList<GameSessionContext> members,
            GameSessionContext? statusContext,
            int targetCharacterId,
            DateTimeOffset damageResolvedAt,
            RuntimeIncomingDamageMitigation runtimeMitigation,
            MonsterCombatProfile monsterProfile,
            ulong combatEventId)
    {
        var targetContext = ResolveCurrentMonsterAttackTarget(
            runtime,
            members,
            targetCharacterId,
            attack);
        if (targetContext is null)
        {
            return default;
        }

        if (!_nextPlayerRecoveryAt.TryGetValue(
                targetContext.CharacterId,
                out var recoveryDeadline))
        {
            // Joining preallocates this cell. A missing deadline means the
            // target is no longer a complete live-life authority.
            return new(
                targetContext,
                RecoveryDeadline: null,
                runtimeMitigation,
                MedusaCapture: default,
                TerminalClear: null,
                TargetAuthority: default,
                AuthorityRejected: true);
        }

        if (statusContext is null ||
            !ReferenceEquals(
                statusContext.Session,
                targetContext.Session))
        {
            runtimeMitigation = default;
        }

        if (!_playerLifeRevisions.TryGetValue(
                targetContext.Session,
                out var currentLifeRevision))
        {
            return new(
                targetContext,
                recoveryDeadline,
                runtimeMitigation,
                MedusaCapture: default,
                TerminalClear: null,
                TargetAuthority: default,
                AuthorityRejected: true);
        }

        MedusaRunTerminalClearWorkItem? terminalClear = null;
        if (runtime.Map.HasBoundMedusaEncounter())
        {
            if (!TryPrepareMedusaRunTerminalClearLocked(
                    runtime.InstanceId,
                    out var preparedTerminalClear))
            {
                return new(
                    targetContext,
                    recoveryDeadline,
                    runtimeMitigation,
                    MedusaCapture: default,
                    TerminalClear: null,
                    TargetAuthority: default,
                    AuthorityRejected: true);
            }
            terminalClear = preparedTerminalClear;
        }

        long capturedVitalsRevision;
        lock (targetContext.Character.VitalsSync)
        {
            capturedVitalsRevision =
                targetContext.Character.VitalsRevision;
        }

        var route = new PlayerMonsterCombatAuthority(
            targetContext.WorldInstanceId,
            targetContext.WorldRevision,
            targetContext.Ownership,
            currentLifeRevision,
            targetContext.WorldMembershipEpoch);
        var emittedTargetAuthority =
            new MedusaMonsterPlayerTargetAuthority(
                attack.TargetWorldInstanceId ?? default,
                attack.TargetWorldRevision ?? -1,
                attack.TargetOwnership ?? default,
                targetContext.CharacterId,
                attack.TargetObjectId ?? 0,
                attack.TargetLifeRevision ?? -1,
                capturedVitalsRevision,
                attack.TargetWorldMembershipEpoch ?? 0);
        var medusaCapture = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map => map
                .CaptureMedusaMonsterPlayerHitForSessionGuarded(
                    targetContext.Session,
                    targetContext.Character,
                    attack.Monster,
                    combatEventId,
                    route,
                    emittedTargetAuthority,
                    damageResolvedAt,
                    monsterProfile));
        if (medusaCapture.IsBound && !medusaCapture.IsCaptured)
        {
            return new(
                targetContext,
                recoveryDeadline,
                runtimeMitigation,
                medusaCapture,
                terminalClear,
                TargetAuthority: default,
                AuthorityRejected: true);
        }

        var targetAuthority = medusaCapture.IsCaptured
            ? medusaCapture.TargetAuthority
            : new MedusaMonsterPlayerTargetAuthority(
                targetContext.WorldInstanceId,
                targetContext.WorldRevision,
                targetContext.Ownership,
                targetContext.CharacterId,
                attack.TargetObjectId ?? targetContext.ObjectId,
                attack.TargetLifeRevision ?? currentLifeRevision,
                attack.TargetVitalsRevision ?? capturedVitalsRevision,
                targetContext.WorldMembershipEpoch);
        return new(
            targetContext,
            recoveryDeadline,
            runtimeMitigation,
            medusaCapture,
            terminalClear,
            targetAuthority,
            AuthorityRejected: false);
    }
}
