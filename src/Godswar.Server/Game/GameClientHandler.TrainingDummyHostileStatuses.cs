using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool>
        TryHandleTrainingDummyHostileStatusCastAsync(
            GamePacket packet,
            SkillCastRequest cast,
            SkillCombatDefinition skill,
            HostileStatusEffectDefinition definition,
            bool publishCastVisual,
            CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return false;
        }

        var interruptionClaims =
            new List<TrainingDummyHostileInterruptionClaim>();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var decision = await _registry
                .ResolveTrainingDummyHostileStatusCastAsync(
                _session,
                cast.CasterObjectId,
                cast.TargetObjectId,
                skill,
                definition,
                NextAdmittedLegacyCombatRevision,
                now,
                cancellationToken,
                (target, appliedDefinition) =>
                    ClaimTrainingDummyHostileStatusInterruption(
                        target,
                        appliedDefinition,
                        interruptionClaims));
            if (!decision.Handled)
            {
                return false;
            }

            var committed = decision.Accepted ||
                decision.RejectionReason ==
                    TrainingDummySkillRejectionReason.
                        PartialCommitFailure &&
                decision.Attacker is not null &&
                decision.Targets.Count > 0;
            if (!committed || decision.Attacker is not { } attacker)
            {
                Console.WriteLine(
                    "[training-status] rejected " +
                    $"character={character.Name} skill={cast.SkillId} " +
                    $"target={cast.TargetObjectId} " +
                    $"reason={decision.RejectionReason}");
                await PublishTrainingSkillManaRejectionAsync(
                    decision.RejectionReason,
                    decision.CurrentMana,
                    cancellationToken);
                return true;
            }

            var primaryTarget = definition.TargetMode ==
                    HostileStatusTargetMode.SingleTarget
                ? decision.Targets.FirstOrDefault()?.Target
                : null;
            if (publishCastVisual)
            {
                await _registry.PublishTrainingDummyHostileCastVisualAsync(
                    attacker,
                    primaryTarget,
                    packet.Buffer,
                    definition,
                    cancellationToken);
            }

            foreach (var targetDecision in decision.Targets)
            {
                try
                {
                    await _registry
                        .PublishTrainingDummyHostileStatusApplicationAsync(
                            targetDecision.Target,
                            targetDecision.Application,
                            now,
                            $"training-dummy-hostile-skill-" +
                            definition.SkillId,
                            cancellationToken);
                }
                catch (Exception ex) when (
                    ex is not OperationCanceledException)
                {
                    if (ex is IOException or ObjectDisposedException)
                    {
                        _registry.Remove(targetDecision.Target.Session);
                    }
                    else
                    {
                        Console.WriteLine(
                            "[training-status] projection deferred " +
                            $"target={targetDecision.Target.DisplayName} " +
                            $"skill={definition.SkillId}: {ex.Message}");
                    }
                }
            }

            await CompleteTrainingDummyHostileInterruptionClaimsAsync(
                interruptionClaims);
            interruptionClaims.Clear();

            await _registry.PublishTrainingDummyHostileCastImpactAsync(
                attacker,
                primaryTarget,
                packet.Buffer,
                definition,
                cancellationToken);
            await PublishTrainingDummyHostileManaAsync(
                attacker,
                decision.CurrentMana,
                cancellationToken);

            var appliedTargets = decision.Targets.Count(
                static target => target.Application.Applied);
            Console.WriteLine(
                "[training-status] committed " +
                $"character={character.Name} skill={cast.SkillId} " +
                $"targets={decision.Targets.Count} " +
                $"applied={appliedTargets} " +
                $"mp={decision.CurrentMana} partial={!decision.Accepted}");
            return true;
        }
        finally
        {
            await CompleteTrainingDummyHostileInterruptionClaimsAsync(
                interruptionClaims);
        }
    }

    private void ClaimTrainingDummyHostileStatusInterruption(
        GameSessionContext target,
        HostileStatusEffectDefinition definition,
        List<TrainingDummyHostileInterruptionClaim> claims)
    {
        if (PlayerSkillCastControlCatalog.ResolveAppliedInterruption(
                definition.StatusId) is not { } reason)
        {
            return;
        }

        var notificationBarrier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var completion = _registry.RequestSkillCastInterruptionAsync(
                target.Session,
                reason,
                CancellationToken.None,
                notificationBarrier.Task);
            claims.Add(new(
                target.DisplayName,
                definition.SkillId,
                notificationBarrier,
                completion));
        }
        catch
        {
            notificationBarrier.TrySetResult();
            throw;
        }
    }

    private static async Task
        CompleteTrainingDummyHostileInterruptionClaimsAsync(
            IReadOnlyList<TrainingDummyHostileInterruptionClaim> claims)
    {
        foreach (var claim in claims)
        {
            claim.NotificationBarrier.TrySetResult();
        }
        foreach (var claim in claims)
        {
            try
            {
                await claim.Completion;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[training-status] interruption deferred " +
                    $"target={claim.TargetDisplayName} " +
                    $"skill={claim.SkillId}: {ex.Message}");
            }
        }
    }

    private sealed record TrainingDummyHostileInterruptionClaim(
        string TargetDisplayName,
        int SkillId,
        TaskCompletionSource NotificationBarrier,
        Task Completion);

    private async Task<bool>
        TryBeginIntonedTrainingDummyHostileStatusCastAsync(
            GamePacket packet,
            SkillCastRequest cast,
            SkillCombatDefinition skill,
            HostileStatusEffectDefinition definition,
            CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null ||
            definition.TargetMode !=
                HostileStatusTargetMode.SingleTarget ||
            !_registry.TryGetCurrentWorldSessionByObjectId(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                out var target) ||
            !_registry.IsTrainingDummy(target.Character))
        {
            return false;
        }

        if (cast.CasterObjectId != LocalPlayerObjectId ||
            !_registry.TryGetCurrentWorldSessionByCharacterId(
                _session,
                character.CurrentMap,
                character.Id,
                out var attacker) ||
            target.Character.CurrentHp <= 0 ||
            !IsTrainingDummyHostileStatusTargetInRange(
                character,
                target,
                skill))
        {
            Console.WriteLine(
                "[training-status] rejected invalid intonation " +
                $"character={character.Name} skill={cast.SkillId} " +
                $"target={cast.TargetObjectId}");
            return true;
        }

        int currentMana;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
        }
        if (currentMana < definition.ManaCost)
        {
            await SendInsufficientManaRejectionAsync(
                currentMana,
                cancellationToken,
                "TrainingDummyHostileIntonationManaRejected");
            return true;
        }

        var started = await TryBeginPendingSkillCastAsync(
            cast.SkillId,
            skill.CastTime,
            "training-dummy-hostile-status",
            token => _registry
                .PublishTrainingDummyHostileCastVisualAsync(
                    attacker,
                    target,
                    packet.Buffer,
                    definition,
                    token),
            async token =>
            {
                _ = await TryHandleTrainingDummyHostileStatusCastAsync(
                    packet,
                    cast,
                    skill,
                    definition,
                    publishCastVisual: false,
                    token);
            },
            cancellationToken,
            () => IsIntonedTrainingDummyHostileStatusCastCurrent(
                target,
                skill,
                definition));
        if (!started)
        {
            Console.WriteLine(
                "[training-status] intonation not started " +
                $"character={character.Name} skill={cast.SkillId}");
        }
        return true;
    }

    private bool IsIntonedTrainingDummyHostileStatusCastCurrent(
        GameSessionContext target,
        in SkillCombatDefinition skill,
        in HostileStatusEffectDefinition definition)
    {
        var character = _character;
        if (character is null ||
            !_registry.IsCurrentWorldSessionSnapshot(_session, target) ||
            !_registry.IsTrainingDummy(target.Character) ||
            target.Character.CurrentHp <= 0)
        {
            return false;
        }

        lock (character.VitalsSync)
        {
            return IsTrainingDummyHostileStatusTargetInRange(
                    character,
                    target,
                    skill);
        }
    }

    private static bool IsTrainingDummyHostileStatusTargetInRange(
        GameCharacter attacker,
        GameSessionContext target,
        in SkillCombatDefinition skill) =>
        PlayerCombatRules.IsWithinSkillRange(
            attacker.PositionX,
            attacker.PositionZ,
            target.Character.PositionX,
            target.Character.PositionZ,
            TrainingDummyDamageSkillPolicy.Snapshot(skill));

    private async Task PublishTrainingDummyHostileManaAsync(
        GameSessionContext attacker,
        int currentMana,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.PlayerManaUpdate(
                LocalPlayerObjectId,
                currentMana),
            cancellationToken,
            "TrainingDummyHostileStatusManaSelf");
        await _registry.BroadcastToMapAsync(
            attacker.MapId,
            PacketBuilder.PlayerManaUpdate(
                attacker.ObjectId,
                currentMana),
            cancellationToken,
            _session,
            "TrainingDummyHostileStatusManaWorld");
    }
}
