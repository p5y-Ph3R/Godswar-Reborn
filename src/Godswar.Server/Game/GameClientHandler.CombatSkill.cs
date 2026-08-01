using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleSkillCastAsync(
        GamePacket packet,
        CancellationToken cancellationToken,
        bool intonationCompleted = false,
        uint? expectedTargetSpawnGeneration = null)
    {
        if (_character is null)
        {
            Console.WriteLine("[skill] ignored cast before character enter");
            return;
        }

        if (!intonationCompleted &&
            _character.CurrentHp <= 0)
        {
            Console.WriteLine($"[skill] ignored cast from dead character={_character.Name}");
            return;
        }

        if (!SkillCastRequest.TryParse(packet.Buffer, out var cast))
        {
            Console.WriteLine($"[skill] ignored cast payload too short len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        var control = _registry.GetPlayerSkillCastControl(
            _session,
            DateTimeOffset.UtcNow);
        // The coordinator validates control statuses before atomically
        // claiming completion. A status applied after that claim belongs to
        // the next action and must not discard this already-completed cast.
        if (!intonationCompleted &&
            control != PlayerSkillCastControl.None)
        {
            var hadPendingCast = HasPendingSkillCast;
            await InterruptPendingSkillCastAsync(
                PlayerSkillCastControlCatalog.ToInterruptionReason(
                    control),
                cancellationToken);
            if (!hadPendingCast)
            {
                await SendBlockedSkillCastNoticeAsync(
                    control,
                    cancellationToken);
            }
            return;
        }

        if (!intonationCompleted && HasPendingSkillCast)
        {
            await InterruptPendingSkillCastAsync(
                SkillCastInterruptionReason.Replaced,
                cancellationToken);
        }

        var castX = float.IsFinite(cast.CasterX) ? cast.CasterX : _character.PositionX;
        var castZ = float.IsFinite(cast.CasterZ) ? cast.CasterZ : _character.PositionZ;
        var learned = await IsSkillLearnedAsync(cast.SkillId, cancellationToken);

        Console.WriteLine(
            $"[skill] cast character={_character.Name} skill={cast.SkillId} learned={learned} caster={cast.CasterObjectId} target={cast.TargetObjectId} x={castX:F2} z={castZ:F2}");
        if (!learned)
        {
            Console.WriteLine(
                $"[skill] rejected unlearned skill character={_character.Name} skill={cast.SkillId}");
            return;
        }

        if (cast.SkillId == MountCatalog.RideSkillId)
        {
            await HandleRideSkillCastAsync(packet, cast, cancellationToken);
            return;
        }

        if (BackhaulSkillCatalog.TryGet(
                cast.SkillId,
                out var backhaul))
        {
            await HandleBackhaulSkillCastAsync(
                packet,
                cast,
                backhaul,
                cancellationToken);
            return;
        }

        if (cast.SkillId <= int.MaxValue &&
            SkillStatusEffectCatalog.TryGet((int)cast.SkillId, out var statusEffect))
        {
            await HandleSelfStatusSkillCastAsync(
                packet,
                cast,
                statusEffect,
                cancellationToken);
            return;
        }

        if (cast.SkillId > int.MaxValue ||
            !_gameplayCatalogs.SkillCombat.TryGet(
                (int)cast.SkillId,
                out var combat) ||
            !SkillCombatResolver.IsHostileMonsterSkill(combat))
        {
            Console.WriteLine(
                $"[skill] rejected unsupported combat skill character={_character.Name} skill={cast.SkillId}");
            return;
        }

        if (!intonationCompleted &&
            combat.CastTime > TimeSpan.Zero)
        {
            await BeginIntonedCombatSkillCastAsync(
                packet,
                cast,
                combat,
                cancellationToken);
            return;
        }

        if (SkillCombatResolver.IsHostileMonsterAreaSkill(combat))
        {
            await HandleHostileMonsterAreaSkillCastAsync(
                packet,
                cast,
                combat,
                publishCastVisual: !intonationCompleted,
                cancellationToken);
            return;
        }

        var isMonsterStunSkill =
            cast.SkillId <= int.MaxValue &&
            MonsterStunSkillCatalog.TryGet(
                (int)cast.SkillId,
                out _);
        if (_registry.PlayerRuntimeMode == PlayerRuntimeMode.Ecs &&
            !isMonsterStunSkill)
        {
            await HandleHostileMonsterSingleSkillCastEcsAsync(
                packet,
                cast,
                combat,
                publishCastVisual: !intonationCompleted,
                expectedTargetSpawnGeneration,
                cancellationToken);
            return;
        }

        if (!_registry.TryGetMonsterSnapshot(
                _session,
                _character.CurrentMap,
                cast.TargetObjectId,
                out var target) ||
            expectedTargetSpawnGeneration is { } expectedGeneration &&
            target.SpawnGeneration != expectedGeneration ||
            !_registry.IsMonsterVisibleTo(
                _session,
                cast.TargetObjectId,
                target.SpawnGeneration) ||
            !target.IsSpawned ||
            !target.IsAlive)
        {
            Console.WriteLine(
                $"[skill] rejected unavailable monster character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        if (!SkillCombatResolver.IsWithinRange(
                _character.PositionX,
                _character.PositionZ,
                target.X,
                target.Z,
                combat))
        {
            Console.WriteLine(
                $"[skill] rejected out-of-range monster character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId} player={_character.PositionX:F2},{_character.PositionZ:F2} monster={target.X:F2},{target.Z:F2} range={combat.Distance:F2}");
            return;
        }

        if (cast.SkillId <= int.MaxValue &&
            MonsterStunSkillCatalog.TryGet((int)cast.SkillId, out var stun))
        {
            await HandleHostileMonsterStunSkillCastAsync(
                packet,
                cast,
                combat,
                stun,
                expectedTargetSpawnGeneration ??
                    target.SpawnGeneration,
                cancellationToken);
            return;
        }

        if (!RevalidateCurrentWorldEffectOwnership(
                "single_skill_damage"))
        {
            return;
        }

        var manaCost = Math.Max(0, combat.Mp);
        int currentMana;
        var manaReserved = false;
        lock (_character.VitalsSync)
        {
            currentMana = _character.CurrentMp;
            if (currentMana >= manaCost)
            {
                _character.CurrentMp = currentMana - manaCost;
                currentMana = _character.CurrentMp;
                if (manaCost > 0)
                {
                    _character.MarkVitalsChanged();
                }
                manaReserved = true;
            }
        }

        if (!manaReserved)
        {
            Console.WriteLine(
                $"[skill] rejected insufficient MP character={_character.Name} skill={cast.SkillId} mp={currentMana} cost={manaCost}");
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "SkillManaRejected");
            return;
        }

        var requestedDamage = SkillCombatResolver.CalculateDamage(_character, combat);
        if (requestedDamage == 0 ||
            !RevalidateCurrentWorldEffectOwnership(
                "single_skill_damage") ||
            !_registry.TryApplyMonsterDamage(
                _character.CurrentMap,
                cast.TargetObjectId,
                requestedDamage,
                _character.Id,
                expectedTargetSpawnGeneration ??
                    target.SpawnGeneration,
                out var damageResult) ||
            damageResult.BeforeHealth == damageResult.AfterHealth)
        {
            if (manaCost > 0)
            {
                lock (_character.VitalsSync)
                {
                    _character.CurrentMp = Math.Min(
                        Math.Max(0, _character.MaxMp),
                        (int)Math.Min(int.MaxValue, (long)_character.CurrentMp + manaCost));
                    _character.MarkVitalsChanged();
                    currentMana = _character.CurrentMp;
                }

                try
                {
                    await PersistVitalsCheckpointAsync(
                        _character,
                        force: false,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine(
                        $"[skill] refunded vitals persistence deferred character={_character.Name}: {ex.Message}");
                }

                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                    cancellationToken,
                    "SkillManaRefund");
            }

            Console.WriteLine(
                $"[skill] rejected stale monster target character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        var pendingReward = damageResult.Killed
            ? await PrepareMonsterKillRewardAsync(damageResult)
            : null;

        var appliedDamage = damageResult.BeforeHealth - damageResult.AfterHealth;
        // The working server reports the resolved hit amount even when it exceeds
        // the monster's remaining HP. Shared runtime health is still clamped at 0.
        var reportedDamage = requestedDamage;
        var targetX = damageResult.Monster.X;
        var targetZ = damageResult.Monster.Z;
        var publishCastVisual = !intonationCompleted;
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
                    _character.CurrentMap,
                    cast.TargetObjectId,
                    selfVisual,
                    damageResult.Monster.SpawnGeneration,
                    cancellationToken,
                    "SkillCastSelf");
            }
            await _registry.DeliverMonsterHealthPacketToViewerAsync(
                _session,
                _character.CurrentMap,
                cast.TargetObjectId,
                selfDamage,
                damageResult.HealthMutation!.Value,
                cancellationToken,
                "SkillDamageSelf");
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                _character.CurrentMap,
                cast.TargetObjectId,
                selfImpact,
                damageResult.Monster.SpawnGeneration,
                cancellationToken,
                "SkillCastImpactSelf");
            if (manaCost > 0)
            {
                lock (_character.VitalsSync)
                {
                    currentMana = _character.CurrentMp;
                }

                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                    cancellationToken,
                    "SkillManaSelf");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The hit already changed shared state. Continue notifying the other
            // viewers even if the caster disconnected during its own response.
            casterNotified = false;
            Console.WriteLine(
                $"[skill] caster notification failed character={_character.Name} target={cast.TargetObjectId}: {ex.Message}");
        }

        var worldObjectId = WorldObjectIds.ForPlayer(_character.Id);
        var visualRecipients = publishCastVisual
            ? await _registry.BroadcastToMonsterViewersAsync(
                _character.CurrentMap,
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
        var damageRecipients = await _registry.BroadcastToMonsterViewersAsync(
            _character.CurrentMap,
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
        var impactRecipients = await _registry.BroadcastToMonsterViewersAsync(
            _character.CurrentMap,
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
            expectedSpawnGeneration: damageResult.Monster.SpawnGeneration);

        if (pendingReward is not null)
        {
            await PublishMonsterKillRewardAsync(
                pendingReward,
                cancellationToken);
        }

        if (_account is not null)
        {
            try
            {
                await PersistVitalsCheckpointAsync(
                    _character,
                    force: false,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Database availability must not suppress an already-authoritative
                // shared hit. The in-memory session remains correct and can retry.
                Console.WriteLine(
                    $"[skill] vitals persistence deferred character={_character.Name}: {ex.Message}");
            }
        }

        lock (_character.VitalsSync)
        {
            currentMana = _character.CurrentMp;
        }

        Console.WriteLine(
            $"[skill] damage character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId} resolved={reportedDamage} applied={appliedDamage} hp={damageResult.AfterHealth}/{damageResult.Monster.MaximumHealth} killed={damageResult.Killed} mp={currentMana}/{_character.MaxMp} caster-notified={casterNotified} viewers={Math.Max(visualRecipients, Math.Max(damageRecipients, impactRecipients))}");
    }

}
