namespace Godswar.Server.State;

/// <summary>
/// Projects the reviewed, book-backed passive skill families from the
/// one selected carried pet. Recall changes only world presentation; Take is
/// the operation that selects a different passive source.
/// </summary>
internal static class PostgresCharacterPetLearnedSkillProjectionSql
{
    public const string CommonTableExpression =
        """
        pet_learned_skill_stat_values AS (
            SELECT
                pet.user_id,
                CASE curve.effect
                    WHEN 0 THEN 'max_hp'
                    WHEN 2 THEN 'hit'
                    WHEN 4 THEN 'physical_attack'
                    WHEN 19 THEN 'ignore_physical_defense'
                    WHEN 21 THEN 'physical_damage_bonus'
                END AS stat_name,
                CASE curve.effect
                    WHEN 19 THEN step.absolute_value * 10000
                    WHEN 21 THEN step.absolute_value * 10000
                    ELSE step.absolute_value
                END AS stat_value
            FROM public.character_pets pet
            JOIN public.character_pet_skills skill
              ON skill.pet_id = pet.id
             AND skill.is_active
            JOIN public.pet_skill_curve_definitions curve
              ON curve.revision = COALESCE(
                  @petLearnedSkillRevision,
                  (
                      SELECT publication.revision
                      FROM public.pet_skill_content_publication publication
                      WHERE publication.singleton
                  ))
             AND curve.first_runtime_skill_id = skill.skill_id
             AND curve.priority = skill.skill_rank
             AND curve.family_type IN (408, 412, 413, 419, 423)
             AND curve.effect IN (0, 2, 4, 19, 21)
            JOIN LATERAL (
                SELECT candidate.absolute_value
                FROM public.pet_skill_curve_steps candidate
                WHERE candidate.revision = curve.revision
                  AND candidate.family_type = curve.family_type
                  AND candidate.priority = curve.priority
                  AND candidate.minimum_pet_rank::numeric <= pet.rank
                ORDER BY candidate.minimum_pet_rank DESC
                LIMIT 1
            ) step ON true
            WHERE pet.user_id = @characterId
              AND pet.activity_state = 'owned'
              AND pet.is_carried
        ),
        """;
}
