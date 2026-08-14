using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class CombatSecondaryProjectionChecks
{
    private static void CheckProjectionAuthorityFences()
    {
        var calculated = PostgresCharacterRuntimeItemProjectionSql
            .CalculatedStatsForCharacter;
        Check.True(
            calculated.Contains(
                "template.equipment_slot = equipment.slot_index",
                StringComparison.Ordinal) &&
            calculated.Contains(
                "equipment_template.equipment_slot = equipment.slot_index",
                StringComparison.Ordinal) &&
            calculated.Contains(
                "template.kind = 'ring'",
                StringComparison.Ordinal) &&
            calculated.Contains(
                "owner.profession = ANY(template.class_ids)",
                StringComparison.Ordinal) &&
            calculated.Contains(
                "owner.fighter_job_lv >=",
                StringComparison.Ordinal),
            "ordinary equipment and attributes are fenced by pinned slot, " +
            "ring, class, and level authority");

        var typed = PostgresCharacterCombatSecondaryProjectionSql
            .CommonTableExpressions;
        Check.True(
            typed.Contains(
                "template.equipment_slot = equipment.slot_index",
                StringComparison.Ordinal) &&
            typed.Contains(
                "owner.profession = ANY(template.class_ids)",
                StringComparison.Ordinal) &&
            typed.Contains(
                "equipment.user_id = @characterId",
                StringComparison.Ordinal),
            "typed absorption cannot read an off-slot or ineligible item");
        Check.True(
            !typed.Contains(
                "holy_stone_stat_values",
                StringComparison.Ordinal) &&
            typed.Contains(
                "attribute_stat_values",
                StringComparison.Ordinal) &&
            typed.Contains(
                "talent_stat_values",
                StringComparison.Ordinal) &&
            typed.Contains(
                "holy_suit_stat_values",
                StringComparison.Ordinal) &&
            typed.Contains(
                "mount_gear_spirit_stat_values",
                StringComparison.Ordinal) &&
            typed.Contains(
                "pet_owner_merge_stat_values",
                StringComparison.Ordinal),
            "typed compatibility expansion excludes Holy Spirit legacy " +
            "display rows while retaining non-Holy all-damage sources");

        var spirits = PostgresCharacterHolySpiritCombatProjectionSql
            .CommonTableExpressions;
        Check.True(
            spirits.Contains(
                "socket.socket_index < equipment.holy_socket_count",
                StringComparison.Ordinal) &&
            spirits.Contains(
                "socket.effect_level BETWEEN 1 AND 10",
                StringComparison.Ordinal) &&
            spirits.Contains(
                "socket.effectiveness_value BETWEEN",
                StringComparison.Ordinal) &&
            spirits.Contains(
                "effect.grade_one_minimum * socket.effect_level",
                StringComparison.Ordinal) &&
            spirits.Contains(
                "equipment.slot_index IN (0, 2, 8, 9, 10)",
                StringComparison.Ordinal) &&
            spirits.Contains(
                "equipment.slot_index IN (1, 3, 4, 5, 6, 7, 11)",
                StringComparison.Ordinal),
            "Holy Spirits require an opened ordinal, reviewed effect/value, " +
            "and affinity-compatible equipped slot");

        var weapon = PostgresCharacterWeaponCombatProjectionSql
            .LateralJoinForCharacterAlias;
        Check.True(
            weapon.Contains(
                "template.equipment_slot = 10",
                StringComparison.Ordinal) &&
            weapon.Contains(
                "cb.profession = ANY(template.class_ids)",
                StringComparison.Ordinal) &&
            weapon.Contains(
                "cb.fighter_job_lv >=",
                StringComparison.Ordinal),
            "weapon cadence and range share equipped class/level authority");
    }
}
