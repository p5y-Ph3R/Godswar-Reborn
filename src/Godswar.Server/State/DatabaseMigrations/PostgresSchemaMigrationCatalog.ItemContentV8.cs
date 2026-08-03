namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateItemContentV8Release() =>
        new(
            "20260803_055_elemental_stone_icon_content",
            "Publish distinct elemental stone icons through immutable item manifest v8",
            ItemContentV8DatabaseContractSql);

    private static string ItemContentV8DatabaseContractSql =>
        UpgradeItemManifestVersionListToV8(
            ItemContentV7DatabaseContractSql);

    private static string UpgradeItemManifestVersionListToV8(string sql) =>
        sql.Replace(
                "IN (1, 2, 3, 4, 5, 6, 7)",
                "IN (1, 2, 3, 4, 5, 6, 7, 8)",
                StringComparison.Ordinal)
            .Replace(
                "IN (2, 3, 4, 5, 6, 7)",
                "IN (2, 3, 4, 5, 6, 7, 8)",
                StringComparison.Ordinal)
            .Replace(
                "IN (3, 4, 5, 6, 7)",
                "IN (3, 4, 5, 6, 7, 8)",
                StringComparison.Ordinal)
            .Replace(
                "IN (4, 5, 6, 7)",
                "IN (4, 5, 6, 7, 8)",
                StringComparison.Ordinal)
            .Replace(
                "IN (5, 6, 7)",
                "IN (5, 6, 7, 8)",
                StringComparison.Ordinal)
            .Replace(
                "version 5 or 6",
                "version 5 through 8",
                StringComparison.Ordinal)
            .Replace("through 7", "through 8", StringComparison.Ordinal);
}
