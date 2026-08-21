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
    private async Task HandleHostileMonsterStunSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        MonsterStunSkillDefinition definition,
        uint expectedSpawnGeneration,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        if (!RevalidateCurrentWorldEffectOwnership(
                "stun_skill_effect"))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_nextSkillCastAt.TryGetValue(cast.SkillId, out var nextCastAt) &&
            nextCastAt > now)
        {
            Console.WriteLine(
                $"[skill] rejected cooldown character={character.Name} skill={cast.SkillId} remaining={(nextCastAt - now).TotalSeconds:F2}");
            return;
        }

        var manaCost = Math.Max(0, combat.Mp);
        int currentMana;
        var manaReserved = false;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
            if (currentMana >= manaCost)
            {
                character.CurrentMp = currentMana - manaCost;
                currentMana = character.CurrentMp;
                if (manaCost > 0)
                {
                    character.MarkVitalsChanged();
                }

                manaReserved = true;
            }
        }

        if (!manaReserved)
        {
            Console.WriteLine(
                $"[skill] rejected insufficient MP character={character.Name} skill={cast.SkillId} mp={currentMana} cost={manaCost}");
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "StunSkillManaRejected");
            return;
        }

        if (!_registry.TryApplyMonsterStun(
                character.CurrentMap,
                cast.TargetObjectId,
                character.Id,
                definition.Duration,
                expectedSpawnGeneration,
                now,
                out var stunResult) ||
            !stunResult.Applied)
        {
            lock (character.VitalsSync)
            {
                character.CurrentMp = Math.Min(
                    Math.Max(0, character.MaxMp),
                    (int)Math.Min(int.MaxValue, (long)character.CurrentMp + manaCost));
                if (manaCost > 0)
                {
                    character.MarkVitalsChanged();
                }

                currentMana = character.CurrentMp;
            }

            _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "StunSkillManaRefund");
            Console.WriteLine(
                $"[skill] rejected stale stun target character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        _nextSkillCastAt[cast.SkillId] = now + definition.Cooldown;
        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);

        var monster = stunResult.Monster;
        var worldObjectId = CurrentPlayerObjectId;
        var statusSeconds = checked((uint)Math.Max(1d, Math.Ceiling(definition.Duration.TotalSeconds)));
        var statusPacket = PacketBuilder.WorldObjectStatusEffects(
            cast.TargetObjectId,
            [new ClientStatusEffect(definition.StatusId, statusSeconds)]);
        var casterNotified = true;
        try
        {
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillCastVisual(packet.Buffer, LocalPlayerObjectId),
                monster.SpawnGeneration,
                cancellationToken,
                "StunSkillCastSelf");
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                statusPacket,
                monster.SpawnGeneration,
                cancellationToken,
                "StunStatusSelf");
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillCastImpact(
                    LocalPlayerObjectId,
                    cast.TargetObjectId,
                    cast.SkillId,
                    monster.X,
                    monster.Z),
                monster.SpawnGeneration,
                cancellationToken,
                "StunSkillImpactSelf");
            if (manaCost > 0)
            {
                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                    cancellationToken,
                    "StunSkillManaSelf");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[skill] stun caster notification failed character={character.Name} target={cast.TargetObjectId}: {ex.Message}");
        }

        var visualRecipients = await _registry.BroadcastToMonsterViewersAsync(
            character.CurrentMap,
            cast.TargetObjectId,
            PacketBuilder.SkillCastVisual(packet.Buffer, worldObjectId),
            cancellationToken,
            _session,
            "StunSkillCastWorld",
            expectedSpawnGeneration: monster.SpawnGeneration);
        var statusRecipients = await _registry.BroadcastToMonsterViewersAsync(
            character.CurrentMap,
            cast.TargetObjectId,
            statusPacket,
            cancellationToken,
            _session,
            "StunStatusWorld",
            expectedSpawnGeneration: monster.SpawnGeneration);
        var impactRecipients = await _registry.BroadcastToMonsterViewersAsync(
            character.CurrentMap,
            cast.TargetObjectId,
            PacketBuilder.SkillCastImpact(
                worldObjectId,
                cast.TargetObjectId,
                cast.SkillId,
                monster.X,
                monster.Z),
            cancellationToken,
            _session,
            "StunSkillImpactWorld",
            expectedSpawnGeneration: monster.SpawnGeneration);

        if (_account is not null)
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
                    $"[skill] stun vitals persistence deferred character={character.Name}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[skill] stun character={character.Name} skill={cast.SkillId} target={cast.TargetObjectId} status={definition.StatusId} duration={definition.Duration.TotalSeconds:F0} cooldown={definition.Cooldown.TotalSeconds:F0} status-odds={definition.StatusOdds} mp={currentMana}/{character.MaxMp} caster-notified={casterNotified} viewers={Math.Max(visualRecipients, Math.Max(statusRecipients, impactRecipients))}");
    }

    private async Task HandleBeneficialStatusSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillStatusEffectDefinition definition,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_nextSkillCastAt.TryGetValue(cast.SkillId, out var nextCastAt) &&
            nextCastAt > now)
        {
            Console.WriteLine(
                $"[skill] rejected cooldown character={character.Name} skill={cast.SkillId} remaining={(nextCastAt - now).TotalSeconds:F2}");
            return;
        }

        if (!_gameplayCatalogs.SkillCombat.TryGet(
                definition.SkillId,
                out var combat))
        {
            Console.WriteLine(
                $"[skill] rejected missing self-status combat data character={character.Name} skill={cast.SkillId}");
            return;
        }

        var manaCost = Math.Max(0, combat.Mp);
        int currentMana;
        var manaReserved = false;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
            if (currentMana >= manaCost)
            {
                character.CurrentMp = currentMana - manaCost;
                currentMana = character.CurrentMp;
                if (manaCost > 0)
                {
                    character.MarkVitalsChanged();
                }

                manaReserved = true;
            }
        }

        if (!manaReserved)
        {
            Console.WriteLine(
                $"[skill] rejected insufficient MP character={character.Name} skill={cast.SkillId} mp={currentMana} cost={manaCost}");
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "StatusSkillManaRejected");
            return;
        }

        _nextSkillCastAt[cast.SkillId] = now + definition.Cooldown;
        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);

        var targetX = float.IsFinite(cast.TargetX) ? cast.TargetX : character.PositionX;
        var targetZ = float.IsFinite(cast.TargetZ) ? cast.TargetZ : character.PositionZ;
        var worldObjectId = CurrentPlayerObjectId;

        await _session.SendAsync(
            PacketBuilder.SelfTargetSkillCastVisual(packet.Buffer, LocalPlayerObjectId),
            cancellationToken,
            "StatusSkillCastSelf");
        var visualRecipients = await _registry.BroadcastToMapAsync(
            character.CurrentMap,
            PacketBuilder.SelfTargetSkillCastVisual(packet.Buffer, worldObjectId),
            cancellationToken,
            _session,
            "StatusSkillCastWorld");

        // AddStatus on the working server publishes the complete MSG_STATUS map
        // before MAGIC_PERFORM. The registry composer preserves every active EXP
        // source while adding/replacing this skill's same-kind runtime status.
        var statusTargets = ResolveBeneficialStatusTargets(combat);
        var appliedTargetCount = 0;
        foreach (var statusTarget in statusTargets)
        {
            if (!CanApplyBeneficialStatusTarget(
                    statusTarget,
                    combat))
            {
                continue;
            }

            if (await _registry.ApplyRuntimeStatusAndPublishAsync(
                    statusTarget.Session,
                    definition,
                    now,
                    $"skill-{definition.SkillId}",
                    cancellationToken))
            {
                appliedTargetCount++;
            }
        }

        await _session.SendAsync(
            PacketBuilder.SkillCastImpact(
                LocalPlayerObjectId,
                LocalPlayerObjectId,
                cast.SkillId,
                targetX,
                targetZ),
            cancellationToken,
            "StatusSkillImpactSelf");
        var impactRecipients = await _registry.BroadcastToMapAsync(
            character.CurrentMap,
            PacketBuilder.SkillCastImpact(
                worldObjectId,
                worldObjectId,
                cast.SkillId,
                targetX,
                targetZ),
            cancellationToken,
            _session,
            "StatusSkillImpactWorld");

        if (manaCost > 0)
        {
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "StatusSkillManaSelf");
            await _registry.BroadcastToMapAsync(
                character.CurrentMap,
                PacketBuilder.PlayerManaUpdate(worldObjectId, currentMana),
                cancellationToken,
                _session,
                "StatusSkillManaWorld");
        }

        if (_account is not null)
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
                    $"[skill] self-status vitals persistence deferred character={character.Name}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"[skill] beneficial status character={character.Name} " +
            $"skill={cast.SkillId} status={definition.StatusId} " +
            $"targets={appliedTargetCount}/{statusTargets.Count} " +
            $"duration={definition.Duration.TotalSeconds:F0} " +
            $"mp={currentMana}/{character.MaxMp} " +
            $"viewers={Math.Max(visualRecipients, impactRecipients)}");
    }

    private List<BeneficialStatusTarget> ResolveBeneficialStatusTargets(
        SkillCombatDefinition combat)
    {
        var character = _character!;
        var targets = new List<BeneficialStatusTarget>
        {
            new(_session, IsCaster: true, WorldContext: null)
        };
        if (combat.AffectObj != 3 || combat.Range <= 0f)
        {
            return targets;
        }

        foreach (var context in _registry.GetMapSessions(
                     character.CurrentMap,
                     _session))
        {
            if (!IsLivingFriendlyTarget(
                    character,
                    context.Character) ||
                !SkillCombatResolver.IsWithinArea(
                    character.PositionX,
                    character.PositionZ,
                    context.Character.PositionX,
                    context.Character.PositionZ,
                    combat))
            {
                continue;
            }

            targets.Add(new BeneficialStatusTarget(
                context.Session,
                IsCaster: false,
                context));
        }

        return targets;
    }

    private bool CanApplyBeneficialStatusTarget(
        BeneficialStatusTarget target,
        SkillCombatDefinition combat)
    {
        if (target.IsCaster)
        {
            return RevalidateCurrentWorldEffectOwnership(
                "beneficial_status_target");
        }

        var character = _character!;
        if (target.WorldContext is not { } context ||
            !_registry.IsCurrentWorldSessionSnapshot(
                _session,
                context) ||
            !IsLivingFriendlyTarget(
                character,
                context.Character))
        {
            return false;
        }

        return combat.AffectObj == 3 &&
               SkillCombatResolver.IsWithinArea(
                   character.PositionX,
                   character.PositionZ,
                   context.Character.PositionX,
                   context.Character.PositionZ,
                   combat);
    }

    private readonly record struct BeneficialStatusTarget(
        ClientSession Session,
        bool IsCaster,
        GameSessionContext? WorldContext);

}
