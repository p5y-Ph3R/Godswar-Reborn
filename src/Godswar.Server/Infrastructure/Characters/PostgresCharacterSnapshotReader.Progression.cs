using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterSnapshotReader
{
    private static async Task<CharacterCalculatedStatsSnapshot>
        ReadCalculatedStatsAsync(
            NpgsqlDataReader reader,
            CancellationToken cancellationToken)
    {
        return await ReadOptionalCalculatedStatsAsync(
                   reader,
                   cancellationToken) ??
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.MissingCalculatedStats,
                "The loaded character has no calculated-stat projection.");
    }

    private static async Task<CharacterCalculatedStatsSnapshot?>
        ReadOptionalCalculatedStatsAsync(
            NpgsqlDataReader reader,
            CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var result = MapCalculatedStats(reader);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "Calculated-stat projection returned duplicate rows.");
        }

        return result;
    }

    private static CharacterCalculatedStatsSnapshot MapCalculatedStats(
        NpgsqlDataReader reader) =>
        new(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetInt32(16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            reader.GetInt32(19),
            reader.GetInt32(20),
            reader.GetInt32(21),
            reader.GetInt32(22),
            reader.GetInt32(23),
            reader.GetInt32(24),
            reader.GetInt32(25),
            reader.GetInt32(26),
            reader.GetInt32(27),
            reader.GetInt32(28),
            reader.GetInt32(29),
            reader.GetInt32(30),
            reader.GetInt16(31),
            reader.GetInt32(32),
            reader.GetInt32(33),
            reader.GetInt16(34),
            reader.GetInt32(35),
            reader.GetInt32(36),
            reader.GetInt32(37),
            reader.GetInt32(38),
            reader.GetInt32(39),
            reader.GetInt32(40),
            reader.GetInt32(41));

    private static async Task<ImmutableArray<CharacterSkillSnapshot>>
        ReadSkillsAsync(
            NpgsqlDataReader reader,
            CancellationToken cancellationToken)
    {
        var rows = ImmutableArray.CreateBuilder<CharacterSkillSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            CheckRowLimit(
                rows.Count,
                CharacterSnapshotLimits.SkillCount,
                "character skills");
            rows.Add(new CharacterSkillSnapshot(
                reader.GetInt32(0),
                reader.GetInt32(1)));
        }

        return rows.ToImmutable();
    }

    private static async Task<ImmutableArray<CharacterTalentSnapshot>>
        ReadTalentsAsync(
            NpgsqlDataReader reader,
            CancellationToken cancellationToken)
    {
        var rows = ImmutableArray.CreateBuilder<CharacterTalentSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            CheckRowLimit(
                rows.Count,
                CharacterSnapshotLimits.TalentCount,
                "character talents");
            rows.Add(CharacterTalentProjection.FromPersistedRank(
                reader.GetInt32(0),
                reader.GetInt32(1)));
        }

        return rows.ToImmutable();
    }

    private static async Task<
        ImmutableArray<CharacterProgressionBoostSnapshot>> ReadBoostsAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var rows =
            ImmutableArray.CreateBuilder<CharacterProgressionBoostSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            CheckRowLimit(
                rows.Count,
                CharacterSnapshotLimits.PersonalBoostCount,
                "personal progression boosts");
            rows.Add(new CharacterProgressionBoostSnapshot(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                ToUtcOffset(reader.GetDateTime(4)),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetString(6)));
        }

        return rows.ToImmutable();
    }

    private static void CheckRowLimit(
        int existingCount,
        int limit,
        string family)
    {
        if (existingCount >= limit)
        {
            throw new CharacterSnapshotUnavailableException(
                CharacterSnapshotFailureReason.BoundsExceeded,
                $"{family} exceeds the {limit}-row snapshot limit.");
        }
    }

    private static readonly string CalculatedStatsQuery =
        PostgresCharacterRuntimeItemProjectionSql
            .CalculatedStatsForCharacter;

    private const string SkillsQuery =
        """
        SELECT
            skills.skill_id,
            GREATEST(1, skills.skill_level)::integer
        FROM character_base character
        JOIN character_skills skills
          ON skills.user_id = character.id
        JOIN gameplay_skill_combat_definitions template
          ON template.skill_id = skills.skill_id
        WHERE character.account_id = @accountId
          AND character.id = @characterId
          AND character.lifecycle_state = 'active'
          AND template.revision = COALESCE(
              @gameplayContentRevision,
              (
                  SELECT publication.revision
                  FROM gameplay_content_publication publication
                  WHERE publication.family = 'gameplay'
              )
          )
          AND character.profession = ANY(template.class_ids)
        ORDER BY skills.skill_id;

        """;

    private const string TalentsQuery =
        """
        SELECT
            template.id,
            COALESCE(talent.rank, 0)::integer
        FROM character_base character
        JOIN gameplay_talent_definitions template
          ON template.class_id = character.profession
        LEFT JOIN character_talents talent
          ON talent.user_id = character.id
         AND talent.talent_id = template.id
        WHERE character.account_id = @accountId
          AND character.id = @characterId
          AND template.revision = COALESCE(
              @gameplayContentRevision,
              (
                  SELECT publication.revision
                  FROM gameplay_content_publication publication
                  WHERE publication.family = 'gameplay'
              )
          )
          AND (
              character.profession <> 1
              OR template.id IN (
                  64, 59, 61, 65, 51, 55, 62, 67, 50,
                  53, 56, 63, 66, 52, 54, 58, 60, 57
              )
          )
        ORDER BY
            CASE
                WHEN character.profession = 1 THEN CASE template.id
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
                ELSE template.tree_order
            END,
            template.id;

        """;

    private const string PersonalBoostsQuery =
        """
        SELECT
            modifier.status_id,
            modifier.kind,
            modifier.bonus_basis_points,
            modifier.priority,
            modifier.activated_at,
            COALESCE(
                modifier.remaining_online_ticks,
                CASE
                    WHEN modifier.expires_at IS NULL THEN NULL
                    ELSE GREATEST(
                        0,
                        ROUND(EXTRACT(EPOCH FROM (
                            modifier.expires_at - modifier.activated_at
                        )) * 10000000)::bigint
                    )
                END
            ),
            modifier.source
        FROM character_experience_modifiers modifier
        JOIN character_base character
          ON character.id = modifier.character_id
         AND character.account_id = @accountId
        WHERE modifier.character_id = @characterId
          AND modifier.activated_at <= @readAt
          AND (
              modifier.expires_at IS NULL
              AND modifier.remaining_online_ticks IS NULL
              OR COALESCE(
                  modifier.remaining_online_ticks,
                  GREATEST(
                      0,
                      ROUND(EXTRACT(EPOCH FROM (
                          modifier.expires_at - modifier.activated_at
                      )) * 10000000)::bigint
                  )
              ) > 0
          )
        ORDER BY modifier.kind;

        """;
}
