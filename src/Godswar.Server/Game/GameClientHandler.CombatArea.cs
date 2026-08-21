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

        var isGroundTargeted =
            SkillCombatResolver.IsHostileMonsterGroundAreaSkill(combat);
        if (isGroundTargeted && !cast.HasTargetPosition ||
            !SkillCombatResolver.TryResolveHostileMonsterAreaCenter(
                character.PositionX,
                character.PositionZ,
                cast.TargetX,
                cast.TargetZ,
                combat,
                out var areaCenterX,
                out var areaCenterZ))
        {
            Console.WriteLine(
                $"[skill] rejected invalid area target character={character.Name} skill={cast.SkillId}");
            return;
        }

        if (_registry.PlayerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            await HandleHostileMonsterAreaSkillCastEcsAsync(
                packet,
                cast,
                combat,
                areaCenterX,
                areaCenterZ,
                publishCastVisual,
                cancellationToken);
            return;
        }

        if (!RevalidateCurrentWorldEffectOwnership(
                "area_skill_damage"))
        {
            return;
        }

        using var elementalAuthority =
            CapturePveElementalCommitAuthority(character);
        if (elementalAuthority is null)
        {
            Console.WriteLine(
                $"[skill] rejected stale elemental authority character={character.Name} skill={cast.SkillId}");
            return;
        }

        var manaCost = Math.Max(0, combat.Mp);
        var observedAt = DateTimeOffset.UtcNow;
        var manaReserved = TryReserveLegacyHostileSkill(
            character,
            combat,
            observedAt,
            out var currentMana,
            out _,
            out var cooldownRejected);

        if (!manaReserved)
        {
            if (cooldownRejected)
            {
                return;
            }

            Console.WriteLine(
                $"[skill] rejected insufficient MP character={character.Name} skill={cast.SkillId} mp={currentMana} cost={manaCost}");
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "AreaSkillManaRejected");
            return;
        }

        var candidates = _registry.GetMapMonsterSnapshots(
                _session,
                character.CurrentMap)
            .Where(monster =>
                monster.IsSpawned &&
                monster.IsAlive &&
                _registry.IsMonsterVisibleTo(
                    _session,
                    monster.ObjectId,
                    monster.SpawnGeneration) &&
                SkillCombatResolver.IsWithinArea(
                    areaCenterX,
                    areaCenterZ,
                    monster.X,
                    monster.Z,
                    combat))
            .OrderBy(static monster => monster.ObjectId)
            .ToArray();
        var hits = new List<(
            MonsterDamageResult Result,
            uint ReportedDamage,
            ulong CombatEventId)>(candidates.Length);
        var admittedCombatRevision =
            checked((ulong)NextAdmittedLegacyCombatRevision());
        var runtimeCombatModifiers =
            _registry.GetRuntimeStatusAggregate(_session, observedAt);
        var misses = 0;
        for (var targetOrder = 0;
             targetOrder < candidates.Length;
             targetOrder++)
        {
            var candidate = candidates[targetOrder];
            var resolution = ResolveHostileMonsterSkillDamage(
                character,
                combat,
                candidate,
                admittedCombatRevision,
                targetOrder,
                observedAt,
                runtimeCombatModifiers);
            if (!resolution.Hit)
            {
                misses++;
                continue;
            }

            if (resolution.Damage == 0)
            {
                continue;
            }

            if (!RevalidateCurrentWorldEffectOwnership(
                    "area_skill_damage"))
            {
                break;
            }

            if (_registry.TryApplyMonsterDamage(
                    character.CurrentMap,
                    candidate.ObjectId,
                    resolution.Damage,
                    character.Id,
                    candidate.SpawnGeneration,
                    out var damageResult) &&
                damageResult.BeforeHealth != damageResult.AfterHealth)
            {
                // The original protocol reports resolved damage, even if the
                // target had less health remaining.
                hits.Add((
                    damageResult,
                    resolution.CapturedDamageValue,
                    resolution.EventId));
            }
        }

        var lifeAbsorption = CommitPveLifeAbsorption(
            character,
            hits.Select(static hit => new PveCommittedMonsterDamage(
                    hit.CombatEventId,
                    hit.Result.ObjectId,
                    hit.Result.Monster.SpawnGeneration,
                    hit.Result.BeforeHealth - hit.Result.AfterHealth))
                .ToArray());
        var elementalCommit = CommitPveElementalHits(
            elementalAuthority,
            CombatEventProvenance.DirectSkill,
            hits.Select((hit, index) => new PveElementalCommittedHit(
                    hit.CombatEventId,
                    index,
                    hit.Result))
                .ToArray());
        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
        var pendingRewards = new List<PendingMonsterKillReward>();
        foreach (var hit in hits.Where(static hit => hit.Result.Killed))
        {
            var pending = await PrepareMonsterKillRewardAsync(hit.Result);
            if (pending is not null)
            {
                pendingRewards.Add(pending);
            }
        }
        var elementalRewards =
            await PreparePveElementalKillRewardsAsync(
                elementalAuthority,
                elementalCommit);

        var selfVisual = isGroundTargeted
            ? PacketBuilder.SkillCastVisual(
                packet.Buffer,
                LocalPlayerObjectId)
            : PacketBuilder.SelfTargetSkillCastVisual(
                packet.Buffer,
                LocalPlayerObjectId);
        var selfImpact = PacketBuilder.SkillCastImpact(
            LocalPlayerObjectId,
            uint.MaxValue,
            cast.SkillId,
            areaCenterX,
            areaCenterZ);
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

        var worldObjectId = CurrentPlayerObjectId;
        var worldVisual = isGroundTargeted
            ? PacketBuilder.SkillCastVisual(packet.Buffer, worldObjectId)
            : PacketBuilder.SelfTargetSkillCastVisual(
                packet.Buffer,
                worldObjectId);
        var areaRecipients = await _registry.BroadcastMonsterAreaDamageToViewersAsync(
            character.CurrentMap,
            worldVisual,
            PacketBuilder.SkillCastImpact(
                worldObjectId,
                uint.MaxValue,
                cast.SkillId,
                areaCenterX,
                areaCenterZ),
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

        await PublishPveLifeAbsorptionAsync(
            character,
            lifeAbsorption,
            cancellationToken,
            persistVitals: false);

        await PublishPveElementalCommitAsync(
            elementalAuthority,
            elementalCommit,
            elementalRewards,
            cancellationToken);

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
                    $"[skill] area vitals persistence deferred character={character.Name}: {ex.Message}");
            }
        }

        foreach (var pendingReward in pendingRewards)
        {
            await PublishMonsterKillRewardAsync(
                pendingReward,
                cancellationToken);
        }

        var appliedDamage = hits.Aggregate(
            0UL,
            static (total, hit) => total + hit.Result.BeforeHealth - hit.Result.AfterHealth);
        var firstReportedDamage = hits.Count == 0
            ? 0u
            : hits[0].ReportedDamage;
        Console.WriteLine(
            $"[skill] area damage character={character.Name} skill={cast.SkillId} cast-revision={admittedCombatRevision} center={areaCenterX:F2},{areaCenterZ:F2} radius={combat.Range:F2} candidates={candidates.Length} hits={hits.Count} misses={misses} first-resolved={firstReportedDamage} applied-total={appliedDamage} mp={currentMana}/{character.MaxMp} caster-notified={casterNotified} viewers={areaRecipients}");
    }

}
