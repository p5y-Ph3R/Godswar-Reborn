namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static string HolySpiritEffectivenessCanonicalStateSql =>
        CreateHolySpiritEffectivenessCanonicalStateSql();

    private static string CreateHolySpiritEffectivenessCanonicalStateSql()
    {
        const string oldTail =
            """
            'elemental_attribute2', NULLIF(COALESCE(
                item_state ->> 'elemental_attribute2',
                item_state ->> 'elementalAttribute2'), '')::smallint
        );
        """;
        const string newTail =
            """
            'elemental_attribute2', NULLIF(COALESCE(
                item_state ->> 'elemental_attribute2',
                item_state ->> 'elementalAttribute2'), '')::smallint,
            'holy_socket1_value', NULLIF(
                item_state ->> 'holy_socket1_value', '')::smallint,
            'holy_socket2_value', NULLIF(
                item_state ->> 'holy_socket2_value', '')::smallint,
            'holy_socket3_value', NULLIF(
                item_state ->> 'holy_socket3_value', '')::smallint,
            'holy_socket4_value', NULLIF(
                item_state ->> 'holy_socket4_value', '')::smallint
        );
        """;

        var sql = ElementalAttributeCanonicalStateSql.Replace(
            oldTail,
            newTail,
            StringComparison.Ordinal);
        if (string.Equals(
                sql,
                ElementalAttributeCanonicalStateSql,
                StringComparison.Ordinal) ||
            !sql.Contains(
                "item_state ->> 'holy_socket4_value'",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Holy Spirit canonical-state migration could not be composed.");
        }

        return sql;
    }
}
