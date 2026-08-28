using System.Buffers.Binary;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleRideSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var isRiding = _registry.IsRuntimeStatusActive(
            _session,
            MountCatalog.RuntimeStatusKind,
            now);

        if (isRiding)
        {
            // Dismount is a true toggle, not another intonation cast. Publishing
            // the cleared status makes the client restore the unmounted model
            // immediately. Working-server captures send only that status snapshot:
            // no cast-start visual, cast impact, mana update, cooldown, or delay.
            // This deliberately happens before mount validation so inconsistent
            // equipment state can never trap a character in Ride status.
            if (!_registry.TryGetPlayerLifeRevision(
                    _session,
                    out var dismountLifeRevision))
            {
                Console.WriteLine(
                    $"[mount] rejected dismount without life authority " +
                    $"character={character.Name}");
                return;
            }
            _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
            await DismountMountRideAndPublishAsync(
                _session,
                _registry,
                character,
                dismountLifeRevision,
                cancellationToken);
            return;
        }

        if (HasPendingSkillCast)
        {
            Console.WriteLine(
                $"[mount] rejected Ride while activation is pending character={character.Name}");
            return;
        }

        if (!RequireItemContent().Mounts.TryGetEquippedRideDefinition(
                character,
                out var mount))
        {
            Console.WriteLine(
                $"[mount] rejected Ride without a supported equipped mount character={character.Name} slot={EquipmentSlots.Mount} item={EquipmentSlots.GetItemId(character.Equipment, character.Profession, EquipmentSlots.Mount)}");
            return;
        }

        if (character.Level < mount.MountLevel)
        {
            Console.WriteLine(
                $"[mount] rejected Ride below mount level character={character.Name} level={character.Level} mount={mount.ItemId} required={mount.MountLevel}");
            return;
        }

        if (_nextSkillCastAt.TryGetValue(cast.SkillId, out var nextCastAt) &&
            nextCastAt > now)
        {
            Console.WriteLine(
                $"[mount] rejected Ride cooldown character={character.Name} remaining={(nextCastAt - now).TotalSeconds:F2}");
            return;
        }

        var manaCost = MountCatalog.RideManaCost;
        int currentMana;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
        }

        if (currentMana < manaCost)
        {
            Console.WriteLine(
                $"[mount] rejected Ride insufficient MP character={character.Name} mp={currentMana} cost={manaCost}");
            await SendInsufficientManaRejectionAsync(
                currentMana,
                cancellationToken,
                "RideManaRejected");
            return;
        }

        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
        var targetX = float.IsFinite(cast.TargetX) ? cast.TargetX : character.PositionX;
        var targetZ = float.IsFinite(cast.TargetZ) ? cast.TargetZ : character.PositionZ;
        var worldObjectId = CurrentPlayerObjectId;
        if (!_registry.TryGetPlayerLifeRevision(
                _session,
                out var activationLifeRevision))
        {
            Console.WriteLine(
                $"[mount] rejected Ride without life authority " +
                $"character={character.Name}");
            return;
        }

        var visualRecipients = 0;
        var started = await TryBeginPendingSkillCastAsync(
            cast.SkillId,
            MountCatalog.RideCastTime,
            "ride",
            async token =>
            {
                await _session.SendAsync(
                    PacketBuilder.SelfTargetSkillCastVisual(
                        packet.Buffer,
                        LocalPlayerObjectId),
                    token,
                    "RideSkillCastSelf");
                visualRecipients = await _registry.BroadcastToMapAsync(
                    character.CurrentMap,
                    PacketBuilder.SelfTargetSkillCastVisual(
                        packet.Buffer,
                        worldObjectId),
                    token,
                    _session,
                    "RideSkillCastWorld");
            },
            token => CompleteRideActivationAsync(
                character.Id,
                character.Name,
                mount,
                cast.SkillId,
                targetX,
                targetZ,
                worldObjectId,
                activationLifeRevision,
                visualRecipients,
                token),
            cancellationToken,
            () => IsRideActivationStillValid(
                character.Id,
                mount));
        if (!started)
        {
            Console.WriteLine(
                $"[mount] rejected Ride while another intonation is " +
                $"pending character={character.Name}");
        }
    }

    private async Task HandlePlayerStateActionAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null || !IsRideDismountRequest(packet.Buffer))
        {
            Console.WriteLine(
                $"[mount] ignored unsupported player-state action character={character?.Name ?? "<none>"} len={packet.Length}");
            return;
        }

        if (!_registry.IsRuntimeStatusActive(
                _session,
                MountCatalog.RuntimeStatusKind,
                DateTimeOffset.UtcNow))
        {
            Console.WriteLine(
                $"[mount] ignored Ride cancellation while inactive character={character.Name}");
            return;
        }

        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
        if (!_registry.TryGetPlayerLifeRevision(
                _session,
                out var lifeRevision))
        {
            Console.WriteLine(
                $"[mount] rejected cancellation without life authority " +
                $"character={character.Name}");
            return;
        }
        await DismountMountRideAndPublishAsync(
            _session,
            _registry,
            character,
            lifeRevision,
            cancellationToken);
    }

    internal static bool IsRideDismountRequest(ReadOnlySpan<byte> packet)
    {
        const int packetLength = 20;
        const uint rideCancellationAction = 6;
        return packet.Length == packetLength &&
               BinaryPrimitives.ReadUInt16LittleEndian(packet[..2]) == packetLength &&
               BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(2, 2)) == Opcodes.PlayerStateAction &&
               BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8, 4)) == rideCancellationAction;
    }

    private async Task CompleteRideActivationAsync(
        int characterId,
        string characterName,
        MountRideDefinition mount,
        uint skillId,
        float targetX,
        float targetZ,
        uint worldObjectId,
        long expectedLifeRevision,
        int visualRecipients,
        CancellationToken cancellationToken)
    {
        var activation =
            await _registry.TryActivateMountRideAndPublishAsync(
                _session,
                characterId,
                expectedLifeRevision,
                mount,
                DateTimeOffset.UtcNow,
                cancellationToken);
        if (activation is null)
        {
            var currentMana = 0;
            var currentCharacter = _character;
            if (currentCharacter is not null)
            {
                lock (currentCharacter.VitalsSync)
                {
                    currentMana = currentCharacter.CurrentMp;
                }
            }

            Console.WriteLine(
                $"[mount] Ride completion rejected by character, " +
                $"life, mount, or MP state character={characterName} " +
                $"mount={mount.ItemId}");
            if (currentMana < MountCatalog.RideManaCost)
            {
                await SendInsufficientManaRejectionAsync(
                    currentMana,
                    cancellationToken,
                    "RideManaCompletionRejected");
            }
            else
            {
                await SendSkillCastRejectionInterruptAsync(
                    cancellationToken,
                    "RideCompletionRejected");
            }
            return;
        }

        _nextSkillCastAt[skillId] =
            DateTimeOffset.UtcNow + MountCatalog.RideCooldown;
        var character = activation.Value.Character;
        if (!ReferenceEquals(_character, character))
        {
            InstallUpdatedCharacter(character);
        }

        await PublishRideActivationCompletionAsync(
            character,
            mount,
            skillId,
            targetX,
            targetZ,
            worldObjectId,
            activated: true,
            activation.Value.CurrentMana,
            activation.Value.StatusChanged,
            visualRecipients,
            cancellationToken);
    }

    private bool IsRideActivationStillValid(
        int characterId,
        MountRideDefinition expectedMount)
    {
        var character = _character;
        if (character is null ||
            character.Id != characterId ||
            character.Level < expectedMount.MountLevel ||
            _registry.IsRuntimeStatusActive(
                _session,
                MountCatalog.RuntimeStatusKind,
                DateTimeOffset.UtcNow) ||
            !RequireItemContent().Mounts.TryGetEquippedRideDefinition(
                character,
                out var equippedMount) ||
            equippedMount.ItemId != expectedMount.ItemId)
        {
            return false;
        }

        lock (character.VitalsSync)
        {
            return character.CurrentHp > 0;
        }
    }

    internal static async Task<bool> DismountMountRideAndPublishAsync(
        ClientSession session,
        GameSessionRegistry registry,
        GameCharacter character,
        long expectedLifeRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(character);

        var statusChanged = await registry.RemovePersistentRuntimeStatusForLifeRevisionAndPublishAsync(
            session,
            expectedLifeRevision,
            MountCatalog.RuntimeStatusKind,
            DateTimeOffset.UtcNow,
            "mount-dismount",
            cancellationToken);

        var mountItemId = EquipmentSlots.GetItemId(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Mount);
        Console.WriteLine(
            $"[mount] Ride toggled character={character.Name} active=False mount={mountItemId} changed={statusChanged} packets=10167+10166");
        return statusChanged;
    }

    private async Task PublishRideActivationCompletionAsync(
        GameCharacter character,
        MountRideDefinition mount,
        uint skillId,
        float targetX,
        float targetZ,
        uint worldObjectId,
        bool activated,
        int currentMana,
        bool statusChanged,
        int visualRecipients,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.SkillCastImpact(
                LocalPlayerObjectId,
                LocalPlayerObjectId,
                skillId,
                targetX,
                targetZ),
            cancellationToken,
            "RideSkillImpactSelf");
        var impactRecipients = await _registry.BroadcastToMapAsync(
            character.CurrentMap,
            PacketBuilder.SkillCastImpact(
                worldObjectId,
                worldObjectId,
                skillId,
                targetX,
                targetZ),
            cancellationToken,
            _session,
            "RideSkillImpactWorld");

        if (activated)
        {
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "RideManaSelf");
            await _registry.BroadcastToMapAsync(
                character.CurrentMap,
                PacketBuilder.PlayerManaUpdate(worldObjectId, currentMana),
                cancellationToken,
                _session,
                "RideManaWorld");
        }

        if (_account is not null && activated)
        {
            try
            {
                lock (character.VitalsSync)
                {
                    currentMana = character.CurrentMp;
                }

                await PersistVitalsCheckpointAsync(
                    character,
                    force: false,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[mount] Ride vitals persistence deferred character={character.Name}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[mount] Ride toggled character={character.Name} active={activated} mount={mount.ItemId} status={mount.StatusId} speed={1f + mount.SpeedBonus:R} changed={statusChanged} mp={currentMana}/{character.MaxMp} viewers={Math.Max(visualRecipients, impactRecipients)}");
    }
}
