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

        // Currency-backed in-place revival is not implemented yet. Every valid
        // revive button therefore takes the original free-revival path instead
        // of accepting an unpaid premium revive or leaving the player stuck.
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
        lock (_character.VitalsSync)
        {
            _character.CurrentHp = Math.Max(1, _character.MaxHp / 10);
            _character.CurrentMp = Math.Max(0, _character.MaxMp / 10);
            _character.MarkVitalsChanged();
        }
        _positionDirty = false;
        _lastPositionPersistUtc = DateTime.UtcNow;

        var accountId = _account?.Id ?? _character.AccountId;
        var characterId = _character.Id;
        var mapId = _character.CurrentMap;
        var positionX = _character.PositionX;
        var positionZ = _character.PositionZ;
        await _positionPersistence.AdvanceAndPersistAsync(
            token => _store.SaveCharacterPositionAsync(
                accountId,
                characterId,
                mapId,
                positionX,
                positionZ,
                token),
            cancellationToken);
        int revivedHp;
        int revivedMp;
        long revivedVitalsRevision;
        lock (_character.VitalsSync)
        {
            revivedHp = _character.CurrentHp;
            revivedMp = _character.CurrentMp;
            revivedVitalsRevision = _character.VitalsRevision;
        }

        await _store.SaveCharacterVitalsAsync(
            accountId,
            _character.Id,
            revivedHp,
            revivedMp,
            revivedVitalsRevision,
            cancellationToken);
    }

    private async Task HandleBasicAttackAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[attack] ignored basic attack before character enter");
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
                MonsterCombatResolver.ResolvePlayerBasicAttackRange(target.Definition)))
        {
            Console.WriteLine(
                $"[attack] rejected out-of-range monster character={_character.Name} target={attack.TargetObjectId} player={attackX:F2},{attackZ:F2} monster={target.X:F2},{target.Z:F2}");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextBasicAttackAt)
        {
            Console.WriteLine($"[attack] rejected cooldown character={_character.Name} target={attack.TargetObjectId}");
            return;
        }

        var requestedDamage = MonsterCombatResolver.CalculatePlayerBasicAttack(_character);
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

        _nextBasicAttackAt = now + BasicAttackCooldown;
        var attackSelector = _character.Profession is 2 or 3 ? (byte)5 : (byte)3;
        var selfPacket = PacketBuilder.PhysicalDamage(
            LocalPlayerObjectId,
            0f,
            0f,
            0f,
            attack.TargetObjectId,
            requestedDamage,
            result: attackSelector);
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
                requestedDamage,
                result: attackSelector),
            cancellationToken,
            _session,
            "BasicAttackWorld",
            healthMutation: damageResult.HealthMutation);

        if (damageResult.Killed)
        {
            await AwardMonsterKillAsync(damageResult, cancellationToken);
        }

        Console.WriteLine(
            $"[attack] damage character={_character.Name} target={attack.TargetObjectId} resolved={requestedDamage} applied={damageResult.BeforeHealth - damageResult.AfterHealth} hp={damageResult.AfterHealth}/{damageResult.Monster.MaximumHealth} killed={damageResult.Killed} caster-notified={casterNotified} viewers={viewers}");
    }

}
