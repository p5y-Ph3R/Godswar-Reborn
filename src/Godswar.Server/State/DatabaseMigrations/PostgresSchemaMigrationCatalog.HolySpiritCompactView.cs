namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static string HolySpiritEffectivenessCompactViewSql =>
        CreateHolySpiritEffectivenessCompactViewSql();

    private static string CreateHolySpiritEffectivenessCompactViewSql()
    {
        const string oldEmptyCondition =
            "AND ci.elemental_attribute2 IS NULL";
        const string newEmptyCondition =
            """
            AND ci.elemental_attribute2 IS NULL
                 AND ci.holy_socket1_value IS NULL
                 AND ci.holy_socket2_value IS NULL
                 AND ci.holy_socket3_value IS NULL
                 AND ci.holy_socket4_value IS NULL
            """;
        const string oldExtensionTail =
            "COALESCE(ci.elemental_attribute2::text, '')";
        const string newExtensionTail =
            """
            COALESCE(ci.elemental_attribute2::text, '') || ',' ||
                    COALESCE(ci.holy_socket1_value::text, '') || ',' ||
                    COALESCE(ci.holy_socket2_value::text, '') || ',' ||
                    COALESCE(ci.holy_socket3_value::text, '') || ',' ||
                    COALESCE(ci.holy_socket4_value::text, '')
            """;

        var sql = ElementalAttributeCompactViewSql
            .Replace(
                oldEmptyCondition,
                newEmptyCondition,
                StringComparison.Ordinal)
            .Replace(
                oldExtensionTail,
                newExtensionTail,
                StringComparison.Ordinal);
        if (!sql.Contains(
                "ci.holy_socket4_value",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Holy Spirit compact-view migration could not be composed.");
        }

        return sql;
    }
}
