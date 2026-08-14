using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool RejectDeadLegacyMovement(GamePacket packet)
    {
        if (_character is not { CurrentHp: <= 0 })
        {
            return false;
        }

        Console.WriteLine(
            $"[world] ignored legacy movement from dead character={_character.Name} opcode={packet.Opcode}");
        return true;
    }

    private async Task<bool> HandleWalkAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return false;
        }
        if (_session.IsRealtimeMovementActive)
        {
            await RejectLegacyWalkAfterRealtimeCutoverAsync(
                cancellationToken);
            return false;
        }

        var movementAcceptedAt = DateTimeOffset.UtcNow;
        if (!IsElementalMovementAllowed(movementAcceptedAt))
        {
            return false;
        }

        var updated = _registry.PlayerRuntimeMode ==
            PlayerRuntimeMode.Ecs
            ? UpdateCharacterPositionFromWalkEcs(
                packet,
                out var movement)
            : UpdateCharacterPositionFromWalk(
                packet,
                out movement);
        if (!updated)
        {
            return false;
        }

        CommitAcceptedElementalMovement(
            movement,
            movementAcceptedAt);

        await InterruptPendingSkillCastAsync(
            SkillCastInterruptionReason.Movement,
            cancellationToken);

        if (await TryBeginMapTransitionAsync(
                movement,
                cancellationToken))
        {
            // The source map receives an explicit removal during the
            // transition. Never broadcast the triggering walk into either
            // the old or the still-hidden destination world.
            return false;
        }

        await RefreshNearbyWorldObjectsAsync("walk", cancellationToken);
        await PersistCharacterPositionAsync(force: false, cancellationToken);

        return true;
    }

    private async Task HandleReviveAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[revive] ignored request before character enter");
            return;
        }

        if (!ReviveRequest.TryParse(packet.Buffer, out var request))
        {
            Console.WriteLine($"[revive] ignored malformed request len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        if (request.PlayerObjectId != LocalPlayerObjectId)
        {
            Console.WriteLine(
                $"[revive] ignored spoofed player object character={_character.Name} request-object={request.PlayerObjectId} expected-object={LocalPlayerObjectId}");
            return;
        }

        if (request.ReviveType != ReviveRequest.FreeReviveType)
        {
            Console.WriteLine(
                $"[revive] ignored unsupported type character={_character.Name} requested-type={request.ReviveType}");
            return;
        }

        if (_character.CurrentHp > 0)
        {
            Console.WriteLine($"[revive] ignored request for living character={_character.Name}");
            return;
        }

        var revivedLifeRevision = _registry.AdvancePlayerLifeRevision(_session);
        try
        {
            await _registry.SetPersistentRuntimeStatusAndPublishAsync(
                _session,
                MountCatalog.RuntimeStatusKind,
                statusId: 0,
                priority: 0,
                beneficial: false,
                movementSpeedBonus: 0f,
                active: false,
                DateTimeOffset.UtcNow,
                "mount-revive",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[mount] failed publishing revive dismount character={_character.Name} life={revivedLifeRevision}: {ex.Message}");
        }

        var previousMap = _character.CurrentMap;
        if (_worldPresenceAnnounced)
        {
            await BroadcastPlayerLeaveAsync(cancellationToken);
        }

        if (_registered)
        {
            _registry.Remove(_session, preservePlayerStatus: true);
            _registered = false;
        }

        _worldPresenceAnnounced = false;
        _clientReadyReceived = false;
        _playerDetailSent = false;
        _enterUiReadyReceived = false;
        _postEnterBootstrapSent = false;
        ClearLocalNpcCatalog();
        _nextBasicAttackAt = DateTimeOffset.MinValue;

        // Type 2 is the only capture-proven free-revival path. Currency-backed
        // in-place revival remains unsupported until its native contract and
        // settlement rules are proven.
        await RestoreFreeRevivalStateAsync(cancellationToken);
        await HandleEnterGameAsync(cancellationToken);
        Console.WriteLine(
            $"[revive] free revival character={_character.Name} request-object={request.PlayerObjectId} requested-type={request.ReviveType} map={previousMap}->{_character.CurrentMap} hp={_character.CurrentHp}/{_character.MaxHp} mp={_character.CurrentMp}/{_character.MaxMp}");
    }

    private async Task RestoreFreeRevivalStateAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        GameDefaults.InitializeStartingLocation(_character);
        _character.MarkPositionChanged();
        lock (_character.VitalsSync)
        {
            _character.CurrentHp = Math.Max(1, _character.MaxHp / 10);
            _character.CurrentMp = Math.Max(0, _character.MaxMp / 10);
            _character.MarkVitalsChanged();
        }
        _positionDirty = false;
        _lastPositionPersistUtc = DateTime.UtcNow;

        if (!await PersistPositionCheckpointAsync(
                _character,
                force: true,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The revival position checkpoint was not durable.");
        }
        if (!await PersistVitalsCheckpointAsync(
                _character,
                force: true,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "The revival vitals checkpoint was not durable.");
        }
    }

    private async Task HandleBasicAttackAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[attack] ignored basic attack before character enter");
            return;
        }

        if (await TryHandlePvpBasicAttackAsync(
                packet,
                cancellationToken))
        {
            return;
        }

        if (_registry.PlayerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            await HandleBasicAttackEcsAsync(
                packet,
                cancellationToken);
            return;
        }

        if (_character.CurrentHp <= 0)
        {
            Console.WriteLine($"[attack] ignored basic attack from dead character={_character.Name}");
            return;
        }

        if (!BasicAttackRequest.TryParse(packet.Buffer, out var attack))
        {
            Console.WriteLine($"[attack] ignored malformed basic attack len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        if (attack.AttackerObjectId != LocalPlayerObjectId)
        {
            Console.WriteLine(
                $"[attack] rejected spoofed attacker character={_character.Name} supplied={attack.AttackerObjectId} expected={LocalPlayerObjectId}");
            return;
        }

        if (!_registry.TryGetMonsterSnapshot(
                _session,
                _character.CurrentMap,
                attack.TargetObjectId,
                out var target) ||
            !_registry.IsMonsterVisibleTo(
                _session,
                attack.TargetObjectId,
                target.SpawnGeneration) ||
            !target.IsSpawned ||
            !target.IsAlive)
        {
            Console.WriteLine($"[attack] rejected unavailable monster character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        if (!MonsterCombatResolver.TryResolvePlayerBasicAttackPosition(
                _character.PositionX,
                _character.PositionZ,
                attack.AttackerX,
                attack.AttackerZ,
                out var attackX,
                out var attackZ))
        {
            Console.WriteLine(
                $"[attack] rejected mismatched position character={_character.Name} server={_character.PositionX:F2},{_character.PositionZ:F2} reported={attack.AttackerX:F2},{attack.AttackerZ:F2}");
            return;
        }

        if (!MonsterCombatResolver.IsWithinBasicAttackRange(
                attackX,
                attackZ,
                target.X,
                target.Z,
                PlayerCombatRules.ResolveBasicAttackRange(
                    (_character.CalculatedStats ??
                     CharacterStats.FromCharacter(_character))
                    .BasicAttackRange)))
        {
            Console.WriteLine(
                $"[attack] rejected out-of-range monster character={_character.Name} target={attack.TargetObjectId} player={attackX:F2},{attackZ:F2} monster={target.X:F2},{target.Z:F2}");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_registry.GetPlayerSkillCastControl(_session, now) ==
            PlayerSkillCastControl.Stunned)
        {
            Console.WriteLine(
                $"[attack] rejected stunned character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        if (now < _nextBasicAttackAt)
        {
            Console.WriteLine($"[attack] rejected cooldown character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        if (!RevalidateCurrentWorldEffectOwnership(
                "basic_attack_resolution"))
        {
            Console.WriteLine(
                $"[attack] rejected stale ownership character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        using var elementalAuthority =
            CapturePveElementalCommitAuthority(_character);
        if (elementalAuthority is null)
        {
            Console.WriteLine(
                $"[attack] rejected stale elemental authority character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        var admittedCombatRevision =
            NextAdmittedLegacyCombatRevision();
        var eventId = CombatEventIdentity.ForPlayerMonsterBasicAttack(
            _character.Id,
            target.ObjectId,
            target.SpawnGeneration,
            target.HealthRevision,
            (ulong)admittedCombatRevision);
        var targetCombat = _gameplayCatalogs.MonsterCombatProfiles
            .Resolve(target.Definition)
            .ToTargetStats();
        targetCombat = _registry.AdjustPveMonsterTargetStats(
            _session,
            target,
            now,
            targetCombat);
        var resolution = MonsterCombatResolver.ResolvePlayerBasicAttack(
            _character,
            targetCombat,
            eventId);
        resolution = _registry.AdjustPveOutgoingResolution(
            _session,
            _character,
            target,
            CombatEventProvenance.DirectBasicAttack,
            now,
            resolution,
            checked((ulong)admittedCombatRevision));
        var attackStats = _character.CalculatedStats ??
            CharacterStats.FromCharacter(_character);
        var cooldown = PlayerCombatRules.ResolveBasicAttackCooldown(
            attackStats.BasicAttackIntervalMilliseconds);
        _nextBasicAttackAt = now + cooldown;
        var attackSelector = _character.Profession is 2 or 3
            ? (byte)5
            : (byte)3;
        if (!resolution.Hit)
        {
            await InterruptPendingSkillCastAsync(
                SkillCastInterruptionReason.Replaced,
                cancellationToken);
            var selfMiss = PacketBuilder.PhysicalDamage(
                LocalPlayerObjectId,
                0f,
                0f,
                0f,
                attack.TargetObjectId,
                resolution.CapturedDamageValue,
                result: attackSelector,
                damageType: (byte)resolution.Outcome);
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                _character.CurrentMap,
                attack.TargetObjectId,
                selfMiss,
                target.SpawnGeneration,
                cancellationToken,
                "BasicAttackMissSelf");
            var missViewers = await _registry.BroadcastToMonsterViewersAsync(
                _character.CurrentMap,
                attack.TargetObjectId,
                PacketBuilder.PhysicalDamage(
                    WorldObjectIds.ForPlayer(_character.Id),
                    0f,
                    0f,
                    0f,
                    attack.TargetObjectId,
                    resolution.CapturedDamageValue,
                    result: attackSelector,
                    damageType: (byte)resolution.Outcome),
                cancellationToken,
                _session,
                "BasicAttackMissWorld",
                expectedSpawnGeneration: target.SpawnGeneration);
            Console.WriteLine(
                $"[attack] miss character={_character.Name} target={attack.TargetObjectId} event={eventId} hit={resolution.Rolls.HitRollBasisPoints}/{resolution.Rolls.HitChanceBasisPoints} viewers={missViewers}");
            return;
        }

        var requestedDamage = resolution.Damage;
        if (!_registry.TryApplyMonsterDamage(
                _character.CurrentMap,
                attack.TargetObjectId,
                requestedDamage,
                _character.Id,
                target.SpawnGeneration,
                out var damageResult) ||
            damageResult.BeforeHealth == damageResult.AfterHealth)
        {
            Console.WriteLine($"[attack] rejected stale monster character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        var lifeAbsorption = CommitPveLifeAbsorption(
            _character,
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
            ? await PrepareMonsterKillRewardAsync(damageResult)
            : null;
        var elementalRewards =
            await PreparePveElementalKillRewardsAsync(
                elementalAuthority,
                elementalCommit);
        await InterruptPendingSkillCastAsync(
            SkillCastInterruptionReason.Replaced,
            cancellationToken);
        var selfPacket = PacketBuilder.PhysicalDamage(
            LocalPlayerObjectId,
            0f,
            0f,
            0f,
            attack.TargetObjectId,
            resolution.CapturedDamageValue,
            result: attackSelector,
            damageType: (byte)resolution.Outcome);
        var casterNotified = true;
        try
        {
            await _registry.DeliverMonsterHealthPacketToViewerAsync(
                _session,
                _character.CurrentMap,
                attack.TargetObjectId,
                selfPacket,
                damageResult.HealthMutation!.Value,
                cancellationToken,
                "BasicAttackSelf");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[attack] caster notification failed character={_character.Name} target={attack.TargetObjectId}: {ex.Message}");
        }

        var worldObjectId = WorldObjectIds.ForPlayer(_character.Id);
        var viewers = await _registry.BroadcastToMonsterViewersAsync(
            _character.CurrentMap,
            attack.TargetObjectId,
            PacketBuilder.PhysicalDamage(
                worldObjectId,
                0f,
                0f,
                0f,
                attack.TargetObjectId,
                resolution.CapturedDamageValue,
                result: attackSelector,
                damageType: (byte)resolution.Outcome),
            cancellationToken,
            _session,
            "BasicAttackWorld",
            healthMutation: damageResult.HealthMutation);

        await PublishPveLifeAbsorptionAsync(
            _character,
            lifeAbsorption,
            cancellationToken);

        await PublishPveElementalCommitAsync(
            elementalAuthority,
            elementalCommit,
            elementalRewards,
            cancellationToken);

        if (pendingReward is not null)
        {
            await PublishMonsterKillRewardAsync(
                pendingReward,
                cancellationToken);
        }

        Console.WriteLine(
            $"[attack] damage character={_character.Name} target={attack.TargetObjectId} event={eventId} outcome={resolution.Outcome} resolved={requestedDamage} applied={damageResult.BeforeHealth - damageResult.AfterHealth} hp={damageResult.AfterHealth}/{damageResult.Monster.MaximumHealth} killed={damageResult.Killed} caster-notified={casterNotified} viewers={viewers}");
    }

}
