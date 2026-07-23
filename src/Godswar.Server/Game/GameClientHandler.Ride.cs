using System.Buffers.Binary;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly CancellationTokenSource _rideCastLifetime = new();
    private Task? _rideCastCompletionTask;
    private int _rideCastPending;

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
            _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
            await DismountMountRideAndPublishAsync(
                _session,
                _registry,
                character,
                _registry.GetPlayerLifeRevision(_session),
                cancellationToken);
            return;
        }

        if (Volatile.Read(ref _rideCastPending) != 0)
        {
            Console.WriteLine(
                $"[mount] rejected Ride while activation is pending character={character.Name}");
            return;
        }

        if (!MountCatalog.TryGetEquippedRideDefinition(character, out var mount))
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
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "RideManaRejected");
            return;
        }

        if (Interlocked.CompareExchange(ref _rideCastPending, 1, 0) != 0)
        {
            Console.WriteLine(
                $"[mount] rejected Ride while activation is pending character={character.Name}");
            return;
        }

        _nextSkillCastAt[cast.SkillId] = now + MountCatalog.RideCooldown;

        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
        var targetX = float.IsFinite(cast.TargetX) ? cast.TargetX : character.PositionX;
        var targetZ = float.IsFinite(cast.TargetZ) ? cast.TargetZ : character.PositionZ;
        var worldObjectId = WorldObjectIds.ForPlayer(character.Id);
        var lifeRevision = _registry.GetPlayerLifeRevision(_session);

        int visualRecipients;
        try
        {
            await _session.SendAsync(
                PacketBuilder.SelfTargetSkillCastVisual(packet.Buffer, LocalPlayerObjectId),
                cancellationToken,
                "RideSkillCastSelf");
            visualRecipients = await _registry.BroadcastToMapAsync(
                character.CurrentMap,
                PacketBuilder.SelfTargetSkillCastVisual(packet.Buffer, worldObjectId),
                cancellationToken,
                _session,
                "RideSkillCastWorld");
        }
        catch
        {
            Interlocked.Exchange(ref _rideCastPending, 0);

            throw;
        }

        _rideCastCompletionTask = CompleteRideActivationAsync(
            character.Id,
            character.Name,
            mount,
            cast.SkillId,
            targetX,
            targetZ,
            worldObjectId,
            lifeRevision,
            visualRecipients);
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
        await DismountMountRideAndPublishAsync(
            _session,
            _registry,
            character,
            _registry.GetPlayerLifeRevision(_session),
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
        int visualRecipients)
    {
        var cancellationToken = _rideCastLifetime.Token;
        try
        {
            await Task.Delay(MountCatalog.RideCastTime, cancellationToken);
            await _characterStateGate.WaitAsync(cancellationToken);
            try
            {
                var activation = await _registry.TryActivateMountRideAndPublishAsync(
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
                        $"[mount] Ride interrupted by character, life, mount, or MP state character={characterName} mount={mount.ItemId}");
                    await _session.SendAsync(
                        PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                        cancellationToken,
                        "RideManaInterrupted");
                    return;
                }

                var character = activation.Value.Character;
                if (!ReferenceEquals(_character, character))
                {
                    _character = character;
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
            finally
            {
                _characterStateGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine(
                $"[mount] Ride activation cancelled character={characterName} mount={mount.ItemId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[mount] Ride activation failed character={characterName} mount={mount.ItemId}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _rideCastPending, 0);
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
                int currentHp;
                long vitalsRevision;
                lock (character.VitalsSync)
                {
                    currentHp = character.CurrentHp;
                    currentMana = character.CurrentMp;
                    vitalsRevision = character.VitalsRevision;
                }

                await _store.SaveCharacterVitalsAsync(
                    _account.Id,
                    character.Id,
                    currentHp,
                    currentMana,
                    vitalsRevision,
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
