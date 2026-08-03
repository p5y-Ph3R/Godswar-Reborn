namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateItemContentV9Release() =>
        new(
            "20260803_056_canonical_elemental_stone_content",
            "Publish seven gear-context elemental stones through immutable item manifest v9",
            ItemContentV9DatabaseContractSql);

    private static string ItemContentV9DatabaseContractSql =>
        UpgradeItemManifestVersionListToV9(
            ItemContentV8DatabaseContractSql);

    private static string UpgradeItemManifestVersionListToV9(string sql) =>
        sql.Replace(
                "IN (1, 2, 3, 4, 5, 6, 7, 8)",
                "IN (1, 2, 3, 4, 5, 6, 7, 8, 9)",
                StringComparison.Ordinal)
            .Replace(
                "IN (2, 3, 4, 5, 6, 7, 8)",
                "IN (2, 3, 4, 5, 6, 7, 8, 9)",
                StringComparison.Ordinal)
            .Replace(
                "IN (3, 4, 5, 6, 7, 8)",
                "IN (3, 4, 5, 6, 7, 8, 9)",
                StringComparison.Ordinal)
            .Replace(
                "IN (4, 5, 6, 7, 8)",
                "IN (4, 5, 6, 7, 8, 9)",
                StringComparison.Ordinal)
            .Replace(
                "IN (5, 6, 7, 8)",
                "IN (5, 6, 7, 8, 9)",
                StringComparison.Ordinal)
            .Replace(
                "version 5 through 8",
                "version 5 through 9",
                StringComparison.Ordinal)
            .Replace("through 8", "through 9", StringComparison.Ordinal);
}
