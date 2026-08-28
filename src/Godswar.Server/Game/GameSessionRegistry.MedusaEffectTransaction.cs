using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly record struct CapturedMedusaMonsterHitTransaction(
        MedusaMonsterPlayerHitCommit Commit,
        Task DeathInterruptionTask,
        RegistryMedusaCapturedEffectInterruption? EffectInterruption);

    private CapturedMedusaMonsterHitTransaction
        CommitCapturedMedusaMonsterPlayerHit(
            WorldInstanceRuntime runtime,
            GameSessionContext targetContext,
            in MedusaMonsterPlayerHitCapture capture,
            in PlayerMonsterDamageEcsRequest request,
            CancellationToken cancellationToken)
    {
        var source = capture.SourceAuthority!.Value;
        var target = capture.TargetAuthority;
        var deathInterruptionTask = Task.CompletedTask;
        var capturedDeathInterruption = CaptureDeathInterruption(
            targetContext.Session,
            cancellationToken);
        var vitalsCommit = CapturePlayerVitalsDamageEcsCommit(
            targetContext.Session,
            targetContext.Character,
            targetContext.ObjectId,
            target.LifeRevision,
            request,
            beforeLethalCommit: () =>
            {
                deathInterruptionTask = capturedDeathInterruption();
            });
        var effectInterruption = CaptureMedusaEffectInterruption(
            runtime,
            targetContext,
            source,
            target,
            capture.AuthoredEffectKind);

        var commit = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map =>
            {
                InvokeProtocolCheckBeforeMedusaOwnerCommit();
                return map.CommitMedusaMonsterPlayerHitForSessionGuarded(
                    targetContext.Session,
                    targetContext.Character,
                    source,
                    target,
                    vitalsCommit,
                    effectInterruption);
            });
        return new(
            commit,
            deathInterruptionTask,
            effectInterruption);
    }

    private RegistryMedusaCapturedEffectInterruption?
        CaptureMedusaEffectInterruption(
            WorldInstanceRuntime runtime,
            GameSessionContext targetContext,
            in MedusaMonsterPlayerSourceAuthority source,
            in MedusaMonsterPlayerTargetAuthority target,
            MedusaEncounterEffectKind? expectedEffectKind)
    {
        if (expectedEffectKind is null or
            MedusaEncounterEffectKind.Bleed ||
            !TryResolveMedusaAuthoredSkillBinding(
                targetContext.MapId,
                source,
                expectedEffectKind.Value,
                out var binding) ||
            PlayerSkillCastControlCatalog.ResolveAppliedInterruption(
                binding.StatusId) is not { } reason ||
            !_preparedSkillCastInterruptionSinks.TryGetValue(
                targetContext.Session,
                out var prepare))
        {
            return null;
        }

        var prepared = prepare(reason);
        if (prepared is null)
        {
            return null;
        }

        var recipients = CaptureMonsterAttackPublicationRecipients(
            runtime,
            SnapshotMonsterAttackMembers(runtime));
        return new(
            this,
            runtime,
            targetContext,
            recipients,
            targetContext.Session,
            targetContext.Character,
            source,
            target,
            expectedEffectKind.Value,
            prepared);
    }

    private static bool TryResolveMedusaAuthoredSkillBinding(
        byte mapId,
        in MedusaMonsterPlayerSourceAuthority source,
        MedusaEncounterEffectKind expectedEffectKind,
        out MedusaIslandRosterSkillBinding binding) =>
        TryResolveMedusaAuthoredSkillBinding(
            mapId,
            source.RosterSpawnId,
            expectedEffectKind,
            out binding);

    private static bool TryResolveMedusaAuthoredSkillBinding(
        byte mapId,
        string rosterSpawnId,
        MedusaEncounterEffectKind expectedEffectKind,
        out MedusaIslandRosterSkillBinding binding)
    {
        if (MedusaIslandRosterPolicy.TryGetSpawn(
                rosterSpawnId,
                out var spawn) &&
            spawn.Skill is { } authored &&
            MedusaEncounterMechanicsPolicy.TryGetEffectDefinition(
                authored.Mechanic,
                mapId,
                out var definition) &&
            definition.Kind == expectedEffectKind &&
            authored.SkillId > 0 &&
            authored.StatusId ==
                definition.ClientProjection.EmittableStatusId)
        {
            binding = authored;
            return true;
        }

        binding = default;
        return false;
    }
}
