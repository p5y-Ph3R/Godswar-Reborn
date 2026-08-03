namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static string ItemContentV7DatabaseContractSql =>
        UpgradeItemManifestVersionList(ItemContentV6HeaderSql) +
        UpgradeItemManifestVersionList(ItemContentV6InsertGuardsSql) +
        UpgradeItemManifestVersionList(ItemContentV6PublicationGuardSql) +
        UpgradeItemManifestVersionList(ItemContentV6ViewsSql);

    private static string UpgradeItemManifestVersionList(string sql) =>
        sql.Replace(
                "IN (1, 2, 3, 4, 5, 6)",
                "IN (1, 2, 3, 4, 5, 6, 7)",
                StringComparison.Ordinal)
            .Replace(
                "IN (2, 3, 4, 5, 6)",
                "IN (2, 3, 4, 5, 6, 7)",
                StringComparison.Ordinal)
            .Replace(
                "IN (3, 4, 5, 6)",
                "IN (3, 4, 5, 6, 7)",
                StringComparison.Ordinal)
            .Replace(
                "IN (4, 5, 6)",
                "IN (4, 5, 6, 7)",
                StringComparison.Ordinal)
            .Replace(
                "IN (5, 6)",
                "IN (5, 6, 7)",
                StringComparison.Ordinal)
            .Replace("through 6", "through 7", StringComparison.Ordinal);
}
