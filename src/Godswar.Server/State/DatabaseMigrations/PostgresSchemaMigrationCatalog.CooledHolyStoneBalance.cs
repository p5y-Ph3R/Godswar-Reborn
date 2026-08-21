using Godswar.Server.Domain.Inventory;

namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const int PreviousCooledDirectReductionGradeOneMaximum = 55;
    private const int RaisedCooledDirectReductionGradeOneMaximum = 80;

    internal static PostgresSchemaMigration CreateCooledHolyStoneBalance() =>
        new(
            "20260821_098_cooled_holy_stone_balance",
            "Raise Cooled Holy Stone direct-reduction maximum rolls",
            $$"""
            UPDATE public.character_items
            SET holy_socket1_value = CASE
                    WHEN holy_socket1_effect_id IN (
                            {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                            {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                         AND holy_socket1_level BETWEEN 1 AND 10
                         AND holy_socket1_value =
                             holy_socket1_level * {{PreviousCooledDirectReductionGradeOneMaximum}}
                        THEN holy_socket1_level * {{RaisedCooledDirectReductionGradeOneMaximum}}
                    ELSE holy_socket1_value
                END,
                holy_socket2_value = CASE
                    WHEN holy_socket2_effect_id IN (
                            {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                            {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                         AND holy_socket2_level BETWEEN 1 AND 10
                         AND holy_socket2_value =
                             holy_socket2_level * {{PreviousCooledDirectReductionGradeOneMaximum}}
                        THEN holy_socket2_level * {{RaisedCooledDirectReductionGradeOneMaximum}}
                    ELSE holy_socket2_value
                END,
                holy_socket3_value = CASE
                    WHEN holy_socket3_effect_id IN (
                            {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                            {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                         AND holy_socket3_level BETWEEN 1 AND 10
                         AND holy_socket3_value =
                             holy_socket3_level * {{PreviousCooledDirectReductionGradeOneMaximum}}
                        THEN holy_socket3_level * {{RaisedCooledDirectReductionGradeOneMaximum}}
                    ELSE holy_socket3_value
                END,
                holy_socket4_value = CASE
                    WHEN holy_socket4_effect_id IN (
                            {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                            {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                         AND holy_socket4_level BETWEEN 1 AND 10
                         AND holy_socket4_value =
                             holy_socket4_level * {{PreviousCooledDirectReductionGradeOneMaximum}}
                        THEN holy_socket4_level * {{RaisedCooledDirectReductionGradeOneMaximum}}
                    ELSE holy_socket4_value
                END
            WHERE holy_socket1_effect_id IN (
                      {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                      {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                  AND holy_socket1_level BETWEEN 1 AND 10
                  AND holy_socket1_value =
                      holy_socket1_level * {{PreviousCooledDirectReductionGradeOneMaximum}}
               OR holy_socket2_effect_id IN (
                      {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                      {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                  AND holy_socket2_level BETWEEN 1 AND 10
                  AND holy_socket2_value =
                      holy_socket2_level * {{PreviousCooledDirectReductionGradeOneMaximum}}
               OR holy_socket3_effect_id IN (
                      {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                      {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                  AND holy_socket3_level BETWEEN 1 AND 10
                  AND holy_socket3_value =
                      holy_socket3_level * {{PreviousCooledDirectReductionGradeOneMaximum}}
               OR holy_socket4_effect_id IN (
                      {{HolySpiritImplementationPolicy.CooledPhysicalDamageReductionEffectId}},
                      {{HolySpiritImplementationPolicy.CooledMagicDamageReductionEffectId}})
                  AND holy_socket4_level BETWEEN 1 AND 10
                  AND holy_socket4_value =
                      holy_socket4_level * {{PreviousCooledDirectReductionGradeOneMaximum}};
            """);
}
