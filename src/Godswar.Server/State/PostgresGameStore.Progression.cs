using Godswar.Server.Game;
using Godswar.Server.Application.Characters;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task SaveCharacterPositionAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE character_base
            SET "Map" = @currentMap,
                "Pos_X" = @positionX,
                "Pos_Z" = @positionZ
            WHERE id = @characterId
              AND account_id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("currentMap", (short)currentMap);
        command.Parameters.AddWithValue("positionX", positionX);
        command.Parameters.AddWithValue("positionZ", positionZ);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveCharacterVitalsAsync(
        int accountId,
        int characterId,
        int currentHp,
        int currentMp,
        long vitalsRevision,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE character_base
            SET "curHP" = GREATEST(0, @currentHp),
                "curMP" = GREATEST(0, @currentMp),
                vitals_revision = @vitalsRevision
            WHERE id = @characterId
              AND account_id = @accountId
              AND vitals_revision < @vitalsRevision;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("currentHp", currentHp);
        command.Parameters.AddWithValue("currentMp", currentMp);
        command.Parameters.AddWithValue("vitalsRevision", vitalsRevision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CharacterProgressionResult?> ApplyMonsterKillRewardAsync(
        int accountId,
        int characterId,
        int experience,
        int talentExperience,
        CancellationToken cancellationToken = default)
    {
        experience = Math.Max(0, experience);
        talentExperience = Math.Max(0, talentExperience);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int previousLevel;
        int previousExperience;
        int previousTalentExperience;
        int previousTalentPoints;
        await using (var command = new NpgsqlCommand("""
            SELECT fighter_job_lv, fighter_job_exp, "SkillExp", "SkillPoint"
            FROM character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            previousLevel = reader.GetInt32(0);
            previousExperience = reader.GetInt32(1);
            previousTalentExperience = reader.GetInt32(2);
            previousTalentPoints = reader.GetInt32(3);
        }

        var fighterProgression = PlayerExperienceCatalog.Apply(
            previousLevel,
            previousExperience,
            experience);
        var accumulatedTalentExperience = checked(previousTalentExperience + talentExperience);
        var talentPointsGained = accumulatedTalentExperience / 100;
        var currentTalentExperience = accumulatedTalentExperience % 100;
        var currentTalentPoints = checked(previousTalentPoints + talentPointsGained);

        await using (var command = new NpgsqlCommand("""
            UPDATE character_base
            SET fighter_job_lv = @level,
                fighter_job_exp = @experience,
                "SkillPoint" = @talentPoints,
                "SkillExp" = @talentExperience
            WHERE id = @characterId
              AND account_id = @accountId;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("level", fighterProgression.Level);
            command.Parameters.AddWithValue("experience", fighterProgression.Experience);
            command.Parameters.AddWithValue("talentPoints", currentTalentPoints);
            command.Parameters.AddWithValue("talentExperience", currentTalentExperience);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new CharacterProgressionResult(
            fighterProgression.ExperienceGained,
            previousLevel,
            fighterProgression.Level,
            fighterProgression.Experience,
            PlayerExperienceCatalog.GetNextLevelExperience(fighterProgression.Level),
            fighterProgression.LevelUps,
            talentExperience,
            currentTalentExperience,
            talentPointsGained,
            currentTalentPoints);
    }

    public async Task<ZodiacLevelUpgradeResult?> UpgradeZodiacLevelAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default)
    {
        var result = await _zodiacLevelStore.UpgradeAsync(
            accountId,
            characterId,
            ownership,
            cancellationToken);
        return result is null
            ? null
            : FocusedGameplayProjectionCompatibility.ToLegacy(result);
    }

}
