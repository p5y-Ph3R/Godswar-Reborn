using Godswar.Server.Application.Talents;
using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<TalentUpgradeResult?> UpgradeTalentAsync(
        int accountId,
        int characterId,
        int talentId,
        int clientRank,
        int clientTalentPoints,
        CancellationToken cancellationToken = default)
    {
        if (clientRank is < 0 or >= TalentProgression.RankCap ||
            clientTalentPoints < 0)
        {
            return null;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int serverTalentPoints;
        int characterLevel;
        await using (var command = new NpgsqlCommand("""
            SELECT cb."SkillPoint", cb.fighter_job_lv
            FROM character_base cb
            JOIN gameplay_talent_definitions tt
              ON tt.id = @talentId
             AND tt.revision = COALESCE(
                 @gameplayContentRevision,
                 (
                     SELECT publication.revision
                     FROM gameplay_content_publication publication
                     WHERE publication.family = 'gameplay'
                 )
             )
            WHERE cb.account_id = @accountId
              AND cb.id = @characterId
              AND tt.class_id = cb.profession
            FOR UPDATE OF cb;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("talentId", talentId);
            AddGameplayContentRevisionParameter(command);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            serverTalentPoints = reader.GetInt32(0);
            characterLevel = reader.GetInt32(1);
        }

        var baseRank = 0;
        await using (var command = new NpgsqlCommand("""
            SELECT rank
            FROM character_talents
            WHERE user_id = @characterId AND talent_id = @talentId;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("talentId", talentId);

            var scalar = await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is not null)
            {
                baseRank = Convert.ToInt32(scalar);
            }
        }

        // The client rank is an untrusted optimistic precondition. It names
        // one N -> N+1 transition, while rank, cost, and points remain
        // server-owned under the character-row lock.
        var currentRank = baseRank;
        if (currentRank >= TalentProgression.RankCap ||
            currentRank != clientRank)
        {
            return null;
        }

        var requiredPlayerLevel = TalentProgression.CalculateRequiredPlayerLevel(currentRank);
        if (characterLevel < requiredPlayerLevel)
        {
            return null;
        }

        var cost = TalentProgression.CalculateUpgradeCost(currentRank);
        if (serverTalentPoints < cost)
        {
            return null;
        }

        var newRank = currentRank + 1;
        var remainingTalentPoints = serverTalentPoints - cost;

        await using (var command = new NpgsqlCommand("""
            INSERT INTO character_talents (user_id, talent_id, rank, updated_at)
            VALUES (@characterId, @talentId, @rank, now())
            ON CONFLICT (user_id, talent_id) DO UPDATE
            SET rank = EXCLUDED.rank,
                updated_at = now();

            UPDATE character_base
            SET "SkillPoint" = @remainingTalentPoints
            WHERE account_id = @accountId AND id = @characterId;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("talentId", talentId);
            command.Parameters.AddWithValue("rank", (short)Math.Clamp(newRank, short.MinValue, short.MaxValue));
            command.Parameters.AddWithValue("remainingTalentPoints", remainingTalentPoints);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var character = await GetCharacterByIdAsync(characterId, cancellationToken);
        if (character is null)
        {
            return null;
        }

        return new TalentUpgradeResult
        {
            Character = character,
            TalentId = talentId,
            NewRank = newRank,
            Cost = cost,
            RemainingTalentPoints = remainingTalentPoints,
            DisplayValue = TalentProgression.CalculateDisplayValue(newRank)
        };
    }

    public async Task<IReadOnlyList<TalentState>> GetTalentStatesAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        var talents = new List<TalentState>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT tt.id, COALESCE(ct.rank, 0)::integer AS rank
            FROM character_base cb
            JOIN gameplay_talent_definitions tt
              ON tt.class_id = cb.profession
             AND tt.revision = COALESCE(
                 @gameplayContentRevision,
                 (
                     SELECT publication.revision
                     FROM gameplay_content_publication publication
                     WHERE publication.family = 'gameplay'
                 )
             )
            LEFT JOIN character_talents ct ON ct.user_id = cb.id AND ct.talent_id = tt.id
            WHERE cb.account_id = @accountId
              AND cb.id = @characterId
              AND (
                  cb.profession <> 1
                  OR tt.id IN (64, 59, 61, 65, 51, 55, 62, 67, 50, 53, 56, 63, 66, 52, 54, 58, 60, 57)
              )
            ORDER BY
                CASE
                    WHEN cb.profession = 1 THEN CASE tt.id
                        WHEN 64 THEN 0
                        WHEN 59 THEN 1
                        WHEN 61 THEN 2
                        WHEN 65 THEN 3
                        WHEN 51 THEN 4
                        WHEN 55 THEN 5
                        WHEN 62 THEN 6
                        WHEN 67 THEN 7
                        WHEN 50 THEN 8
                        WHEN 53 THEN 9
                        WHEN 56 THEN 10
                        WHEN 63 THEN 11
                        WHEN 66 THEN 12
                        WHEN 52 THEN 13
                        WHEN 54 THEN 14
                        WHEN 58 THEN 15
                        WHEN 60 THEN 16
                        WHEN 57 THEN 17
                        ELSE 999
                    END
                    ELSE tt.tree_order
                END,
                tt.id;
            """, connection);

        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        AddGameplayContentRevisionParameter(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rank = Math.Max(0, reader.GetInt32(1));
            talents.Add(new TalentState
            {
                TalentId = reader.GetInt32(0),
                Rank = rank,
                DisplayValue = TalentProgression.CalculateDisplayValue(rank),
                NextCost = TalentProgression.CalculateUpgradeCost(rank)
            });
        }

        return talents;
    }

    public async Task<IReadOnlyList<SkillState>> GetSkillStatesAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        var skills = new List<SkillState>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT cs.skill_id, GREATEST(1, cs.skill_level)::integer AS skill_level
            FROM character_base cb
            JOIN character_skills cs ON cs.user_id = cb.id
            JOIN gameplay_skill_combat_definitions st
              ON st.skill_id = cs.skill_id
             AND st.revision = COALESCE(
                 @gameplayContentRevision,
                 (
                     SELECT publication.revision
                     FROM gameplay_content_publication publication
                     WHERE publication.family = 'gameplay'
                 )
             )
            WHERE cb.account_id = @accountId
              AND cb.id = @characterId
              AND cb.profession = ANY(st.class_ids)
            ORDER BY cs.skill_id;
            """, connection);

        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        AddGameplayContentRevisionParameter(command);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            skills.Add(new SkillState
            {
                SkillId = reader.GetInt32(0),
                Level = reader.GetInt32(1)
            });
        }

        return skills;
    }

}
