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
    private async Task BroadcastEquipmentRefreshAsync(string reason, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var objectId = CurrentPlayerObjectId;
        var statusSnapshot = await _registry.GetStatusSnapshotAsync(
            _session,
            DateTimeOffset.UtcNow,
            cancellationToken);
        var recipients = await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            PacketBuilder.PlayerWorldSpawn(
                _character,
                objectId,
                statusSnapshot.Effects,
                pkMode: _registry.TrainingDummySpawnPkMode(_character)),
            cancellationToken,
            _session,
            "PlayerWorldSpawnRefresh");

        if (recipients > 0)
        {
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.EquipmentVisualRefresh(
                    _character,
                    objectId,
                    _itemContent?.FashionAppearances),
                cancellationToken,
                _session,
                "PlayerEquipmentVisualRefresh");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.EquipmentEffectVisibility(
                    objectId,
                    ResolveEquipmentEffectProjection(_character)),
                cancellationToken,
                _session,
                "PlayerEquipmentEffectVisibility");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerAppearanceExtras(_character, objectId),
                cancellationToken,
                _session,
                "PlayerAppearanceExtrasRefresh");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerTitleInfo(_character, objectId),
                cancellationToken,
                _session,
                "PlayerTitleInfoRefresh");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerInspectEquipmentRemoteStatusBundle(
                    _character,
                    objectId,
                    statusSnapshot.Aggregate,
                    _registry.TrainingDummySpawnPkMode(
                        _character)),
                cancellationToken,
                _session,
                "PlayerInspectEquipmentStatusBroadcast",
                framed: false);
        }

        if (recipients > 0)
        {
            Console.WriteLine(
                $"[world] broadcast equipment refresh reason={reason} map={_character.CurrentMap} character={_character.Name} object={objectId} recipients={recipients} equipment={PacketBuilder.EnterEquipmentSummary(_character)}");
        }
    }

    private async Task BroadcastPlayerLeaveAsync(CancellationToken cancellationToken)
    {
        if (_character is null ||
            _account is null ||
            !TryGetCharacterOwnership(
                _character,
                out var ownership) ||
            !_registry.IsCurrentWorldOwnership(
                _session,
                _account.Id,
                _character.Id,
                ownership))
        {
            return;
        }

        var objectId = CurrentPlayerObjectId;
        var recipients = await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            PacketBuilder.RemoveWorldObjects(objectId),
            cancellationToken,
            _session,
            "WorldObjectRemove");

        if (recipients > 0)
        {
            Console.WriteLine(
                $"[world] broadcast leave map={_character.CurrentMap} character={_character.Name} object={objectId} recipients={recipients}");
        }
    }

    private async Task SendMapPlayersAsync(CancellationToken cancellationToken)
    {
        if (_character is null || _worldPresenceAnnounced)
        {
            return;
        }

        var sentWorldRevisions = new Dictionary<uint, long>();
        var initialPlayers = _registry.GetMapSessions(_character.CurrentMap, _session);
        foreach (var player in initialPlayers)
        {
            await SendVisiblePlayerAsync(player, "initial", cancellationToken);
            sentWorldRevisions[player.ObjectId] = player.WorldRevision;
        }

        if (!_registered)
        {
            JoinCurrentWorld(worldReady: false);
            _registered = true;
        }

        // Reconcile the handoff after joining. A player that entered while the
        // initial snapshot was being sent would otherwise be absent, while one
        // that left before registration would remain as a ghost on this client.
        var currentPlayers = _registry.GetMapSessions(_character.CurrentMap, _session);
        foreach (var player in currentPlayers)
        {
            if (sentWorldRevisions.TryGetValue(player.ObjectId, out var sentRevision) &&
                sentRevision == player.WorldRevision)
            {
                continue;
            }

            await SendVisiblePlayerAsync(player, "reconcile", cancellationToken);
            sentWorldRevisions[player.ObjectId] = player.WorldRevision;
        }

        // Activation is atomic with respect to map joins. If another session
        // became ready during the snapshot send, keep this one hidden until its
        // spawn bundle has also been delivered. A session joining after the
        // successful flip sees this player and announces itself normally.
        while (!_registry.TryMarkWorldReady(
                   _session,
                   sentWorldRevisions,
                   out var unseenPlayers))
        {
            if (unseenPlayers.Count == 0)
            {
                throw new InvalidOperationException("Cannot activate an unregistered world session.");
            }

            foreach (var player in unseenPlayers)
            {
                if (sentWorldRevisions.TryGetValue(player.ObjectId, out var sentRevision) &&
                    sentRevision == player.WorldRevision)
                {
                    continue;
                }

                await SendVisiblePlayerAsync(player, "activation-reconcile", cancellationToken);
                sentWorldRevisions[player.ObjectId] = player.WorldRevision;
            }
        }

        // The initial monster snapshot is committed before this session becomes
        // WorldReady, so its generation or runtime health/state may drift during
        // bootstrap without a live broadcast. Force one ordered remove + fresh
        // appearance at activation; normal AOI updates remain incremental after it.
        await RefreshNearbyWorldObjectsAsync(
            "activation-reconcile",
            cancellationToken,
            forceMonsterRefresh: true);

        // Position changes deliberately do not invalidate the durable-state
        // barrier. Send one current position after activation so movement that
        // occurred while this session was hidden is not lost. Subsequent movement
        // broadcasts remain serialized with this handoff by the session send lock.
        var activationPlayers = _registry.GetMapSessions(_character.CurrentMap, _session);
        foreach (var player in activationPlayers)
        {
            if (!sentWorldRevisions.ContainsKey(player.ObjectId))
            {
                continue;
            }

            await _session.SendAsync(
                PacketBuilder.PlayerWorldPosition(player.Character, player.ObjectId),
                cancellationToken,
                "VisiblePlayerActivationPosition");
        }

        // Re-snapshot after the position sends. If a player disconnected during
        // the loop, its normal remove may have preceded a queued position packet;
        // this final remove is therefore guaranteed to be the last handoff event.
        var finalPlayers = _registry.GetMapSessions(_character.CurrentMap, _session);
        var currentObjectIds = finalPlayers
            .Select(player => player.ObjectId)
            .ToHashSet();
        var staleObjectIds = sentWorldRevisions.Keys
            .Where(objectId => !currentObjectIds.Contains(objectId))
            .ToArray();
        if (staleObjectIds.Length > 0)
        {
            await _session.SendAsync(
                PacketBuilder.RemoveWorldObjects(staleObjectIds),
                cancellationToken,
                "VisiblePlayerReconcileRemove");
        }

        var objectId = CurrentPlayerObjectId;
        var statusSnapshot = await _registry.GetStatusSnapshotAsync(
            _session,
            DateTimeOffset.UtcNow,
            cancellationToken);
        // A dead remote-player presentation can outlive its first ordered
        // world removal. Reassert retirement immediately before reusing the
        // dummy's stable object ID so the stock client cannot carry that
        // presentation into the replacement spawn.
        if (_registry.IsTrainingDummy(_character))
        {
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.RemoveWorldObjects(objectId),
                cancellationToken,
                _session,
                "TrainingDummyPreSpawnReset");
        }
        var spawnRecipients = await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            PacketBuilder.PlayerWorldSpawn(
                _character,
                objectId,
                statusSnapshot.Effects,
                pkMode: _registry.TrainingDummySpawnPkMode(_character)),
            cancellationToken,
            _session);
        if (spawnRecipients > 0)
        {
            Console.WriteLine(
                $"[world] announcing player to map character={_character.Name} object={objectId} wr={_character.WeaponRank}/aura{_character.WeaponAuraEffect} ar={_character.ArmorRank}/aura{_character.ArmorAuraEffect} equipment={PacketBuilder.EnterEquipmentSummary(_character)} recipients={spawnRecipients}");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.EquipmentVisualRefresh(
                    _character,
                    objectId,
                    _itemContent?.FashionAppearances),
                cancellationToken,
                _session);
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.EquipmentEffectVisibility(
                    objectId,
                    ResolveEquipmentEffectProjection(_character)),
                cancellationToken,
                _session,
                "PlayerEquipmentEffectVisibility");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerAppearanceExtras(_character, objectId),
                cancellationToken,
                _session);
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerTitleInfo(_character, objectId),
                cancellationToken,
                _session);
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerWorldPosition(_character, objectId),
                cancellationToken,
                _session);
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.RemotePlayerStatusUpdate(
                    _character,
                    objectId,
                    statusSnapshot.Aggregate,
                    _registry.TrainingDummySpawnPkMode(
                        _character)),
                cancellationToken,
                _session,
                "VisiblePlayerStatus");
        }

        if (!await PublishPlayerCoordinationOnlineAsync(
                _character.CurrentMap,
                cancellationToken))
        {
            return;
        }
        _worldPresenceAnnounced = true;
        Console.WriteLine(
            $"[world] player presence map={_character.CurrentMap} character={_character.Name} object={objectId} receivedExisting={currentObjectIds.Count} announcedTo={spawnRecipients}");
    }

}
