namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateElementalAttributeSlots() =>
        new(
            "20260803_054_elemental_class_suit_attributes",
            "Add two elemental slots beside one Class Suit attribute",
            string.Concat(
                ItemContentV7DatabaseContractSql,
                ElementalAttributeSchemaSql,
                ElementalAttributeCanonicalStateSql,
                ElementalAttributeCompactViewSql,
                ElementalAttributeConstraintsSql,
                CharacterInventoryReconciliationV3Sql));
}
