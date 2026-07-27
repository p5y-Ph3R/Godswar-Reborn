using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const float BackhaulMovementTolerance = 0.05f;

    private readonly CancellationTokenSource _backhaulCastLifetime = new();
    private readonly Dictionary<uint, DateTimeOffset> _nextBackhaulCastAt = [];
    private readonly TimeSpan? _backhaulSkillCastTime;
    private Task? _backhaulCastCompletionTask;
    private int _backhaulCastPending;

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
            !_registry.TryGetMapSessionByCharacterId(
                character.CurrentMap,
                character.Id,
                excludeSession: null,
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

        if (Volatile.Read(ref _backhaulCastPending) != 0)
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
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(
                    LocalPlayerObjectId,
                    currentMana),
                cancellationToken,
                "BackhaulManaRejected");
            return;
        }

        if (Interlocked.CompareExchange(
                ref _backhaulCastPending,
                1,
                0) != 0)
        {
            return;
        }

        _nextBackhaulCastAt[definition.SkillId] =
            now + definition.Cooldown;
        _registry.UpdateCharacter(
            _session,
            character,
            advanceWorldRevision: false);

        var sourceMapId = character.CurrentMap;
        var sourceX = character.PositionX;
        var sourceZ = character.PositionZ;
        var characterId = character.Id;
        var characterName = character.Name;
        var worldObjectId = WorldObjectIds.ForPlayer(characterId);
        var expectedLifeRevision =
            _registry.GetPlayerLifeRevision(_session);

        int visualRecipients;
        try
        {
            await _session.SendAsync(
                PacketBuilder.SelfTargetSkillCastVisual(
                    packet.Buffer,
                    LocalPlayerObjectId),
                cancellationToken,
                "BackhaulSkillCastSelf");
            visualRecipients = await _registry.BroadcastToMapAsync(
                sourceMapId,
                PacketBuilder.SelfTargetSkillCastVisual(
                    packet.Buffer,
                    worldObjectId),
                cancellationToken,
                _session,
                "BackhaulSkillCastWorld");
        }
        catch
        {
            _nextBackhaulCastAt.Remove(definition.SkillId);
            Interlocked.Exchange(ref _backhaulCastPending, 0);
            throw;
        }

        _backhaulCastCompletionTask = CompleteBackhaulCastAsync(
            definition,
            characterId,
            characterName,
            sourceMapId,
            sourceX,
            sourceZ,
            expectedLifeRevision,
            worldObjectId,
            visualRecipients);
    }

    private async Task CompleteBackhaulCastAsync(
        BackhaulSkillDefinition definition,
        int characterId,
        string characterName,
        byte sourceMapId,
        float sourceX,
        float sourceZ,
        long expectedLifeRevision,
        uint worldObjectId,
        int visualRecipients)
    {
        var cancellationToken = _backhaulCastLifetime.Token;
        try
        {
            await Task.Delay(
                _backhaulSkillCastTime ?? definition.CastTime,
                cancellationToken);
            await _characterStateGate.WaitAsync(cancellationToken);
            try
            {
                var character = _character;
                if (!IsBackhaulCastStillValid(
                        character,
                        characterId,
                        sourceMapId,
                        sourceX,
                        sourceZ,
                        expectedLifeRevision))
                {
                    await SendCurrentBackhaulManaAsync(
                        character,
                        cancellationToken,
                        "BackhaulManaInterrupted");
                    Console.WriteLine(
                        $"[backhaul] interrupted by character, life, map, " +
                        $"or movement state character={characterName} " +
                        $"skill={definition.SkillId}");
                    return;
                }

                int currentMana;
                var manaReserved = false;
                lock (character!.VitalsSync)
                {
                    if (character.CurrentHp > 0 &&
                        character.CurrentMp >= definition.ManaCost)
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
                    await _session.SendAsync(
                        PacketBuilder.PlayerManaUpdate(
                            LocalPlayerObjectId,
                            currentMana),
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

                Console.WriteLine(
                    $"[backhaul] transition started character={characterName} " +
                    $"skill={definition.SkillId} " +
                    $"map={sourceMapId}->{definition.TargetMapId} " +
                    $"arrival={definition.TargetX:F2},{definition.TargetZ:F2} " +
                    $"mp={currentMana}/{character.MaxMp} " +
                    $"viewers={Math.Max(visualRecipients, impactRecipients)}");
            }
            finally
            {
                _characterStateGate.Release();
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine(
                $"[backhaul] cast cancelled character={characterName} " +
                $"skill={definition.SkillId}");
        }
        catch (Exception error)
        {
            Console.WriteLine(
                $"[backhaul] cast failed character={characterName} " +
                $"skill={definition.SkillId}: {error.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _backhaulCastPending, 0);
        }
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
            _registry.GetPlayerLifeRevision(_session) !=
                expectedLifeRevision ||
            MathF.Abs(character.PositionX - sourceX) >
                BackhaulMovementTolerance ||
            MathF.Abs(character.PositionZ - sourceZ) >
                BackhaulMovementTolerance ||
            !_registry.TryGetMapSessionByCharacterId(
                sourceMapId,
                characterId,
                excludeSession: null,
                out var context))
        {
            return false;
        }

        return ReferenceEquals(context.Session, _session) &&
               ReferenceEquals(context.Character, character);
    }

    private async Task SendCurrentBackhaulManaAsync(
        GameCharacter? character,
        CancellationToken cancellationToken,
        string label)
    {
        var currentMana = 0;
        if (character is not null)
        {
            lock (character.VitalsSync)
            {
                currentMana = character.CurrentMp;
            }
        }

        await _session.SendAsync(
            PacketBuilder.PlayerManaUpdate(
                LocalPlayerObjectId,
                currentMana),
            cancellationToken,
            label);
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
            int currentHp;
            int currentMp;
            long vitalsRevision;
            lock (character.VitalsSync)
            {
                currentHp = character.CurrentHp;
                currentMp = character.CurrentMp;
                vitalsRevision = character.VitalsRevision;
            }

            await _store.SaveCharacterVitalsAsync(
                _account.Id,
                character.Id,
                currentHp,
                currentMp,
                vitalsRevision,
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
