using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Networking;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.World;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<PendingMonsterKillReward?>
        PrepareMonsterKillRewardAsync(
            MonsterDamageResult damageResult)
    {
        if (_account is null || _character is null || !damageResult.Killed)
        {
            return null;
        }

        var reward = MonsterRewardCatalog.Resolve(damageResult.Monster, _character.Level);
        var awardedPetExperience =
            MonsterRewardCatalog.ResolvePetExperience(damageResult.Monster);
        if (_registry.TryResolveMedusaMonsterRule(
                _session,
                damageResult,
                out var medusaRule))
        {
            awardedPetExperience = medusaRule.PetExperience;
        }
        var rewardTime = DateTimeOffset.UtcNow;
        var experienceBoosts = ExperienceBoostState.Empty;
        if (reward.Experience > 0 || reward.TalentExperience > 0)
        {
            try
            {
                experienceBoosts =
                    await _registry.GetExperienceBoostStateAsync(
                        _session,
                        _account.Id,
                        _character.Id,
                        _character.Camp,
                        _character.CurrentMap,
                        rewardTime,
                        CancellationToken.None);
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[reward] boost resolution failed character={_character.Name}: {ex.Message}");
            }
        }

        var awardedExperience = experienceBoosts.ApplyTo(reward.Experience);
        var awardedTalentExperience = experienceBoosts.ApplyToTalent(reward.TalentExperience);

        MonsterRewardSettlement? settlement;
        try
        {
            settlement = await SettleMonsterRewardWithImmediateRetryAsync(
                damageResult,
                awardedExperience,
                awardedTalentExperience,
                awardedPetExperience,
                rewardTime);
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[reward] persistence failed character={_character.Name} monster={damageResult.ObjectId}: {ex.Message}");
            return null;
        }

        if (settlement is null)
        {
            Console.WriteLine(
                $"[reward] settlement unavailable account={_account.Id} character={_character.Id} monster={damageResult.ObjectId}");
            return null;
        }

        ApplyMonsterRewardProjection(settlement);
        MonsterLootPresentation? monsterLoot = null;
        try
        {
            monsterLoot = _registry.PrepareMedusaMonsterLoot(
                _session,
                damageResult,
                settlement.DeathEventId,
                rewardTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[loot] preparation failed monster={damageResult.ObjectId}: {ex.Message}");
        }
        var worldBossControl =
            await ActivateWorldBossAreaControlIfApplicableAsync(
                damageResult,
                rewardTime,
                settlement.DeathEventId);
        return new PendingMonsterKillReward(
            damageResult,
            reward,
            experienceBoosts,
            awardedExperience,
            awardedTalentExperience,
            settlement,
            worldBossControl,
            monsterLoot);
    }

    private async Task PublishMonsterKillRewardAsync(
        PendingMonsterKillReward pending,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var damageResult = pending.DamageResult;
        var reward = pending.Reward;
        var experienceBoosts = pending.ExperienceBoosts;
        var awardedExperience = pending.AwardedExperience;
        var awardedTalentExperience = pending.AwardedTalentExperience;
        var settlement = pending.Settlement;
        var progression = settlement.Progression;

        Console.WriteLine(
            $"[reward] character={_character.Name} base-exp={reward.Experience} awarded-exp={awardedExperience} exp-bonus-bps={experienceBoosts.TotalBonusBasisPoints} base-talent-exp={reward.TalentExperience} awarded-talent-exp={awardedTalentExperience} talent-bonus-bps={experienceBoosts.TotalTalentBonusBasisPoints} boosts={string.Join(',', experienceBoosts.ActiveBoosts.Select(boost => boost.StatusId))}");

        if (settlement.IsFirstCommit &&
            _registry.PlayerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            try
            {
                var projection =
                    _registry.ProjectCommittedMonsterKillProgressionEcs(
                        _session,
                        damageResult,
                        progression);
                if (!projection.Applied)
                {
                    Console.WriteLine(
                        $"[reward] ECS progression projection skipped character={_character.Name} monster={damageResult.ObjectId} reason={projection.RejectionReason}");
                }
            }
            catch (Exception ex)
            {
                // Persistence is authoritative. Projection diagnostics must not
                // suppress packets for an already-committed reward.
                Console.WriteLine(
                    $"[reward] ECS progression projection deferred character={_character.Name} monster={damageResult.ObjectId}: {ex.Message}");
            }
        }

        ApplyMonsterRewardProjection(settlement);

        if (settlement.IsFirstCommit &&
            progression.LevelUps.Count > 0)
        {
            try
            {
                var refreshedProjection =
                    await _characterRuntimeProjections
                        .ReadCalculatedStatsAsync(
                            _account.Id,
                            _character.Id,
                            cancellationToken);
                if (refreshedProjection is not null)
                {
                    var refreshedStats =
                        CharacterLoadSnapshotHydrator.MapCalculatedStats(
                            refreshedProjection);
                    // The killing skill's MP cost is persisted after this reward
                    // sequence. Refresh derived maxima without restoring the
                    // older database vitals and accidentally refunding that cost.
                    lock (_character.VitalsSync)
                    {
                        var currentHp = _character.CurrentHp;
                        var currentMp = _character.CurrentMp;
                        refreshedStats.ApplyTo(_character);
                        ApplyElementalPassiveStats(
                            _character,
                            refreshedStats);
                        _character.CurrentHp = Math.Clamp(currentHp, 0, _character.MaxHp);
                        _character.CurrentMp = Math.Clamp(currentMp, 0, _character.MaxMp);
                        _character.MarkVitalsChanged();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    $"[reward] level-up stat refresh deferred character={_character.Name}: {ex.Message}");
            }
        }

        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);

        foreach (var levelUp in settlement.IsFirstCommit
                     ? progression.LevelUps
                     : [])
        {
            var clientExperienceMaximum =
                PlayerExperienceCatalog.GetClientExperienceMaximum(
                    levelUp.Level,
                    _character.FighterLevelSealed);
            await _session.SendAsync(
                PacketBuilder.PlayerLevelUp(
                    LocalPlayerObjectId,
                    levelUp.Level,
                    clientExperienceMaximum,
                    levelUp.CurrentExperience,
                    _character.MaxHp,
                    _character.CurrentHp,
                    _character.MaxMp,
                    _character.CurrentMp),
                cancellationToken,
                "MonsterKillLevelUp");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerLevelUp(
                    CurrentPlayerObjectId,
                    levelUp.Level,
                    clientExperienceMaximum,
                    levelUp.CurrentExperience,
                    _character.MaxHp,
                    _character.CurrentHp,
                    _character.MaxMp,
                    _character.CurrentMp),
                cancellationToken,
                _session,
                "MonsterKillLevelUpWorld");
        }

        if (settlement.IsFirstCommit &&
            progression.ExperienceGained > 0)
        {
            await _session.SendAsync(
                PacketBuilder.ExperienceGain(
                    progression.ExperienceGained,
                    progression.CurrentExperience),
                cancellationToken,
                "MonsterKillExperience");
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "MonsterKillProgressionStatus");
        }

        if (settlement.IsFirstCommit &&
            progression.TalentExperienceGained > 0)
        {
            await _session.SendAsync(
                PacketBuilder.TalentExperienceGain(progression.TalentExperienceGained),
                cancellationToken,
                "MonsterKillTalentExperience");
        }

        await SendMonsterDeathProgressionAsync(
            damageResult.ObjectId,
            damageResult.Monster.SpawnGeneration,
            _character.Experience,
            _character.TalentExperience,
            _character.TalentPoints,
            cancellationToken);

        if (settlement.PetExperience is
                { HasPetProjection: true } petExperience)
        {
            await _session.SendAsync(
                PacketBuilder.PetExperience(
                    petExperience.PetId!.Value,
                    petExperience.TotalExperience!.Value),
                cancellationToken,
                "MonsterKillPetExperience");
        }

        if (pending.MonsterLoot is { Entries.Count: > 0 } loot)
        {
            await _session.SendAsync(
                PacketBuilder.MonsterLoot(
                    loot.MonsterObjectId,
                    loot.Entries),
                cancellationToken,
                "MonsterLootAvailable");
        }

        if (settlement.IsFirstCommit &&
            progression.TalentPointsGained > 0)
        {
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "MonsterKillTalentPointCarry");
        }

        await PublishWorldBossAreaControlAsync(
            pending.WorldBossControl,
            cancellationToken);
        Console.WriteLine(
            $"[reward] kill character={_character.Name} monster={damageResult.ObjectId} death={settlement.DeathEventId:N} durable={settlement.IsDurable} first={settlement.IsFirstCommit} level={progression.PreviousLevel}->{_character.Level} exp=+{(settlement.IsFirstCommit ? progression.ExperienceGained : 0)}->{_character.Experience} talent-exp=+{(settlement.IsFirstCommit ? progression.TalentExperienceGained : 0)}->{_character.TalentExperience} talent-points=+{(settlement.IsFirstCommit ? progression.TalentPointsGained : 0)}->{_character.TalentPoints}");
    }

    private sealed record PendingMonsterKillReward(
        MonsterDamageResult DamageResult,
        MonsterKillReward Reward,
        ExperienceBoostState ExperienceBoosts,
        int AwardedExperience,
        int AwardedTalentExperience,
        MonsterRewardSettlement Settlement,
        FactionAreaExperienceControl? WorldBossControl,
        MonsterLootPresentation? MonsterLoot);

    private async Task<MonsterRewardSettlement?>
        SettleLegacyMonsterRewardAsync(
            Guid deathEventId,
            int awardedExperience,
            int awardedTalentExperience,
            CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return null;
        }

        LegacyPersistenceMetrics.Record(
            LegacyPersistenceOperation.ApplyMonsterKillReward);
        var progression = await _store.ApplyMonsterKillRewardAsync(
            _account.Id,
            _character.Id,
            awardedExperience,
            awardedTalentExperience,
            cancellationToken);
        return progression is null
            ? null
            : new MonsterRewardSettlement(
                deathEventId,
                progression,
                Projection: null,
                IsFirstCommit: true,
                IsDurable: false);
    }

    private async Task<FactionAreaExperienceControl?>
        ActivateWorldBossAreaControlIfApplicableAsync(
        MonsterDamageResult damageResult,
        DateTimeOffset killedAt,
        Guid deathEventId)
    {
        if (_character is null ||
            !_gameplayCatalogs.WorldBosses.IsWorldBoss(
                _character.CurrentMap,
                damageResult.Monster.Definition.TemplateKey))
        {
            return null;
        }

        var deathToken = $"monster-death:{deathEventId:N}";
        try
        {
            var result = await _worldBossAreaControl.ActivateAsync(
                new WorldBossAreaActivation(
                    _character.CurrentMap,
                    damageResult.Monster.Definition.TemplateKey,
                    _character.Camp,
                    killedAt,
                    deathToken),
                CancellationToken.None);
            return !result.IsSuccess || result.Control is null
                ? null
                : FocusedGameplayProjectionCompatibility.ToLegacy(
                    result.Control);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[world-boss] area-control activation failed map={_character.CurrentMap} boss={damageResult.Monster.Definition.TemplateKey}: {ex.Message}");
            return null;
        }
    }

    private async Task PublishWorldBossAreaControlAsync(
        FactionAreaExperienceControl? control,
        CancellationToken cancellationToken)
    {
        if (control is null)
        {
            return;
        }

        Console.WriteLine(
            $"[world-boss] area-control map={control.MapId} camp={control.ControllingCamp} boss={control.BossTemplateKey} expires={control.ExpiresAt:O}");
        try
        {
            await _registry.SendExperienceBoostStatusesAsync(
                mapId: control.MapId,
                camp: null,
                reason: "world-boss-control",
                cancellationToken: cancellationToken,
                routingSession: _session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[world-boss] status refresh deferred map={control.MapId} boss={control.BossTemplateKey}: {ex.Message}");
        }
    }

    private async Task SendMonsterDeathProgressionAsync(
        uint monsterObjectId,
        uint monsterSpawnGeneration,
        long currentExperience,
        int currentTalentExperience,
        int currentTalentPoints,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        await _registry.DeliverMonsterPacketToViewerAsync(
            _session,
            _character.CurrentMap,
            monsterObjectId,
            PacketBuilder.MonsterDeathReward(
                monsterObjectId,
                LocalPlayerObjectId,
                currentExperience,
                currentTalentExperience,
                currentTalentPoints),
            monsterSpawnGeneration,
            cancellationToken,
            "MonsterKillProgressionRefresh");

        await _registry.BroadcastToMonsterViewersAsync(
            _character.CurrentMap,
            monsterObjectId,
            PacketBuilder.MonsterDeathReward(
                monsterObjectId,
                CurrentPlayerObjectId,
                currentExperience,
                currentTalentExperience,
                currentTalentPoints),
            cancellationToken,
            _session,
            "MonsterKillProgressionRefreshWorld",
            expectedSpawnGeneration: monsterSpawnGeneration);
    }

    private async Task<bool> IsSkillLearnedAsync(uint skillId, CancellationToken cancellationToken)
    {
        if (_account is null || _character is null || skillId > int.MaxValue)
        {
            return false;
        }

        return await _characterRuntimeProjections.IsSkillLearnedAsync(
            _account.Id,
            _character.Id,
            checked((int)skillId),
            cancellationToken);
    }

    private async Task BroadcastToCurrentMapAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine($"[world] ignored {Opcodes.Name(packet.Opcode)} broadcast before character enter");
            return;
        }

        if (!RevalidateCurrentWorldEffectOwnership(
                packet.Opcode == Opcodes.Talk
                    ? "chat_broadcast"
                    : "world_broadcast"))
        {
            return;
        }

        var outboundPacket = packet.Opcode == Opcodes.Walk
            ? PacketBuilder.PlayerWorldMovement(packet.Buffer.AsSpan(), CurrentPlayerObjectId)
            : packet.Buffer;
        var recipients =
            await _registry.BroadcastToCurrentWorldInstanceAsync(
            _session,
            outboundPacket,
            cancellationToken,
            includeRoutingSession:
                packet.Opcode != Opcodes.Walk);

        if (packet.Opcode == Opcodes.Walk && recipients > 0)
        {
            Console.WriteLine($"[world] broadcast walk map={_character.CurrentMap} character={_character.Name} object={CurrentPlayerObjectId} recipients={recipients}");
        }

        if (packet.Opcode == Opcodes.Talk)
        {
            Console.WriteLine($"[world] broadcast talk map={_character.CurrentMap} character={_character.Name} recipients={recipients}");
        }
    }

}
