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
    private async Task AwardMonsterKillAsync(
        MonsterDamageResult damageResult,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null || !damageResult.Killed)
        {
            return;
        }

        var reward = MonsterRewardCatalog.Resolve(damageResult.Monster, _character.Level);
        if (reward.Experience == 0 && reward.TalentExperience == 0)
        {
            await ActivateWorldBossAreaIfApplicableAsync(
                damageResult,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await SendMonsterDeathProgressionAsync(
                damageResult.ObjectId,
                damageResult.Monster.SpawnGeneration,
                _character.Experience,
                _character.TalentExperience,
                _character.TalentPoints,
                cancellationToken);
            Console.WriteLine(
                $"[reward] no eligible reward character={_character.Name} level={_character.Level} monster={damageResult.ObjectId} tier={damageResult.Monster.Definition.Tier}");
            return;
        }

        var rewardTime = DateTimeOffset.UtcNow;
        ExperienceBoostState experienceBoosts;
        try
        {
            experienceBoosts = await _registry.GetExperienceBoostStateAsync(
                _session,
                _account.Id,
                _character.Id,
                _character.Camp,
                _character.CurrentMap,
                rewardTime,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            experienceBoosts = ExperienceBoostState.Empty;
            Console.WriteLine(
                $"[reward] boost resolution failed character={_character.Name}: {ex.Message}");
        }

        var awardedExperience = experienceBoosts.ApplyTo(reward.Experience);
        var awardedTalentExperience = experienceBoosts.ApplyToTalent(reward.TalentExperience);
        await ActivateWorldBossAreaIfApplicableAsync(
            damageResult,
            rewardTime,
            cancellationToken);

        CharacterProgressionResult? progression;
        try
        {
            progression = await _store.ApplyMonsterKillRewardAsync(
                _account.Id,
                _character.Id,
                awardedExperience,
                awardedTalentExperience,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[reward] persistence failed character={_character.Name} monster={damageResult.ObjectId}: {ex.Message}");
            return;
        }

        if (progression is null)
        {
            Console.WriteLine(
                $"[reward] character missing account={_account.Id} character={_character.Id} monster={damageResult.ObjectId}");
            return;
        }

        Console.WriteLine(
            $"[reward] character={_character.Name} base-exp={reward.Experience} awarded-exp={awardedExperience} exp-bonus-bps={experienceBoosts.TotalBonusBasisPoints} base-talent-exp={reward.TalentExperience} awarded-talent-exp={awardedTalentExperience} talent-bonus-bps={experienceBoosts.TotalTalentBonusBasisPoints} boosts={string.Join(',', experienceBoosts.ActiveBoosts.Select(boost => boost.StatusId))}");

        if (_registry.PlayerRuntimeMode == PlayerRuntimeMode.Ecs)
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

        _character.Level = progression.CurrentLevel;
        _character.Experience = progression.CurrentExperience;
        _character.TalentExperience = progression.CurrentTalentExperience;
        _character.TalentPoints = progression.CurrentTalentPoints;

        if (progression.LevelUps.Count > 0)
        {
            try
            {
                var refreshedStats = await _store.GetCharacterStatsAsync(
                    _account.Id,
                    _character.Id,
                    cancellationToken);
                if (refreshedStats is not null)
                {
                    // The killing skill's MP cost is persisted after this reward
                    // sequence. Refresh derived maxima without restoring the
                    // older database vitals and accidentally refunding that cost.
                    lock (_character.VitalsSync)
                    {
                        var currentHp = _character.CurrentHp;
                        var currentMp = _character.CurrentMp;
                        refreshedStats.ApplyTo(_character);
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

        foreach (var levelUp in progression.LevelUps)
        {
            await _session.SendAsync(
                PacketBuilder.PlayerLevelUp(
                    LocalPlayerObjectId,
                    levelUp.Level,
                    levelUp.NextLevelExperience,
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
                    WorldObjectIds.ForPlayer(_character.Id),
                    levelUp.Level,
                    levelUp.NextLevelExperience,
                    levelUp.CurrentExperience,
                    _character.MaxHp,
                    _character.CurrentHp,
                    _character.MaxMp,
                    _character.CurrentMp),
                cancellationToken,
                _session,
                "MonsterKillLevelUpWorld");
        }

        if (progression.ExperienceGained > 0)
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

        if (progression.TalentExperienceGained > 0)
        {
            await _session.SendAsync(
                PacketBuilder.TalentExperienceGain(progression.TalentExperienceGained),
                cancellationToken,
                "MonsterKillTalentExperience");
        }

        await SendMonsterDeathProgressionAsync(
            damageResult.ObjectId,
            damageResult.Monster.SpawnGeneration,
            progression.CurrentExperience,
            progression.CurrentTalentExperience,
            progression.CurrentTalentPoints,
            cancellationToken);

        if (progression.TalentPointsGained > 0)
        {
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "MonsterKillTalentPointCarry");
        }

        Console.WriteLine(
            $"[reward] kill character={_character.Name} monster={damageResult.ObjectId} tier={damageResult.Monster.Definition.Tier} level={progression.PreviousLevel}->{progression.CurrentLevel} exp=+{progression.ExperienceGained}->{progression.CurrentExperience}/{progression.NextLevelExperience} talent-exp=+{progression.TalentExperienceGained}->{progression.CurrentTalentExperience} talent-points=+{progression.TalentPointsGained}->{progression.CurrentTalentPoints}");
    }

    private async Task ActivateWorldBossAreaIfApplicableAsync(
        MonsterDamageResult damageResult,
        DateTimeOffset killedAt,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !WorldBossCatalog.Default.IsWorldBoss(
                _character.CurrentMap,
                damageResult.Monster.Definition.TemplateKey))
        {
            return;
        }

        var deathToken = $"{_character.CurrentMap}:{damageResult.ObjectId}:{killedAt.UtcTicks}";
        try
        {
            var control = await _store.ActivateWorldBossAreaAsync(
                _character.CurrentMap,
                damageResult.Monster.Definition.TemplateKey,
                _character.Camp,
                killedAt,
                deathToken,
                cancellationToken);
            if (control is null)
            {
                return;
            }

            Console.WriteLine(
                $"[world-boss] area-control map={control.MapId} camp={control.ControllingCamp} boss={control.BossTemplateKey} expires={control.ExpiresAt:O}");
            await _registry.SendExperienceBoostStatusesAsync(
                mapId: control.MapId,
                camp: null,
                reason: "world-boss-control",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine(
                $"[world-boss] area-control activation failed map={_character.CurrentMap} boss={damageResult.Monster.Definition.TemplateKey}: {ex.Message}");
        }
    }

    private async Task SendMonsterDeathProgressionAsync(
        uint monsterObjectId,
        uint monsterSpawnGeneration,
        int currentExperience,
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
                WorldObjectIds.ForPlayer(_character.Id),
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

        var skills = await _store.GetSkillStatesAsync(_account.Id, _character.Id, cancellationToken);
        return skills.Any(skill => skill.SkillId == (int)skillId);
    }

    private async Task BroadcastToCurrentMapAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            Console.WriteLine($"[world] ignored {Opcodes.Name(packet.Opcode)} broadcast before character enter");
            return;
        }

        var outboundPacket = packet.Opcode == Opcodes.Walk
            ? PacketBuilder.PlayerWorldMovement(packet.Buffer.AsSpan(), WorldObjectIds.ForPlayer(_character.Id))
            : packet.Buffer;
        var excludeSelf = packet.Opcode == Opcodes.Walk ? _session : null;
        var recipients = await _registry.BroadcastToMapAsync(
            _character.CurrentMap,
            outboundPacket,
            cancellationToken,
            excludeSelf);

        if (packet.Opcode == Opcodes.Walk && recipients > 0)
        {
            Console.WriteLine($"[world] broadcast walk map={_character.CurrentMap} character={_character.Name} object={WorldObjectIds.ForPlayer(_character.Id)} recipients={recipients}");
        }

        if (packet.Opcode == Opcodes.Talk)
        {
            Console.WriteLine($"[world] broadcast talk map={_character.CurrentMap} character={_character.Name} recipients={recipients}");
        }
    }

}
