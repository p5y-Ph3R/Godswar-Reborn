using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const float BackhaulMovementTolerance = 0.05f;

    private readonly Dictionary<uint, DateTimeOffset> _nextBackhaulCastAt = [];
    private readonly TimeSpan? _backhaulSkillCastTime;

    private async Task HandleBackhaulSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        BackhaulSkillDefinition definition,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        if (character.Camp != definition.RequiredCamp ||
            character.CurrentMap == definition.TargetMapId ||
            !_registered ||
            !_worldPresenceAnnounced ||
            IsMapTransitionPending ||
            !_registry.TryGetCurrentWorldSessionByCharacterId(
                _session,
                character.CurrentMap,
                character.Id,
                out var context) ||
            !ReferenceEquals(context.Session, _session))
        {
            Console.WriteLine(
                $"[backhaul] rejected invalid world state " +
                $"character={character.Name} skill={definition.SkillId} " +
                $"camp={character.Camp} map={character.CurrentMap} " +
                $"target-map={definition.TargetMapId}");
            return;
        }

        if (HasPendingSkillCast)
        {
            Console.WriteLine(
                $"[backhaul] rejected while another return cast is pending " +
                $"character={character.Name} skill={definition.SkillId}");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_nextBackhaulCastAt.TryGetValue(
                definition.SkillId,
                out var nextCastAt) &&
            nextCastAt > now)
        {
            Console.WriteLine(
                $"[backhaul] rejected cooldown character={character.Name} " +
                $"skill={definition.SkillId} " +
                $"remaining={(nextCastAt - now).TotalSeconds:F2}");
            return;
        }

        int currentMana;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
        }

        if (currentMana < definition.ManaCost)
        {
            Console.WriteLine(
                $"[backhaul] rejected insufficient MP " +
                $"character={character.Name} skill={definition.SkillId} " +
                $"mp={currentMana} cost={definition.ManaCost}");
            await SendInsufficientManaRejectionAsync(
                currentMana,
                cancellationToken,
                "BackhaulManaRejected");
            return;
        }

        _registry.UpdateCharacter(
            _session,
            character,
            advanceWorldRevision: false);

        var sourceMapId = character.CurrentMap;
        var sourceX = character.PositionX;
        var sourceZ = character.PositionZ;
        var characterId = character.Id;
        var characterName = character.Name;
        var worldObjectId = CurrentPlayerObjectId;
        if (!_registry.TryGetPlayerLifeRevision(
                _session,
                out var expectedLifeRevision))
        {
            Console.WriteLine(
                $"[backhaul] rejected missing life authority " +
                $"character={character.Name} skill={definition.SkillId}");
            return;
        }

        var visualRecipients = 0;
        var started = await TryBeginPendingSkillCastAsync(
            definition.SkillId,
            _backhaulSkillCastTime ?? definition.CastTime,
            "backhaul",
            async token =>
            {
                await _session.SendAsync(
                    PacketBuilder.SelfTargetSkillCastVisual(
                        packet.Buffer,
                        LocalPlayerObjectId),
                    token,
                    "BackhaulSkillCastSelf");
                visualRecipients =
                    await _registry.BroadcastToMapAsync(
                        sourceMapId,
                        PacketBuilder.SelfTargetSkillCastVisual(
                            packet.Buffer,
                            worldObjectId),
                        token,
                        _session,
                        "BackhaulSkillCastWorld");
            },
            token => CompleteBackhaulCastAsync(
                definition,
                characterName,
                sourceMapId,
                sourceX,
                sourceZ,
                worldObjectId,
                visualRecipients,
                token),
            cancellationToken,
            () => IsBackhaulCastStillValid(
                _character,
                characterId,
                sourceMapId,
                sourceX,
                sourceZ,
                expectedLifeRevision));
        if (!started)
        {
            Console.WriteLine(
                $"[backhaul] rejected while another intonation is " +
                $"pending character={character.Name} " +
                $"skill={definition.SkillId}");
        }
    }

    private async Task CompleteBackhaulCastAsync(
        BackhaulSkillDefinition definition,
        string characterName,
        byte sourceMapId,
        float sourceX,
        float sourceZ,
        uint worldObjectId,
        int visualRecipients,
        CancellationToken cancellationToken)
    {
        // The shared coordinator already revalidated identity, life, map,
        // movement, and control state before atomically claiming completion.
        // A death or status applied after that claim belongs to the next
        // action and must not silently discard this completed return cast.
        var character = _character!;

        int currentMana;
        var manaReserved = false;
        lock (character.VitalsSync)
        {
            if (character.CurrentMp >= definition.ManaCost)
            {
                character.CurrentMp -= definition.ManaCost;
                currentMana = character.CurrentMp;
                if (definition.ManaCost > 0)
                {
                    character.MarkVitalsChanged();
                }
                manaReserved = true;
            }
            else
            {
                currentMana = character.CurrentMp;
            }
        }

        if (!manaReserved)
        {
            await SendInsufficientManaRejectionAsync(
                currentMana,
                cancellationToken,
                "BackhaulManaCompletionRejected");
            Console.WriteLine(
                $"[backhaul] rejected at completion " +
                $"character={characterName} " +
                $"skill={definition.SkillId} mp={currentMana} " +
                $"cost={definition.ManaCost}");
            return;
        }

        _registry.UpdateCharacter(
            _session,
            character,
            advanceWorldRevision: false);
        await _session.SendAsync(
            PacketBuilder.SkillCastImpact(
                LocalPlayerObjectId,
                LocalPlayerObjectId,
                definition.SkillId,
                sourceX,
                sourceZ),
            cancellationToken,
            "BackhaulSkillImpactSelf");
        var impactRecipients =
            await _registry.BroadcastToMapAsync(
                sourceMapId,
                PacketBuilder.SkillCastImpact(
                    worldObjectId,
                    worldObjectId,
                    definition.SkillId,
                    sourceX,
                    sourceZ),
                cancellationToken,
                _session,
                "BackhaulSkillImpactWorld");
        await _session.SendAsync(
            PacketBuilder.PlayerManaUpdate(
                LocalPlayerObjectId,
                currentMana),
            cancellationToken,
            "BackhaulManaSelf");
        await _registry.BroadcastToMapAsync(
            sourceMapId,
            PacketBuilder.PlayerManaUpdate(
                worldObjectId,
                currentMana),
            cancellationToken,
            _session,
            "BackhaulManaWorld");
        await PersistBackhaulVitalsAsync(
            character,
            cancellationToken);

        var transitioned = await TryBeginMapTransitionAsync(
            definition.TargetMapId,
            definition.TargetX,
            definition.TargetZ,
            $"backhaul:{definition.ScriptId}",
            cancellationToken);
        if (!transitioned)
        {
            await RefundBackhaulManaAsync(
                character,
                definition.ManaCost,
                sourceMapId,
                worldObjectId,
                cancellationToken);
            Console.WriteLine(
                $"[backhaul] authoritative transition rejected " +
                $"character={characterName} " +
                $"skill={definition.SkillId}");
            return;
        }

        _nextBackhaulCastAt[definition.SkillId] =
            DateTimeOffset.UtcNow + definition.Cooldown;
        Console.WriteLine(
            $"[backhaul] transition started character={characterName} " +
            $"skill={definition.SkillId} " +
            $"map={sourceMapId}->{definition.TargetMapId} " +
            $"arrival={definition.TargetX:F2},{definition.TargetZ:F2} " +
            $"mp={currentMana}/{character.MaxMp} " +
            $"viewers={Math.Max(visualRecipients, impactRecipients)}");
    }

    private bool IsBackhaulCastStillValid(
        GameCharacter? character,
        int characterId,
        byte sourceMapId,
        float sourceX,
        float sourceZ,
        long expectedLifeRevision)
    {
        if (character is null ||
            character.Id != characterId ||
            character.CurrentHp <= 0 ||
            character.CurrentMap != sourceMapId ||
            IsMapTransitionPending ||
            !_registered ||
            !_worldPresenceAnnounced ||
            !_registry.TryGetPlayerLifeRevision(
                _session,
                out var lifeRevision) ||
            lifeRevision != expectedLifeRevision ||
            MathF.Abs(character.PositionX - sourceX) >
                BackhaulMovementTolerance ||
            MathF.Abs(character.PositionZ - sourceZ) >
                BackhaulMovementTolerance ||
            !_registry.TryGetCurrentWorldSessionByCharacterId(
                _session,
                sourceMapId,
                characterId,
                out var context))
        {
            return false;
        }

        return ReferenceEquals(context.Session, _session) &&
               ReferenceEquals(context.Character, character);
    }

    private async Task RefundBackhaulManaAsync(
        GameCharacter character,
        int manaCost,
        byte sourceMapId,
        uint worldObjectId,
        CancellationToken cancellationToken)
    {
        int currentMana;
        lock (character.VitalsSync)
        {
            character.CurrentMp = Math.Min(
                Math.Max(0, character.MaxMp),
                (int)Math.Min(
                    int.MaxValue,
                    (long)character.CurrentMp + manaCost));
            if (manaCost > 0)
            {
                character.MarkVitalsChanged();
            }
            currentMana = character.CurrentMp;
        }

        _registry.UpdateCharacter(
            _session,
            character,
            advanceWorldRevision: false);
        await PersistBackhaulVitalsAsync(
            character,
            cancellationToken);
        await _session.SendAsync(
            PacketBuilder.PlayerManaUpdate(
                LocalPlayerObjectId,
                currentMana),
            cancellationToken,
            "BackhaulManaRefund");
        await _registry.BroadcastToMapAsync(
            sourceMapId,
            PacketBuilder.PlayerManaUpdate(
                worldObjectId,
                currentMana),
            cancellationToken,
            _session,
            "BackhaulManaRefundWorld");
    }

    private async Task PersistBackhaulVitalsAsync(
        GameCharacter character,
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
        catch (Exception error)
            when (error is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[backhaul] vitals persistence deferred " +
                $"character={character.Name}: {error.Message}");
        }
    }
}
