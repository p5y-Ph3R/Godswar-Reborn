using System.Security.Cryptography;
using System.Text;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresMigrationFoundationChecks
{
    public static async Task RunAsync()
    {
        CheckForwardOnlyCatalog();
        CheckDatabaseCleanupMigrations();
        CheckStableChecksums();
        CheckImmutableHistory();
        CheckBootstrapSafetyDecision();
        await CheckBootstrapResourceIdentityAsync();
    }

    private static void CheckForwardOnlyCatalog()
    {
        Check.Equal(8, PostgresSchemaMigrationCatalog.All.Count, "migration catalog entry count");
        var baseline = PostgresSchemaMigrationCatalog.All[0];
        Check.Equal(
            "20260723_000_legacy_schema_baseline",
            baseline.Id,
            "legacy database receives one explicit metadata baseline");
        Check.True(
            !baseline.Sql.Contains("050_test_character", StringComparison.OrdinalIgnoreCase) &&
            !baseline.Sql.Contains("character_base", StringComparison.OrdinalIgnoreCase) &&
            !baseline.Sql.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase),
            "baseline cannot replay legacy bootstrap or test-character mutations");
        Check.Throws<ArgumentException>(
            () => new PostgresSchemaMigration(
                "050_test_character_fixture",
                "legacy fixture",
                "SELECT 1;"),
            "legacy numbered script IDs cannot enter the forward-only catalog");
        Check.True(
            PostgresSchemaMigrationCatalog.All
                .Select(static migration => migration.Id)
                .SequenceEqual(
                [
                    "20260723_000_legacy_schema_baseline",
                    "20260723_001_mount_ride_compatibility",
                    "20260723_002_mount_rank_guard",
                    "20260723_003_erebus_lion_mount",
                    "20260723_004_remove_redundant_indexes",
                    "20260723_005_starter_consumable_templates",
                    "20260723_006_archive_legacy_character_kitbag",
                    "20260723_007_character_item_template_foreign_key"
                ]),
            "explicit migration catalog remains ordered and complete");
        Check.True(
            PostgresSchemaMigrationCatalog.All.All(migration =>
                !migration.Sql.Contains(
                    "test_character",
                    StringComparison.OrdinalIgnoreCase)),
            "production migration catalog cannot contain local character fixtures");
        var indexCleanup = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id == "20260723_004_remove_redundant_indexes");
        Check.True(
            indexCleanup.Sql.Contains(
                "UNIQUE USING INDEX ux_accounts_username",
                StringComparison.Ordinal) &&
            indexCleanup.Sql.Contains(
                "WHERE conindid = username_index",
                StringComparison.Ordinal),
            "fresh and existing databases retain an authoritative username uniqueness constraint");
    }

    private static void CheckDatabaseCleanupMigrations()
    {
        var consumables = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id == "20260723_005_starter_consumable_templates");
        Check.True(
            consumables.Sql.Contains("'HPPotion_a'", StringComparison.Ordinal) &&
            consumables.Sql.Contains("'MPPotion_a'", StringComparison.Ordinal) &&
            consumables.Sql.Contains("'consume item'", StringComparison.Ordinal) &&
            consumables.Sql.Contains("'252,972'", StringComparison.Ordinal) &&
            consumables.Sql.Contains("'432,972'", StringComparison.Ordinal) &&
            consumables.Sql.Contains("\"Skill\": \"3100\"", StringComparison.Ordinal) &&
            consumables.Sql.Contains("\"Skill\": \"3120\"", StringComparison.Ordinal) &&
            consumables.Sql.Contains("\"ItemType\": \"10\"", StringComparison.Ordinal) &&
            consumables.Sql.Contains("\"ItemType\": \"11\"", StringComparison.Ordinal) &&
            consumables.Sql.Contains("\"Overlap\": \"99\"", StringComparison.Ordinal) &&
            consumables.Sql.Contains("\"Money\": \"5\"", StringComparison.Ordinal),
            "starter consumables retain their client-derived metadata");
        Check.True(
            consumables.Sql.Contains(
                "'./Localization/en_us/UI/Texture/Icon.gwo'",
                StringComparison.Ordinal) &&
            consumables.Sql
                .Split('\n')
                .Count(static line => line.Trim().Equals("-1,", StringComparison.Ordinal)) == 2,
            "starter consumables use the native texture and cannot occupy equipment slots");
        Check.True(
            consumables.Sql.Contains("ON CONFLICT (id) DO UPDATE", StringComparison.Ordinal),
            "starter consumable reconciliation is idempotent");

        var archive = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id == "20260723_006_archive_legacy_character_kitbag");
        var normalizedArchiveSql = archive.Sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        const string parityMarker =
            "-- Verify exact row parity in both directions. The archive timestamp is";
        var parityStart = normalizedArchiveSql.IndexOf(parityMarker, StringComparison.Ordinal);
        var parityEnd = parityStart >= 0
            ? normalizedArchiveSql.IndexOf(
                "    END IF;",
                parityStart,
                StringComparison.Ordinal)
            : -1;
        var paritySql = parityStart >= 0 && parityEnd > parityStart
            ? normalizedArchiveSql[parityStart..parityEnd]
            : string.Empty;
        var firstSource = paritySql.IndexOf(
            "FROM public.character_kitbag",
            StringComparison.Ordinal);
        var firstExcept = paritySql.IndexOf(
            "EXCEPT",
            Math.Max(0, firstSource),
            StringComparison.Ordinal);
        var firstArchive = paritySql.IndexOf(
            "FROM legacy.character_kitbag_archive",
            Math.Max(0, firstExcept),
            StringComparison.Ordinal);
        var secondArchive = paritySql.IndexOf(
            "FROM legacy.character_kitbag_archive",
            Math.Max(0, firstArchive + 1),
            StringComparison.Ordinal);
        var secondExcept = paritySql.IndexOf(
            "EXCEPT",
            Math.Max(0, secondArchive),
            StringComparison.Ordinal);
        var secondSource = paritySql.IndexOf(
            "FROM public.character_kitbag",
            Math.Max(0, secondExcept),
            StringComparison.Ordinal);
        Check.True(
            archive.Sql.Contains(
                "legacy.character_kitbag_archive",
                StringComparison.Ordinal) &&
            archive.Sql.Contains(
                "20260721_legacy_character_kitbag_import",
                StringComparison.Ordinal) &&
            archive.Sql.Contains(
                "DROP TABLE IF EXISTS public.character_kitbag RESTRICT",
                StringComparison.Ordinal),
            "legacy kitbag retirement requires import, archive parity, and a restricted drop");
        Check.True(
            archive.Sql.Contains(
                "Cannot retire public.character_kitbag because the source table is absent",
                StringComparison.Ordinal) &&
            !normalizedArchiveSql.Contains(
                "IF source_table IS NULL THEN\n                RETURN;",
                StringComparison.Ordinal),
            "legacy kitbag retirement fails closed when its source table is absent");
        Check.True(
            firstSource >= 0 &&
            firstSource < firstExcept &&
            firstExcept < firstArchive &&
            firstArchive < secondArchive &&
            secondArchive < secondExcept &&
            secondExcept < secondSource &&
            paritySql
                .Split('\n')
                .Count(static line =>
                    line.Trim().Equals(
                        "EXCEPT",
                        StringComparison.Ordinal)) == 2 &&
            !paritySql.Contains("archived_at", StringComparison.Ordinal),
            "legacy kitbag retirement proves source/archive parity in both directions");
        Check.True(
            archive.Sql.Contains("information_schema.referential_constraints", StringComparison.Ordinal) &&
            archive.Sql.Contains("pg_depend", StringComparison.Ordinal) &&
            archive.Sql.Contains("information_schema.triggers", StringComparison.Ordinal),
            "legacy kitbag retirement explicitly proves database dependencies are absent");

        var inventoryForeignKey = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id == "20260723_007_character_item_template_foreign_key");
        Check.True(
            inventoryForeignKey.Sql.Contains(
                "FOREIGN KEY (prop_id)",
                StringComparison.Ordinal) &&
            inventoryForeignKey.Sql.Contains(
                "REFERENCES public.item_templates (id)",
                StringComparison.Ordinal) &&
            inventoryForeignKey.Sql.Contains("ON DELETE RESTRICT", StringComparison.Ordinal) &&
            inventoryForeignKey.Sql.Contains("NOT VALID", StringComparison.Ordinal) &&
            inventoryForeignKey.Sql.Contains(
                "VALIDATE CONSTRAINT fk_character_items_prop_id_item_templates",
                StringComparison.Ordinal),
            "inventory template integrity is staged then validated without deleting rows");
        Check.True(
            string.CompareOrdinal(consumables.Id, inventoryForeignKey.Id) < 0,
            "missing starter consumable templates are reconciled before inventory validation");

        foreach (var migration in PostgresSchemaMigrationCatalog.All)
        {
            Check.True(
                !migration.Sql.Contains(
                    "DELETE FROM character_items",
                    StringComparison.OrdinalIgnoreCase) &&
                !migration.Sql.Contains(
                    "TRUNCATE character_items",
                    StringComparison.OrdinalIgnoreCase) &&
                !migration.Sql.Contains(
                    "DROP TABLE character_items",
                    StringComparison.OrdinalIgnoreCase) &&
                !migration.Sql.Contains(
                    "DROP TABLE public.character_items",
                    StringComparison.OrdinalIgnoreCase),
                $"migration {migration.Id} cannot guess-delete authoritative inventory rows");
        }
    }

    private static void CheckStableChecksums()
    {
        const string id = "20260723_001_checksum_check";
        var windows = new PostgresSchemaMigration(id, "checksum", "SELECT 1;\r\nSELECT 2;\r\n");
        var unix = new PostgresSchemaMigration(id, "checksum", "SELECT 1;\nSELECT 2;\n");
        var changed = new PostgresSchemaMigration(id, "checksum", "SELECT 1;\nSELECT 3;\n");

        Check.Equal(windows.Checksum, unix.Checksum, "migration checksum ignores checkout line endings");
        Check.True(
            !string.Equals(windows.Checksum, changed.Checksum, StringComparison.Ordinal),
            "migration checksum detects SQL changes");
        Check.Equal(64, windows.Checksum.Length, "migration checksum is SHA-256 hex");
    }

    private static void CheckImmutableHistory()
    {
        var first = new PostgresSchemaMigration(
            "20260723_001_first_check",
            "first",
            "SELECT 1;");
        var second = new PostgresSchemaMigration(
            "20260723_002_second_check",
            "second",
            "SELECT 2;");
        var registered = new[] { first, second };

        var freshPlan = PostgresSchemaMigrationPlan.Build(
            registered,
            Array.Empty<AppliedPostgresSchemaMigration>());
        Check.Equal(2, freshPlan.Count, "fresh history applies every registered migration");

        var existingPlan = PostgresSchemaMigrationPlan.Build(
            registered,
            [new AppliedPostgresSchemaMigration(first.Id, first.Checksum)]);
        Check.Equal(1, existingPlan.Count, "applied migration is not replayed");
        Check.Equal(second.Id, existingPlan[0].Id, "only the pending migration remains");

        var completePlan = PostgresSchemaMigrationPlan.Build(
            registered,
            [
                new AppliedPostgresSchemaMigration(first.Id, first.Checksum),
                new AppliedPostgresSchemaMigration(second.Id, second.Checksum)
            ]);
        Check.Equal(0, completePlan.Count, "complete migration history has no pending suffix");

        Check.Throws<InvalidOperationException>(
            () => PostgresSchemaMigrationPlan.Build(
                registered,
                [new AppliedPostgresSchemaMigration(first.Id, new string('0', 64))]),
            "modified applied migration is rejected");
        Check.Throws<InvalidOperationException>(
            () => PostgresSchemaMigrationPlan.Build(
                registered,
                [new AppliedPostgresSchemaMigration(
                    "20260723_999_unknown_check",
                    new string('A', 64))]),
            "unknown applied migration is rejected");
        Check.Throws<InvalidOperationException>(
            () => PostgresSchemaMigrationPlan.Build(
                registered,
                [new AppliedPostgresSchemaMigration(second.Id, second.Checksum)]),
            "gapped migration history is rejected");
        Check.Throws<InvalidOperationException>(
            () => PostgresSchemaMigrationPlan.Build(
                registered,
                [
                    new AppliedPostgresSchemaMigration(second.Id, second.Checksum),
                    new AppliedPostgresSchemaMigration(first.Id, first.Checksum)
                ]),
            "reordered migration history is rejected");
        Check.Throws<InvalidOperationException>(
            () => PostgresSchemaMigrationPlan.Build(
                [first],
                [
                    new AppliedPostgresSchemaMigration(first.Id, first.Checksum),
                    new AppliedPostgresSchemaMigration(second.Id, second.Checksum)
                ]),
            "migration history ahead of this server is rejected");
        Check.Throws<InvalidOperationException>(
            () => PostgresSchemaMigrationPlan.Build(
                registered,
                [
                    new AppliedPostgresSchemaMigration(first.Id, first.Checksum),
                    new AppliedPostgresSchemaMigration(first.Id, first.Checksum)
                ]),
            "duplicate applied migration history is rejected");
        Check.Throws<InvalidOperationException>(
            () => PostgresSchemaMigrationPlan.Build(
                [second, first],
                Array.Empty<AppliedPostgresSchemaMigration>()),
            "out-of-order migration registration is rejected");
        Check.Throws<InvalidOperationException>(
            () => PostgresSchemaMigrationPlan.Build(
                [first, first],
                Array.Empty<AppliedPostgresSchemaMigration>()),
            "duplicate migration registration is rejected");
    }

    private static void CheckBootstrapSafetyDecision()
    {
        Check.True(
            PostgresSchemaMigrationRunner.ClassifyLegacySchema(0) ==
            LegacySchemaBootstrapDecision.BootstrapFreshDatabase,
            "empty database permits legacy bootstrap once");
        Check.True(
            PostgresSchemaMigrationRunner.ClassifyLegacySchema(4) ==
            LegacySchemaBootstrapDecision.BaselineExistingDatabase,
            "existing database skips legacy bootstrap");
        Check.Throws<InvalidOperationException>(
            () => PostgresSchemaMigrationRunner.ClassifyLegacySchema(2),
            "partial database refuses unsafe bootstrap replay");
    }

    private static async Task CheckBootstrapResourceIdentityAsync()
    {
        const int expectedByteCount = 87_573;
        const string expectedSha256 =
            "F10E4B8752506AA10E72D88C62D49850A3F9B3197ECB0E2CBD693AFC34B9B09A";
        var strictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);
        var sql = await LegacySchemaBootstrap.LoadAsync(CancellationToken.None);
        var bytes = strictUtf8.GetBytes(sql);

        Check.Equal(expectedByteCount, bytes.Length, "legacy bootstrap byte count");
        Check.Equal(
            expectedSha256,
            Convert.ToHexString(SHA256.HashData(bytes)),
            "legacy bootstrap fragment reconstruction hash");
    }

}
