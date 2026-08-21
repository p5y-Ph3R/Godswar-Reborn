using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleHostileMonsterSingleSkillCastEcsAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        bool publishCastVisual,
        uint? expectedTargetSpawnGeneration,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        if (!RevalidateCurrentWorldEffectOwnership(
                "ecs_single_skill_damage"))
        {
            return;
        }

        var manaCost = Math.Max(0, combat.Mp);
        var observedAt = DateTimeOffset.UtcNow;
        using var elementalAuthority =
            CapturePveElementalCommitAuthority(character);
        if (elementalAuthority is null)
        {
            Console.WriteLine(
                $"[skill] rejected stale elemental authority character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        if (!TryClaimHostileSkillCooldown(
                character,
                combat,
                observedAt,
                out var cooldownLease))
        {
            return;
        }

        PlayerCombatEcsDecision decision;
        try
        {
            decision = _registry.ResolvePlayerCombatEcs(
                _session,
                character,
                LocalPlayerObjectId,
                _nextBasicAttackAt,
                PlayerCombatEcsRequest.HostileSkill(
                    PlayerCombatIntentKind.SingleTargetSkill,
                    observedAt,
                    cast.TargetObjectId,
                    combat,
                    expectedTargetSpawnGeneration));
        }
        catch
        {
            ReleaseHostileSkillCooldown(cooldownLease);
            throw;
        }

        if (!decision.IntentAccepted)
        {
            RequireRejectedEcsSkillResourcesClosed(decision);
            ReleaseHostileSkillCooldown(cooldownLease);
            if (decision.ResourcesRefunded && manaCost > 0)
            {
                await PersistAndSendSkillManaRefundAsync(
                    character,
                    decision.CurrentMana,
                    cancellationToken);
            }

            await HandleSingleSkillEcsRejectionAsync(
                cast,
                combat,
                decision,
                cancellationToken);
            return;
        }

        if (decision.Resolutions is not [var resolvedTarget] ||
            resolvedTarget.TargetObjectId != cast.TargetObjectId)
        {
            throw new InvalidOperationException(
                "An accepted ECS single-target skill did not retain its " +
                "authoritative resolution.");
        }

        var resolution = resolvedTarget.Resolution;
        if (!resolution.Hit)
        {
            if (!decision.Hits.IsEmpty)
            {
                throw new InvalidOperationException(
                    "A missed ECS hostile skill produced a health mutation.");
            }

            await PublishUnreportedHostileMonsterSkillMissAsync(
                packet,
                cast,
                combat,
                resolvedTarget.SpawnGeneration,
                publishCastVisual,
                decision.CurrentMana,
                resolution,
                cancellationToken);
            return;
        }

        if (decision.Hits.Length != 1)
        {
            if (!decision.ResourcesRefunded)
            {
                throw new InvalidOperationException(
                    "An accepted ECS single-target mutation disappeared " +
                    "without closing its mana reservation.");
            }

            ReleaseHostileSkillCooldown(cooldownLease);
            if (decision.ResourcesRefunded && manaCost > 0)
            {
                await PersistAndSendSkillManaRefundAsync(
                    character,
                    decision.CurrentMana,
                    cancellationToken);
            }

            Console.WriteLine(
                $"[skill] rejected stale monster target character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        var hit = decision.Hits[0];
        var damageResult = hit.Result;
        if (hit.ReportedDamage != resolution.Damage)
        {
            throw new InvalidOperationException(
                "The ECS hostile-skill mutation diverged from its resolution.");
        }

        var lifeAbsorption = CommitPveLifeAbsorption(
            character,
            [CreatePveCommittedMonsterDamage(
                resolution,
                damageResult)]);
        var elementalCommit = CommitPveElementalHit(
            elementalAuthority,
            CombatEventProvenance.DirectSkill,
            resolution,
            damageResult);
        var pendingReward = damageResult.Killed
            ? await PrepareMonsterKillRewardAsync(damageResult)
            : null;
        var elementalRewards =
            await PreparePveElementalKillRewardsAsync(
                elementalAuthority,
                elementalCommit);
        var reportedDamage = resolution.CapturedDamageValue;
        _registry.UpdateCharacter(
            _session,
            character,
            advanceWorldRevision: false);
        var appliedDamage =
            damageResult.BeforeHealth - damageResult.AfterHealth;
        var targetX = damageResult.Monster.X;
        var targetZ = damageResult.Monster.Z;
        var selfVisual = PacketBuilder.SkillCastVisual(
            packet.Buffer,
            LocalPlayerObjectId);
        var selfDamage = PacketBuilder.SkillDamage(
            attackerObjectId: LocalPlayerObjectId,
            targetObjectId: cast.TargetObjectId,
            resultFlags: 1,
            damage: reportedDamage,
            skillId: cast.SkillId,
            targetX: targetX,
            targetZ: targetZ);
        var selfImpact = PacketBuilder.SkillCastImpact(
            LocalPlayerObjectId,
            cast.TargetObjectId,
            cast.SkillId,
            targetX,
            targetZ);

        var casterNotified = true;
        try
        {
            if (publishCastVisual)
            {
                await _registry.DeliverMonsterPacketToViewerAsync(
                    _session,
                    character.CurrentMap,
                    cast.TargetObjectId,
                    selfVisual,
                    damageResult.Monster.SpawnGeneration,
                    cancellationToken,
                    "SkillCastSelf");
            }
            await _registry.DeliverMonsterHealthPacketToViewerAsync(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                selfDamage,
                damageResult.HealthMutation!.Value,
                cancellationToken,
                "SkillDamageSelf");
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                selfImpact,
                damageResult.Monster.SpawnGeneration,
                cancellationToken,
                "SkillCastImpactSelf");
            if (manaCost > 0)
            {
                int currentManaForPacket;
                lock (character.VitalsSync)
                {
                    currentManaForPacket = character.CurrentMp;
                }

                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(
                        LocalPlayerObjectId,
                        currentManaForPacket),
                    cancellationToken,
                    "SkillManaSelf");
            }
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[skill] caster notification failed character={character.Name} target={cast.TargetObjectId}: {ex.Message}");
        }

        var worldObjectId = CurrentPlayerObjectId;
        var visualRecipients = publishCastVisual
            ? await _registry.BroadcastToMonsterViewersAsync(
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillCastVisual(
                    packet.Buffer,
                    worldObjectId),
                cancellationToken,
                _session,
                "SkillCastWorld",
                expectedSpawnGeneration:
                    damageResult.Monster.SpawnGeneration)
            : 0;
        var damageRecipients =
            await _registry.BroadcastToMonsterViewersAsync(
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillDamage(
                    attackerObjectId: worldObjectId,
                    targetObjectId: cast.TargetObjectId,
                    resultFlags: 1,
                    damage: reportedDamage,
                    skillId: cast.SkillId,
                    targetX: targetX,
                    targetZ: targetZ),
                cancellationToken,
                _session,
                "SkillDamageWorld",
                healthMutation: damageResult.HealthMutation);
        var impactRecipients =
            await _registry.BroadcastToMonsterViewersAsync(
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillCastImpact(
                    worldObjectId,
                    cast.TargetObjectId,
                    cast.SkillId,
                    targetX,
                    targetZ),
                cancellationToken,
                _session,
                "SkillCastImpactWorld",
                expectedSpawnGeneration:
                    damageResult.Monster.SpawnGeneration);

        await PublishPveLifeAbsorptionAsync(
            character,
            lifeAbsorption,
            cancellationToken,
            persistVitals: false);

        await PublishPveElementalCommitAsync(
            elementalAuthority,
            elementalCommit,
            elementalRewards,
            cancellationToken);

        await PersistSkillVitalsAsync(
            character,
            areaSkill: false,
            cancellationToken);

        if (pendingReward is not null)
        {
            await PublishMonsterKillRewardAsync(
                pendingReward,
                cancellationToken);
        }

        int currentMana;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
        }

        Console.WriteLine(
            $"[skill] damage character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId} event={resolution.EventId} outcome={resolution.Outcome} resolved={reportedDamage} applied={appliedDamage} hp={damageResult.AfterHealth}/{damageResult.Monster.MaximumHealth} killed={damageResult.Killed} mp={currentMana}/{character.MaxMp} caster-notified={casterNotified} viewers={Math.Max(visualRecipients, Math.Max(damageRecipients, impactRecipients))}");
    }

    private async Task HandleSingleSkillEcsRejectionAsync(
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        PlayerCombatEcsDecision decision,
        CancellationToken cancellationToken)
    {
        var character = _character!;
        switch (decision.RejectionReason)
        {
            case PlayerCombatRejectionReason.SourceDead:
                Console.WriteLine(
                    $"[skill] ignored cast from dead character={character.Name}");
                break;
            case PlayerCombatRejectionReason.InsufficientMana:
                Console.WriteLine(
                    $"[skill] rejected insufficient MP character={character.Name} skill={cast.SkillId} mp={decision.CurrentMana} cost={Math.Max(0, combat.Mp)}");
                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(
                        LocalPlayerObjectId,
                        decision.CurrentMana),
                    cancellationToken,
                    "SkillManaRejected");
                break;
            case PlayerCombatRejectionReason.OutOfRange:
                if (_registry.TryGetMonsterSnapshot(
                        _session,
                        character.CurrentMap,
                        cast.TargetObjectId,
                        out var target))
                {
                    Console.WriteLine(
                        $"[skill] rejected out-of-range monster character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId} player={character.PositionX:F2},{character.PositionZ:F2} monster={target.X:F2},{target.Z:F2} range={combat.Distance:F2}");
                }
                else
                {
                    Console.WriteLine(
                        $"[skill] rejected unavailable monster character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
                }

                break;
            case PlayerCombatRejectionReason.TargetUnavailable:
            case PlayerCombatRejectionReason.TargetGenerationMismatch:
            case PlayerCombatRejectionReason.TargetRevisionMismatch:
                Console.WriteLine(
                    $"[skill] rejected unavailable monster character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
                break;
            default:
                Console.WriteLine(
                    $"[skill] rejected stale monster target character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
                break;
        }
    }

    private async Task PersistAndSendSkillManaRefundAsync(
        GameCharacter character,
        int currentMana,
        CancellationToken cancellationToken)
    {
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
        }

        try
        {
            await PersistVitalsCheckpointAsync(
                character,
                force: false,
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[skill] refunded vitals persistence deferred character={character.Name}: {ex.Message}");
        }

        await _session.SendAsync(
            PacketBuilder.PlayerManaUpdate(
                LocalPlayerObjectId,
                currentMana),
            cancellationToken,
            "SkillManaRefund");
    }

    private async Task PersistSkillVitalsAsync(
        GameCharacter character,
        bool areaSkill,
        CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        try
        {
            await PersistVitalsCheckpointAsync(
                character,
                force: false,
                cancellationToken);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException)
        {
            var area = areaSkill ? " area" : string.Empty;
            Console.WriteLine(
                $"[skill]{area} vitals persistence deferred character={character.Name}: {ex.Message}");
        }
    }

    private static void RequireRejectedEcsSkillResourcesClosed(
        PlayerCombatEcsDecision decision)
    {
        if (decision.ReservedMana > 0 &&
            !decision.ResourcesRefunded)
        {
            throw new InvalidOperationException(
                "A rejected ECS hostile skill retained a mana reservation.");
        }
    }
}
