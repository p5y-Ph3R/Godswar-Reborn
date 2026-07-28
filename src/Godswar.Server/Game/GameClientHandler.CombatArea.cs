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
    private async Task HandleHostileMonsterAreaSkillCastAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        bool publishCastVisual,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        if (_registry.PlayerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            await HandleHostileMonsterAreaSkillCastEcsAsync(
                packet,
                cast,
                combat,
                publishCastVisual,
                cancellationToken);
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
                "AreaSkillManaRejected");
            return;
        }

        var requestedDamage = SkillCombatResolver.CalculateDamage(character, combat);
        var candidates = _registry.GetMapMonsterSnapshots(character.CurrentMap)
            .Where(monster =>
                monster.IsSpawned &&
                monster.IsAlive &&
                _registry.IsMonsterVisibleTo(
                    _session,
                    monster.ObjectId,
                    monster.SpawnGeneration) &&
                SkillCombatResolver.IsWithinArea(
                    character.PositionX,
                    character.PositionZ,
                    monster.X,
                    monster.Z,
                    combat))
            .OrderBy(static monster => monster.ObjectId)
            .ToArray();
        var hits = new List<(MonsterDamageResult Result, uint ReportedDamage)>(candidates.Length);
        if (requestedDamage > 0)
        {
            foreach (var candidate in candidates)
            {
                if (_registry.TryApplyMonsterDamage(
                        character.CurrentMap,
                        candidate.ObjectId,
                        requestedDamage,
                        character.Id,
                        candidate.SpawnGeneration,
                        out var damageResult) &&
                    damageResult.BeforeHealth != damageResult.AfterHealth)
                {
                    // The original protocol reports resolved damage, even if the
                    // target had less health remaining.
                    hits.Add((damageResult, requestedDamage));
                }
            }
        }

        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);

        var selfVisual = PacketBuilder.SelfTargetSkillCastVisual(
            packet.Buffer,
            LocalPlayerObjectId);
        var selfImpact = PacketBuilder.SkillCastImpact(
            LocalPlayerObjectId,
            uint.MaxValue,
            cast.SkillId,
            character.PositionX,
            character.PositionZ);
        var selfCluster = PacketBuilder.SkillClusterDamage(
            LocalPlayerObjectId,
            cast.SkillId,
            hits.Select(static hit => new SkillClusterDamageEntry(
                    hit.Result.ObjectId,
                    hit.ReportedDamage))
                .ToArray());

        var casterNotified = true;
        try
        {
            if (publishCastVisual)
            {
                await _session.SendAsync(
                    selfVisual,
                    cancellationToken,
                    "AreaSkillCastSelf");
            }
            await _session.SendAsync(selfImpact, cancellationToken, "AreaSkillImpactSelf");
            if (hits.Count == 0)
            {
                await _session.SendAsync(selfCluster, cancellationToken, "AreaSkillDamageSelf");
            }
            else
            {
                await _registry.DeliverMonsterAreaDamageToViewerAsync(
                    _session,
                    character.CurrentMap,
                    LocalPlayerObjectId,
                    cast.SkillId,
                    hits.Select(static hit => new MonsterAreaDamageBroadcastHit(
                            hit.Result.HealthMutation!.Value,
                            hit.ReportedDamage))
                        .ToArray(),
                    cancellationToken,
                    "AreaSkillSelf");
            }
            if (manaCost > 0)
            {
                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                    cancellationToken,
                    "AreaSkillManaSelf");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[skill] area caster notification failed character={character.Name} skill={cast.SkillId}: {ex.Message}");
        }

        var worldObjectId = WorldObjectIds.ForPlayer(character.Id);
        var areaRecipients = await _registry.BroadcastMonsterAreaDamageToViewersAsync(
            character.CurrentMap,
            PacketBuilder.SelfTargetSkillCastVisual(packet.Buffer, worldObjectId),
            PacketBuilder.SkillCastImpact(
                worldObjectId,
                uint.MaxValue,
                cast.SkillId,
                character.PositionX,
                character.PositionZ),
            worldObjectId,
            cast.SkillId,
            hits.Select(static hit => new MonsterAreaDamageBroadcastHit(
                    hit.Result.HealthMutation!.Value,
                    hit.ReportedDamage))
                .ToArray(),
            cancellationToken,
            _session,
            "AreaSkill",
            publishCastVisual);

        foreach (var hit in hits)
        {
            if (hit.Result.Killed)
            {
                await AwardMonsterKillAsync(hit.Result, cancellationToken);
            }
        }

        if (_account is not null)
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
                    $"[skill] area vitals persistence deferred character={character.Name}: {ex.Message}");
            }
        }

        var appliedDamage = hits.Aggregate(
            0UL,
            static (total, hit) => total + hit.Result.BeforeHealth - hit.Result.AfterHealth);
        Console.WriteLine(
            $"[skill] area damage character={character.Name} skill={cast.SkillId} radius={combat.Range:F2} candidates={candidates.Length} hits={hits.Count} resolved-each={requestedDamage} applied-total={appliedDamage} mp={currentMana}/{character.MaxMp} caster-notified={casterNotified} viewers={areaRecipients}");
    }

}
