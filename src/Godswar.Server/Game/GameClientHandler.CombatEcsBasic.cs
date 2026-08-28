using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleBasicAttackEcsAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        if (!BasicAttackRequest.TryParse(packet.Buffer, out var attack))
        {
            Console.WriteLine(
                $"[attack] ignored malformed basic attack len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        if (attack.AttackerObjectId != LocalPlayerObjectId)
        {
            Console.WriteLine(
                $"[attack] rejected spoofed attacker character={character.Name} supplied={attack.AttackerObjectId} expected={LocalPlayerObjectId}");
            return;
        }

        if (!RevalidateCurrentWorldEffectOwnership(
                "ecs_basic_attack_damage"))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_registry.GetPlayerSkillCastControl(_session, now) ==
            PlayerSkillCastControl.Stunned)
        {
            Console.WriteLine(
                $"[attack] rejected stunned character={character.Name} target={attack.TargetObjectId}");
            return;
        }

        using var elementalAuthority =
            CapturePveElementalCommitAuthority(character);
        if (elementalAuthority is null)
        {
            Console.WriteLine(
                $"[attack] rejected stale elemental authority character={character.Name} target={attack.TargetObjectId}");
            return;
        }

        var interruptAdmittedCast = false;
        var decision = _registry.ResolvePlayerCombatEcs(
            _session,
            character,
            LocalPlayerObjectId,
            _nextBasicAttackAt,
            PlayerCombatEcsRequest.BasicAttack(
                now,
                attack.TargetObjectId,
                attack.AttackerX,
                attack.AttackerZ),
            onAdmittedAttempt: () => interruptAdmittedCast = true);
        _nextBasicAttackAt = decision.NextBasicAttackAt;

        if (!decision.IntentAccepted)
        {
            LogBasicAttackEcsRejection(
                character,
                attack,
                decision.RejectionReason);
            return;
        }

        if (decision.BasicAttackResolution is not { } resolvedTarget ||
            resolvedTarget.TargetObjectId != attack.TargetObjectId)
        {
            Console.WriteLine(
                $"[attack] rejected unresolved monster character={character.Name} target={attack.TargetObjectId}");
            return;
        }

        var resolution = resolvedTarget.Resolution;
        var attackSelector = character.Profession is 2 or 3
            ? (byte)5
            : (byte)3;
        if (!resolution.Hit)
        {
            if (!decision.Hits.IsEmpty)
            {
                throw new InvalidOperationException(
                    "A missed ECS basic attack produced a health mutation.");
            }

            if (interruptAdmittedCast)
            {
                await InterruptPendingSkillCastAsync(
                    SkillCastInterruptionReason.Replaced,
                    cancellationToken);
            }
            var selfMiss = BuildResolvedBasicAttackPacket(
                LocalPlayerObjectId,
                attack.TargetObjectId,
                attackSelector,
                resolution);
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                character.CurrentMap,
                attack.TargetObjectId,
                selfMiss,
                resolvedTarget.SpawnGeneration,
                cancellationToken,
                "BasicAttackMissSelf");
            var missViewers = await _registry.BroadcastToMonsterViewersAsync(
                character.CurrentMap,
                attack.TargetObjectId,
                BuildResolvedBasicAttackPacket(
                    CurrentPlayerObjectId,
                    attack.TargetObjectId,
                    attackSelector,
                    resolution),
                cancellationToken,
                _session,
                "BasicAttackMissWorld",
                expectedSpawnGeneration: resolvedTarget.SpawnGeneration);
            Console.WriteLine(
                $"[attack] miss character={character.Name} target={attack.TargetObjectId} event={resolution.EventId} hit={resolution.Rolls.HitRollBasisPoints}/{resolution.Rolls.HitChanceBasisPoints} viewers={missViewers}");
            return;
        }

        if (decision.Hits.Length != 1)
        {
            Console.WriteLine(
                $"[attack] rejected stale monster character={character.Name} target={attack.TargetObjectId}");
            return;
        }

        var hit = decision.Hits[0];
        var damageResult = hit.Result;
        if (hit.ReportedDamage != resolution.Damage)
        {
            throw new InvalidOperationException(
                "The ECS basic-attack mutation diverged from its resolution.");
        }

        var lifeAbsorption = CommitPveLifeAbsorption(
            character,
            [new PveCommittedMonsterDamage(
                resolution.EventId,
                damageResult.ObjectId,
                damageResult.Monster.SpawnGeneration,
                damageResult.BeforeHealth - damageResult.AfterHealth)]);
        var elementalCommit = CommitPveElementalHit(
            elementalAuthority,
            CombatEventProvenance.DirectBasicAttack,
            resolution,
            damageResult);
        var pendingReward = damageResult.Killed
            ? await PrepareClaimedMonsterKillRewardAsync(damageResult)
            : null;
        var elementalRewards =
            await PreparePveElementalKillRewardsAsync(
                elementalAuthority,
                elementalCommit);

        await _registry.PublishMonsterClaimStateAsync(
            _session,
            character.CurrentMap,
            damageResult,
            cancellationToken);

        if (interruptAdmittedCast)
        {
            await InterruptPendingSkillCastAsync(
                SkillCastInterruptionReason.Replaced,
                cancellationToken);
        }
        var reportedDamage = resolution.CapturedDamageValue;
        var selfPacket = BuildResolvedBasicAttackPacket(
            LocalPlayerObjectId,
            attack.TargetObjectId,
            attackSelector,
            resolution,
            damageResult.Killed);
        var casterNotified = true;
        try
        {
            await _registry.DeliverMonsterHealthPacketToViewerAsync(
                _session,
                character.CurrentMap,
                attack.TargetObjectId,
                selfPacket,
                damageResult.HealthMutation!.Value,
                cancellationToken,
                "BasicAttackSelf");
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[attack] caster notification failed character={character.Name} target={attack.TargetObjectId}: {ex.Message}");
        }

        var worldObjectId = CurrentPlayerObjectId;
        var viewers = await _registry.BroadcastToMonsterViewersAsync(
            character.CurrentMap,
            attack.TargetObjectId,
            BuildResolvedBasicAttackPacket(
                worldObjectId,
                attack.TargetObjectId,
                attackSelector,
                resolution,
                damageResult.Killed),
            cancellationToken,
            _session,
            "BasicAttackWorld",
            healthMutation: damageResult.HealthMutation);

        await PublishPveLifeAbsorptionAsync(
            character,
            lifeAbsorption,
            cancellationToken);

        await PublishPveElementalCommitAsync(
            elementalAuthority,
            elementalCommit,
            elementalRewards,
            cancellationToken);

        if (pendingReward is not null)
        {
            await pendingReward.PublishAsync(cancellationToken);
        }

        Console.WriteLine(
            $"[attack] damage character={character.Name} target={attack.TargetObjectId} event={resolution.EventId} outcome={resolution.Outcome} resolved={reportedDamage} applied={damageResult.BeforeHealth - damageResult.AfterHealth} hp={damageResult.AfterHealth}/{damageResult.Monster.MaximumHealth} killed={damageResult.Killed} first-hit={damageResult.FirstHitCharacterId} caster-notified={casterNotified} viewers={viewers}");
    }

    internal static byte[] BuildResolvedBasicAttackPacket(
        uint attackerObjectId,
        uint targetObjectId,
        byte attackSelector,
        in CombatResolution resolution,
        bool killed = false) =>
        PacketBuilder.PhysicalDamage(
            attackerObjectId,
            attackerX: 0f,
            attackerY: 0f,
            attackerZ: 0f,
            targetObjectId,
            resolution.CapturedDamageValue,
            result: killed ? (byte)5 : attackSelector,
            damageType: (byte)resolution.Outcome);

    private void LogBasicAttackEcsRejection(
        State.GameCharacter character,
        in BasicAttackRequest attack,
        PlayerCombatRejectionReason reason)
    {
        switch (reason)
        {
            case PlayerCombatRejectionReason.SourceDead:
                Console.WriteLine(
                    $"[attack] ignored basic attack from dead character={character.Name}");
                break;
            case PlayerCombatRejectionReason.InvalidCoordinates:
                Console.WriteLine(
                    $"[attack] rejected mismatched position character={character.Name} server={character.PositionX:F2},{character.PositionZ:F2} reported={attack.AttackerX:F2},{attack.AttackerZ:F2}");
                break;
            case PlayerCombatRejectionReason.OutOfRange:
                if (_registry.TryGetMonsterSnapshot(
                        _session,
                        character.CurrentMap,
                        attack.TargetObjectId,
                        out var target))
                {
                    Console.WriteLine(
                        $"[attack] rejected out-of-range monster character={character.Name} target={attack.TargetObjectId} player={attack.AttackerX:F2},{attack.AttackerZ:F2} monster={target.X:F2},{target.Z:F2}");
                }
                else
                {
                    Console.WriteLine(
                        $"[attack] rejected out-of-range monster character={character.Name} target={attack.TargetObjectId}");
                }

                break;
            case PlayerCombatRejectionReason.CooldownActive:
                Console.WriteLine(
                    $"[attack] rejected cooldown character={character.Name} target={attack.TargetObjectId}");
                break;
            case PlayerCombatRejectionReason.TargetUnavailable:
            case PlayerCombatRejectionReason.TargetGenerationMismatch:
            case PlayerCombatRejectionReason.TargetRevisionMismatch:
                Console.WriteLine(
                    $"[attack] rejected unavailable monster character={character.Name} target={attack.TargetObjectId}");
                break;
            default:
                Console.WriteLine(
                    $"[attack] rejected stale monster character={character.Name} target={attack.TargetObjectId}");
                break;
        }
    }
}
