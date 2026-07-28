using System.Text.RegularExpressions;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetSavvySemanticsMigrationChecks
{
    private const string PreviousMigrationId =
        "20260728_019_pet_initial_savvy_policy";
    private const string MigrationId =
        "20260729_020_pet_savvy_semantics";
    private const string PreviousMigrationChecksum =
        "22D76D95138EF56F7B66496D0D00328203C5FEAA6CEC28BE57A201833D024AA5";
    private const string MigrationChecksum =
        "847BD78F4792AB9EC28DEFE3E94EB2FB4FDCDBC931FB92FF6DBF35FC98D1BED6";

    public static Task RunAsync()
    {
        var catalog = PostgresSchemaMigrationCatalog.All;
        var migrationIndex = catalog
            .Select((migration, index) => (migration, index))
            .Single(entry => entry.migration.Id == MigrationId)
            .index;
        var migration = catalog[migrationIndex];
        var sql = migration.Sql;

        CheckCatalogHistory(catalog, migrationIndex);
        Check.Equal(
            MigrationChecksum,
            migration.Checksum,
            "applied pet-savvy semantics migration remains immutable");
        CheckPolicyParity(sql);
        CheckProvenanceSelection(sql);
        CheckCompleteArchive(sql);
        CheckExactAllocation(sql);
        CheckSemanticUpdate(sql);
        CheckSchemaAndValidation(sql);
        CheckRebirthItem(sql);
        CheckNoFixtureTargeting(sql);
        return Task.CompletedTask;
    }

    private static void CheckCatalogHistory(
        IReadOnlyList<PostgresSchemaMigration> catalog,
        int migrationIndex)
    {
        Check.Equal(
            20,
            migrationIndex,
            "pet savvy semantics remains catalog migration 21");
        Check.True(
            migrationIndex > 0,
            "pet savvy semantics correction has a predecessor");

        var previous = catalog[migrationIndex - 1];
        Check.Equal(
            PreviousMigrationId,
            previous.Id,
            "immutable initial-savvy migration remains immediately before correction");
        Check.Equal(
            PreviousMigrationChecksum,
            previous.Checksum,
            "immutable initial-savvy migration checksum matches applied history");
        Check.Equal(
            PostgresSchemaMigration.ComputeChecksum(previous.Sql),
            previous.Checksum,
            "initial-savvy migration checksum still represents its registered SQL");
        Check.True(
            catalog
                .Select(static migration => migration.Id)
                .SequenceEqual(
                    catalog
                        .Select(static migration => migration.Id)
                        .OrderBy(static id => id, StringComparer.Ordinal)),
            "migration catalog remains strictly ordered");
    }

    private static void CheckPolicyParity(string sql)
    {
        Check.Equal(
            "project-v2",
            PetAddedSavvyPolicy.Version,
            "runtime added-savvy policy version");
        Check.Equal(
            16,
            PetAddedSavvyPolicy.All.Count,
            "runtime added-savvy policy covers all aptitudes");

        var sqlRows = Regex.Matches(
                sql,
                @"\((?<aptitude>\d+)::smallint,\s*" +
                @"(?<minimum>\d+),\s*(?<maximum>\d+)\)",
                RegexOptions.CultureInvariant)
            .Select(match => (
                Aptitude: short.Parse(match.Groups["aptitude"].Value),
                Minimum: int.Parse(match.Groups["minimum"].Value),
                Maximum: int.Parse(match.Groups["maximum"].Value)))
            .ToArray();
        Check.Equal(
            PetAddedSavvyPolicy.All.Count,
            sqlRows.Length,
            "migration contains one added-savvy bracket per aptitude");

        for (var index = 0;
             index < PetAddedSavvyPolicy.All.Count;
             index++)
        {
            var runtime = PetAddedSavvyPolicy.All[index];
            var migration = sqlRows[index];
            Check.Equal(
                runtime.AptitudeValue,
                migration.Aptitude,
                $"{runtime.Aptitude} migration aptitude");
            Check.Equal(
                runtime.MinimumTotalSavvy,
                migration.Minimum,
                $"{runtime.Aptitude} added-savvy minimum");
            Check.Equal(
                runtime.MaximumTotalSavvy,
                migration.Maximum,
                $"{runtime.Aptitude} added-savvy maximum");
        }

        var expectedWeights = string.Join(
            ", ",
            PetAddedSavvyPolicy.AllocationWeights);
        Check.True(
            sql.Contains(
                $"ARRAY[{expectedWeights}]",
                StringComparison.Ordinal),
            "migration allocation uses the runtime policy weights");
        Check.Equal(
            600,
            PetAddedSavvyPolicy.AllocationWeights.Sum(),
            "runtime allocation weights preserve the exact whole");
    }

    private static void CheckProvenanceSelection(string sql)
    {
        var selection = Slice(
            sql,
            "WITH eligible_pets AS MATERIALIZED",
            "weighted_stats AS (");
        Check.True(
            selection.Contains(
                "pet.initial_savvy_policy_version = 'project-v1'",
                StringComparison.Ordinal) &&
            selection.Contains(
                "pet.initial_savvy_baseline_total IS NOT NULL",
                StringComparison.Ordinal) &&
            selection.Contains(
                "pet.initial_savvy_baseline_total AS total_savvy",
                StringComparison.Ordinal),
            "correction targets only migration-019 provenance");

        var guard = Slice(
            sql,
            "DO $guard_pet_savvy_semantics_reconciliation$",
            "$guard_pet_savvy_semantics_reconciliation$;");
        Check.True(
            guard.Contains(
                "pet.initial_savvy_policy_version = 'project-v1'",
                StringComparison.Ordinal) &&
            guard.Contains(
                "count(stat.stat_code) <> 6",
                StringComparison.Ordinal) &&
            guard.Contains(
                "count(DISTINCT stat.stat_code) <> 6",
                StringComparison.Ordinal) &&
            guard.Contains(
                "WHERE stat.added_savvy <> 0",
                StringComparison.Ordinal) &&
            guard.Contains(
                "sum(stat.initial_savvy)",
                StringComparison.Ordinal) &&
            guard.Contains(
                "pet.initial_savvy_baseline_total",
                StringComparison.Ordinal) &&
            guard.Contains(
                "RAISE EXCEPTION",
                StringComparison.Ordinal),
            "provenance guard rejects incomplete or progressed migration-019 pets");
    }

    private static void CheckCompleteArchive(string sql)
    {
        var archiveDdl = Slice(
            sql,
            "CREATE TABLE public.pet_savvy_semantics_reconciliation_archive",
            "ALTER TABLE public.pet_aptitude_templates");
        var requiredColumns = new[]
        {
            "migration_id varchar(128) NOT NULL",
            "pet_id_snapshot bigint NOT NULL",
            "owner_user_id_snapshot integer NOT NULL",
            "aptitude_snapshot smallint NOT NULL",
            "stat_code smallint NOT NULL",
            "old_initial_savvy numeric(18, 6) NOT NULL",
            "old_added_savvy numeric(18, 6) NOT NULL",
            "old_base_growth_rate numeric(18, 6) NOT NULL",
            "old_growth_acceleration numeric(18, 6) NOT NULL",
            "old_stat_revision bigint NOT NULL",
            "old_pet_revision bigint NOT NULL",
            "old_initial_savvy_baseline_total integer",
            "old_initial_savvy_policy_version varchar(32)",
            "archived_at timestamptz NOT NULL"
        };
        Check.True(
            requiredColumns.All(column =>
                archiveDdl.Contains(column, StringComparison.Ordinal)),
            "archive retains the full stat and pet before-image");
        Check.True(
            archiveDdl.Contains(
                "UNIQUE (migration_id, pet_id_snapshot, stat_code)",
                StringComparison.Ordinal) &&
            !archiveDdl.Contains(
                "REFERENCES",
                StringComparison.OrdinalIgnoreCase),
            "archive is idempotent and survives source deletion");

        var archiveWrite = Slice(
            sql,
            "archived_before_images AS (",
            "updated_stats AS (");
        var requiredSources = new[]
        {
            "allocation.old_initial_savvy",
            "allocation.old_added_savvy",
            "allocation.base_growth_rate",
            "allocation.growth_acceleration",
            "allocation.old_stat_revision",
            "allocation.old_pet_revision",
            "allocation.total_savvy",
            "allocation.old_policy_version"
        };
        Check.True(
            requiredSources.All(source =>
                archiveWrite.Contains(source, StringComparison.Ordinal)) &&
            archiveWrite.Contains(
                $"'{MigrationId}'",
                StringComparison.Ordinal) &&
            archiveWrite.Contains(
                "RETURNING pet_id_snapshot, stat_code",
                StringComparison.Ordinal),
            "archive write captures every corrected value and provenance field");
    }

    private static void CheckExactAllocation(string sql)
    {
        var allocation = Slice(
            sql,
            "weighted_stats AS (",
            "archived_before_images AS (");
        Check.True(
            allocation.Contains(
                "ORDER BY md5(",
                StringComparison.Ordinal) &&
            allocation.Contains(
                $"'{MigrationId}:'",
                StringComparison.Ordinal) &&
            allocation.Contains(
                "weighted.total_savvy::bigint",
                StringComparison.Ordinal) &&
            allocation.Contains(
                "* 100",
                StringComparison.Ordinal) &&
            allocation.Contains(
                "/ 600 AS floor_units",
                StringComparison.Ordinal) &&
            allocation.Contains(
                "% 600 AS remainder",
                StringComparison.Ordinal) &&
            allocation.Contains(
                "AS unallocated_units",
                StringComparison.Ordinal) &&
            allocation.Contains(
                "remainder_rank",
                StringComparison.Ordinal) &&
            allocation.Contains(
                "AS allocated_units",
                StringComparison.Ordinal),
            "deterministic largest-remainder allocation preserves exact hundredths");
        Check.True(
            !Regex.IsMatch(
                sql,
                @"\brandom\s*\(",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant),
            "migration contains no PostgreSQL random allocation");
    }

    private static void CheckSemanticUpdate(string sql)
    {
        var update = Slice(
            sql,
            "updated_stats AS (",
            "WITH rebirth_items");
        Check.True(
            Regex.IsMatch(
                update,
                @"\bbirth_initial_savvy\s*=\s*" +
                @"allocation\.base_growth_rate",
                RegexOptions.CultureInvariant) &&
            Regex.IsMatch(
                update,
                @"(?<!birth_)\binitial_savvy\s*=\s*" +
                @"allocation\.base_growth_rate",
                RegexOptions.CultureInvariant),
            "basic savvy and its birth baseline derive from base growth");
        Check.True(
            Regex.IsMatch(
                update,
                @"\brarity_added_savvy\s*=\s*" +
                @"allocation\.allocated_units::numeric\s*/\s*100",
                RegexOptions.CultureInvariant) &&
            Regex.IsMatch(
                update,
                @"(?<!rarity_)\badded_savvy\s*=\s*" +
                @"allocation\.allocated_units::numeric\s*/\s*100",
                RegexOptions.CultureInvariant) &&
            Regex.IsMatch(
                update,
                @"\brarity_added_savvy_baseline_total\s*=\s*" +
                @"pet\.initial_savvy_baseline_total",
                RegexOptions.CultureInvariant),
            "rarity allocation moves to added savvy and its parent baseline");
        Check.True(
            update.Contains(
                "INNER JOIN archived_before_images archived",
                StringComparison.Ordinal) &&
            update.Contains(
                "revision = stat.revision + 1",
                StringComparison.Ordinal) &&
            update.Contains(
                "HAVING count(*) = 6",
                StringComparison.Ordinal) &&
            update.Contains(
                "count(DISTINCT stat_code) = 6",
                StringComparison.Ordinal) &&
            update.Contains(
                "revision = pet.revision + 1",
                StringComparison.Ordinal),
            "archived six-stat correction increments stat and pet revisions once");
        Check.True(
            update.Contains(
                "rarity_added_savvy_policy_version = 'project-v2'",
                StringComparison.Ordinal) &&
            update.Contains(
                "initial_savvy_source_version = 'growth-x1-v1'",
                StringComparison.Ordinal) &&
            update.Contains(
                "initial_savvy_baseline_total = NULL",
                StringComparison.Ordinal) &&
            update.Contains(
                "initial_savvy_policy_version = NULL",
                StringComparison.Ordinal),
            "new provenance replaces the obsolete initial-savvy provenance");
    }

    private static void CheckSchemaAndValidation(string sql)
    {
        var requiredColumns = new[]
        {
            "ADD COLUMN minimum_added_savvy integer",
            "ADD COLUMN maximum_added_savvy integer",
            "ADD COLUMN added_savvy_policy_version varchar(32)",
            "ADD COLUMN rarity_added_savvy_baseline_total integer",
            "ADD COLUMN rarity_added_savvy_policy_version varchar(32)",
            "ADD COLUMN initial_savvy_source_version varchar(32)",
            "ADD COLUMN birth_initial_savvy numeric(18, 6)",
            "ADD COLUMN rarity_added_savvy numeric(18, 6)"
        };
        Check.True(
            requiredColumns.All(column =>
                sql.Contains(column, StringComparison.Ordinal)),
            "migration adds explicit added-savvy baseline and source provenance");

        var validation = Slice(
            sql,
            "DO $validate_pet_savvy_semantics$",
            "$validate_pet_savvy_semantics$;");
        Check.True(
            sql.Contains(
                "GET DIAGNOSTICS updated_aptitudes = ROW_COUNT",
                StringComparison.Ordinal) &&
            sql.Contains(
                "IF updated_aptitudes <> 16",
                StringComparison.Ordinal) &&
            Regex.Matches(
                    sql,
                    @"VALIDATE CONSTRAINT",
                    RegexOptions.CultureInvariant)
                .Count >= 6 &&
            validation.Contains(
                "count(stat.stat_code) <> 6",
                StringComparison.Ordinal) &&
            validation.Contains(
                "sum(stat.rarity_added_savvy)",
                StringComparison.Ordinal) &&
            validation.Contains(
                "pet.rarity_added_savvy_baseline_total",
                StringComparison.Ordinal) &&
            validation.Contains(
                "stat.initial_savvy",
                StringComparison.Ordinal) &&
            validation.Contains(
                "stat.birth_initial_savvy",
                StringComparison.Ordinal) &&
            validation.Contains(
                "RAISE EXCEPTION",
                StringComparison.Ordinal),
            "installed policy, constraints, exact total, and semantic state fail closed");
    }

    private static void CheckRebirthItem(string sql)
    {
        var itemSql = Slice(
            sql,
            "WITH rebirth_items",
            "DO $validate_pet_savvy_semantics$");
        Check.True(
            Regex.IsMatch(
                itemSql,
                @"\(\s*11095,\s*'Pet11095',\s*" +
                @"'Ambrosia of Rebirth'",
                RegexOptions.CultureInvariant) &&
            itemSql.Contains(
                "INSERT INTO public.item_templates",
                StringComparison.Ordinal) &&
            itemSql.Contains(
                "ON CONFLICT (id) DO UPDATE",
                StringComparison.Ordinal),
            "migration safely seeds item 11095 Ambrosia of Rebirth");
    }

    private static void CheckNoFixtureTargeting(string sql)
    {
        Check.True(
            !Regex.IsMatch(
                sql,
                @"\bpet\.id\s*=\s*\d+",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant) &&
            !Regex.IsMatch(
                sql,
                @"\bpet\.(?:name|display_name)\b",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant) &&
            !Regex.IsMatch(
                sql,
                @"\busername\b",
                RegexOptions.IgnoreCase |
                RegexOptions.CultureInvariant),
            "migration has no hardcoded pet ID, pet name, or account username");
    }

    private static string Slice(
        string text,
        string startMarker,
        string endMarker)
    {
        var start = text.IndexOf(
            startMarker,
            StringComparison.Ordinal);
        var end = text.IndexOf(
            endMarker,
            start >= 0 ? start + startMarker.Length : 0,
            StringComparison.Ordinal);
        Check.True(
            start >= 0 && end > start,
            $"migration section {startMarker} is bounded");
        return text[start..end];
    }
}
