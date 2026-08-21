namespace Godswar.Server.State;

/// <summary>
/// Projects only bonuses owned by the one authoritative merged pet. Rows may
/// remain as historical evidence after an unmerge, but they cannot affect a
/// character unless character_pets.contributes_to_character is true.
/// </summary>
internal static class PostgresCharacterPetOwnerMergeProjectionSql
{
    public const string CommonTableExpression =
        """
        pet_owner_merge_stat_values AS (
            SELECT
                pet.user_id,
                CASE bonus.effect_code
                    WHEN 0 THEN 'max_hp'
                    WHEN 1 THEN 'max_mp'
                    WHEN 2 THEN 'hit'
                    WHEN 3 THEN 'dodge'
                    WHEN 4 THEN 'physical_attack'
                    WHEN 5 THEN 'physical_defense'
                    WHEN 6 THEN 'magic_attack'
                    WHEN 7 THEN 'magic_defense'
                    WHEN 10 THEN 'damage_absorb'
                    WHEN 23 THEN 'physical_append_damage'
                    WHEN 24 THEN 'magic_append_damage'
                    WHEN 29 THEN 'physical_flat_absorption'
                    WHEN 30 THEN 'magic_flat_absorption'
                    WHEN 32 THEN 'critical_damage_flat_reduction'
                    WHEN 34 THEN 'life_absorption_flat'
                    WHEN 38 THEN 'damage_rebound_flat'
                    WHEN 1001 THEN 'physical_damage_reduction'
                    WHEN 1002 THEN 'magic_damage_reduction'
                END AS stat_name,
                bonus.effect_value AS stat_value
            FROM public.character_pets pet
            JOIN public.character_pet_character_bonuses bonus
              ON bonus.pet_id = pet.id
            WHERE pet.user_id = @characterId
              AND pet.contributes_to_character
        ),
        """;
}
