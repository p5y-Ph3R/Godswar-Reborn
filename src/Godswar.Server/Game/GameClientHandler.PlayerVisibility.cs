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
    private async Task SendVisiblePlayerAsync(
        GameSessionContext player,
        string phase,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        await RefreshCharacterStatsAsync(player.Character, player.AccountId, "visible-player", cancellationToken);
        var statusSnapshot = await _registry.GetStatusSnapshotAsync(
            player.Session,
            DateTimeOffset.UtcNow,
            cancellationToken);
        Console.WriteLine(
            $"[world] sending existing player phase={phase} to={_character.Name} existing={player.CharacterName} object={player.ObjectId} x={player.Character.PositionX:F2} z={player.Character.PositionZ:F2} wr={player.Character.WeaponRank}/aura{player.Character.WeaponAuraEffect} ar={player.Character.ArmorRank}/aura{player.Character.ArmorAuraEffect} equipment={PacketBuilder.EnterEquipmentSummary(player.Character)}");
        await _session.SendAsync(
            PacketBuilder.PlayerWorldSpawn(
                player.Character,
                player.ObjectId,
                statusSnapshot.Effects,
                pkMode: _registry.TrainingDummySpawnPkMode(
                    player.Character)),
            cancellationToken,
            "VisiblePlayerSpawn");
        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(
                player.Character,
                player.ObjectId,
                _itemContent?.FashionAppearances),
            cancellationToken,
            "VisiblePlayerEquipment");
        await _session.SendAsync(
            PacketBuilder.EquipmentEffectVisibility(
                player.ObjectId,
                ResolveEquipmentEffectProjection(player.Character)),
            cancellationToken,
            "VisiblePlayerEquipmentEffects");
        await _session.SendAsync(
            PacketBuilder.PlayerAppearanceExtras(player.Character, player.ObjectId),
            cancellationToken,
            "VisiblePlayerAppearanceExtras");
        await _session.SendAsync(
            PacketBuilder.PlayerTitleInfo(player.Character, player.ObjectId),
            cancellationToken,
            "VisiblePlayerTitleInfo");
        await _session.SendAsync(
            PacketBuilder.PlayerWorldPosition(player.Character, player.ObjectId),
            cancellationToken,
            "VisiblePlayerPosition");
        await _session.SendAsync(
            PacketBuilder.RemotePlayerStatusUpdate(
                player.Character,
                player.ObjectId,
                statusSnapshot.Aggregate,
                _registry.TrainingDummySpawnPkMode(
                    player.Character)),
            cancellationToken,
            "VisiblePlayerStatus");
        if (player.PetOwnerMergeActive)
        {
            await _session.SendAsync(
                PacketBuilder.PetOwnerMergeStarted(
                    player.ObjectId,
                    player.PetOwnerMergeAptitude,
                    player.PetOwnerMergeCompletedRebirths),
                cancellationToken,
                "VisiblePlayerPetOwnerMerge");
        }
        await _registry.SendStatusSnapshotToViewerAsync(
            player,
            _session,
            cancellationToken);
    }

    private async Task HandlePlayerDetailRequestAsync(GamePacket request, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[game] ignored PlayerDetailRequest: no active character");
            return;
        }

        if (!TryReadFashionVisibilityRequest(
                request.Payload,
                out var fashionHidden))
        {
            Console.WriteLine(
                $"[fashion] ignored malformed Show visibility packet " +
                $"character={_character.Name} bytes={request.Payload.Length}");
            return;
        }

        var requiresDetailFlow =
            !_playerDetailSent ||
            _characterSnapshotBootstrapPending ||
            IsMapTransitionPending;
        var visibilityChanged =
            HasEquippedFashion(_character) &&
            _character.FashionHidden != fashionHidden;
        var rateLimited =
            visibilityChanged &&
            !TryReserveFashionVisibilityTransition(DateTimeOffset.UtcNow);
        if (rateLimited)
        {
            Console.WriteLine(
                $"[fashion] rate-limited Show visibility transition " +
                $"character={_character.Name}");
            visibilityChanged = false;
        }

        var publishVisibility =
            visibilityChanged && _worldPresenceAnnounced;
        if (visibilityChanged)
        {
            _character.FashionHidden = fashionHidden;
            if (publishVisibility)
            {
                _registry.UpdateCharacter(
                    _session,
                    _character,
                    advanceWorldRevision: true);
            }
        }

        if (requiresDetailFlow && !_characterSnapshotBootstrapPending)
        {
            await RefreshActiveCharacterStatsAsync(
                "player-detail",
                cancellationToken);
        }

        var packet = PacketBuilder.PlayerDetail(_character);
        if (packet.Length == 0)
        {
            Console.WriteLine($"[game] ignored PlayerDetailRequest: no detail template character={_character.Name}");
            return;
        }

        Console.WriteLine(
            $"[game] sending self player detail character={_character.Name} " +
            $"fashionHidden={fashionHidden} " +
            $"level={_character.Level} bytes={packet.Length}");
        await _session.SendAsync(packet, cancellationToken, "PlayerDetail", framed: false);
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "PlayerStatusUpdate");
        await PublishFashionVisibilityIfNeededAsync(
            visibilityChanged,
            publishVisibility,
            rateLimited,
            forceSelfProjection: HasEquippedFashion(_character),
            cancellationToken: cancellationToken);

        if (!requiresDetailFlow)
        {
            return;
        }

        if (await HandleMapTransitionPlayerDetailSentAsync(
                cancellationToken))
        {
            return;
        }

        _playerDetailSent = true;
        await SendPostEnterBootstrapAsync(cancellationToken);
    }

    private async Task SendPostEnterBootstrapAsync(CancellationToken cancellationToken)
    {
        if (_postEnterBootstrapSent
            || !CanSendPostEnterBootstrap(
                _clientReadyReceived,
                _playerDetailSent,
                _enterUiReadyReceived)
            || _account is null
            || _character is null)
        {
            return;
        }

        if (!_characterSnapshotBootstrapPending ||
            _characterLoadSnapshot is null)
        {
            RejectCharacterSnapshot(
                "post_enter",
                "bootstrap_not_loaded");
            return;
        }

        _postEnterBootstrapSent = true;
        var bootstrap = _characterLoadSnapshot;

        var enterBootstrap =
            await _worldContent.ReadEnterBootstrapAsync(cancellationToken);
        var suppressedEnterSyncPackets = 0;
        foreach (var packet in enterBootstrap.Packets)
        {
            if (!CanReplayCapturedPostEnterPacket(packet))
            {
                suppressedEnterSyncPackets++;
                continue;
            }

            await _session.SendAsync(packet, cancellationToken, "SynGameData");
        }

        if (suppressedEnterSyncPackets > 0)
        {
            Console.WriteLine(
                $"[game] suppressed unsafe captured enter packets count={suppressedEnterSyncPackets} " +
                "reason=accepted-quest snapshots are character-specific");
        }

        await SendMapWorldObjectsAsync(cancellationToken);
        await RestorePetPresenceAsync(
            bootstrap.Pets,
            summonCarriedPet: true,
            cancellationToken);

        var skillStates = bootstrap.Skills;
        var talentStates = bootstrap.Talents;
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "PlayerStatusUpdate");
        await SendTalentRankPacketsAsync(skillStates, talentStates, "post-enter", cancellationToken);
        await _session.SendAsync(PacketBuilder.PlayerUnknown10098(0), cancellationToken, "PlayerUnknown10098");
        await _session.SendAsync(PacketBuilder.PlayerUnknown10098(1), cancellationToken, "PlayerUnknown10098");
        await _session.SendAsync(
            PacketBuilder.MedusaDesignationInfo(
                _character.SelectedTitleId,
                _character.OwnedTitleIds),
            cancellationToken,
            "DesignationInfo");

        // Opcode 10357 is the final enter/UI-ready boundary. Publish exactly one
        // complete 10167 snapshot here, after both the local object and UI exist.
        await SendExperienceBoostStatusAsync("post-enter", cancellationToken);
        _characterSnapshotBootstrapPending = false;
    }

    internal static bool CanSendPostEnterBootstrap(
        bool clientReadyReceived,
        bool playerDetailSent,
        bool enterUiReadyReceived)
    {
        return clientReadyReceived && playerDetailSent && enterUiReadyReceived;
    }

    internal static bool CanReplayCapturedPostEnterPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 4)
        {
            return false;
        }

        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]);
        return declaredLength == packet.Length
            && opcode != Opcodes.PlayerAcceptedQuests;
    }

    private async Task HandlePlayerInspectRequestAsync(GamePacket request, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[inspect] ignored PlayerInspectRequest: no active character");
            return;
        }

        var requestedObjectId = request.Payload.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(request.Payload[..4])
            : 0;
        var requestedName = PacketText.ReadFixedAscii(request.Payload, 4, 32);
        if (!TryResolveMapPlayer(requestedObjectId, requestedName, out var target))
        {
            Console.WriteLine(
                $"[inspect] target not found requester={_character.Name} object={requestedObjectId} name={requestedName}");
            return;
        }

        var inspectDetailObjectId = target.ObjectId;
        await RefreshCharacterStatsAsync(target.Character, target.AccountId, "inspect-target", cancellationToken);
        var statusSnapshot = await _registry.GetStatusSnapshotAsync(
            target.Session,
            DateTimeOffset.UtcNow,
            cancellationToken);
        Console.WriteLine(
            $"[inspect] sending target equipment requester={_character.Name} target={target.CharacterName} targetObject={target.ObjectId} equipment={PacketBuilder.EnterEquipmentSummary(target.Character)}");
        await _session.SendAsync(
            PacketBuilder.PlayerInspectEquipmentRemoteStatusBundle(
                target.Character,
                inspectDetailObjectId,
                statusSnapshot.Aggregate,
                _registry.TrainingDummySpawnPkMode(
                    target.Character)),
            cancellationToken,
            "PlayerInspectEquipmentStatusBundle",
            framed: false);
    }

    private async Task HandlePlayerInspectVisualRequestAsync(GamePacket request, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine("[inspect] ignored PlayerInspectVisualRequest: no active character");
            return;
        }

        var requestedObjectId = request.Payload.Length >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(request.Payload[..4])
            : 0;
        if (!TryResolveMapPlayer(requestedObjectId, string.Empty, out var target))
        {
            Console.WriteLine($"[inspect] visual target not found requester={_character.Name} object={requestedObjectId}");
            return;
        }

        await SendPlayerVisualBundleAsync(target, cancellationToken, "PlayerInspectVisual");
    }

    private async Task SendPlayerVisualBundleAsync(
        GameSessionContext target,
        CancellationToken cancellationToken,
        string labelPrefix)
    {
        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(
                target.Character,
                target.ObjectId,
                _itemContent?.FashionAppearances),
            cancellationToken,
            $"{labelPrefix}Equipment");
        await _session.SendAsync(
            PacketBuilder.EquipmentEffectVisibility(
                target.ObjectId,
                ResolveEquipmentEffectProjection(target.Character)),
            cancellationToken,
            $"{labelPrefix}EquipmentEffects");
        await _session.SendAsync(
            PacketBuilder.PlayerAppearanceExtras(target.Character, target.ObjectId),
            cancellationToken,
            $"{labelPrefix}AppearanceExtras");
        await _session.SendAsync(
            PacketBuilder.PlayerTitleInfo(target.Character, target.ObjectId),
            cancellationToken,
            $"{labelPrefix}TitleInfo");
        await _session.SendAsync(
            PacketBuilder.PlayerDetailRefreshAck(target.ObjectId),
            cancellationToken,
            $"{labelPrefix}RefreshAck");
    }

    private bool TryResolveMapPlayer(uint objectId, string characterName, out GameSessionContext target)
    {
        target = default!;
        if (_character is null)
        {
            return false;
        }

        if (objectId != 0
            && _registry.TryGetMapSessionByObjectId(_character.CurrentMap, objectId, _session, out target))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(characterName))
        {
            foreach (var player in _registry.GetMapSessions(_character.CurrentMap, _session))
            {
                if (!string.Equals(player.CharacterName, characterName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                target = player;
                return true;
            }
        }

        return false;
    }

    private Task RefreshActiveCharacterStatsAsync(string reason, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return Task.CompletedTask;
        }

        var accountId = _account?.Id ?? _character.AccountId;
        return RefreshCharacterStatsAsync(_character, accountId, reason, cancellationToken);
    }

    private async Task RefreshCharacterStatsAsync(
        GameCharacter character,
        int accountId,
        string reason,
        CancellationToken cancellationToken)
    {
        CharacterStats? stats;
        if (accountId > 0)
        {
            var projection =
                await _characterRuntimeProjections
                    .ReadCalculatedStatsAsync(
                accountId,
                character.Id,
                cancellationToken);
            stats = projection is null
                ? null
                : CharacterLoadSnapshotHydrator.MapCalculatedStats(
                    projection);
        }
        else
        {
            stats = CharacterStats.FromCharacter(character);
        }

        if (stats is null)
        {
            Console.WriteLine($"[stats] missing character={character.Name} id={character.Id} account={accountId} reason={reason}");
            return;
        }

        stats.ApplyTo(character);
        ApplyElementalPassiveStats(character, stats);
        Console.WriteLine($"[stats] refreshed reason={reason} character={character.Name} {stats.ToLogSummary()}");
    }

    private bool UpdateCharacterPositionFromWalk(
        GamePacket packet,
        out AcceptedMapMovementSegment movement)
    {
        movement = default;
        if (_character is null || packet.Payload.Length < 12)
        {
            return false;
        }

        var previousX = _character.PositionX;
        var previousZ = _character.PositionZ;
        var mapId = _character.CurrentMap;
        var positionX = BinaryPrimitives.ReadSingleLittleEndian(packet.Payload.Slice(4, 4));
        var positionZ = BinaryPrimitives.ReadSingleLittleEndian(packet.Payload.Slice(8, 4));
        if (!WorldSectorVisibilityTracker<NpcSpawnDefinition>.TryGetCell(positionX, positionZ, out _))
        {
            Console.WriteLine(
                $"[world] ignored invalid walk position character={_character.Name} x={positionX} z={positionZ}");
            return false;
        }

        _character.PositionX = positionX;
        _character.PositionZ = positionZ;
        _character.MarkPositionChanged();
        _positionDirty = true;
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);
        movement = new AcceptedMapMovementSegment(
            mapId,
            new MapTraversalPosition(previousX, previousZ),
            new MapTraversalPosition(positionX, positionZ));
        return true;
    }

    private async Task PersistCharacterPositionAsync(bool force, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null || !_positionDirty)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && now - _lastPositionPersistUtc < PositionPersistInterval)
        {
            return;
        }

        var character = _character;
        var characterId = _character.Id;
        var mapId = _character.CurrentMap;
        var x = _character.PositionX;
        var z = _character.PositionZ;
        var revision = _character.PositionRevision;
        try
        {
            var persisted = await PersistPositionCheckpointAsync(
                character,
                mapId,
                x,
                z,
                revision,
                force,
                cancellationToken);
            if (!persisted)
            {
                return;
            }

            if (_character.Id == characterId &&
                _character.CurrentMap == mapId &&
                _character.PositionX == x &&
                _character.PositionZ == z)
            {
                _positionDirty = false;
            }
            _lastPositionPersistUtc = now;
            Console.WriteLine(
                $"[world] saved position character={_character.Name} " +
                $"map={mapId} x={x:F2} z={z:F2} force={force} " +
                $"revision={revision}");
        }
        catch (Exception ex) when (
            !force &&
            (ex is not OperationCanceledException ||
             !cancellationToken.IsCancellationRequested))
        {
            Console.WriteLine(
                "[world] deferred position checkpoint " +
                $"reason={ex.GetType().Name}");
        }
    }

}
