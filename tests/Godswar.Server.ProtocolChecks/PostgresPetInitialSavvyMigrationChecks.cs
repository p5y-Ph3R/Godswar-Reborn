using System.Text.RegularExpressions;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetInitialSavvyMigrationChecks
{
    private const string MigrationId =
        "20260728_019_pet_initial_savvy_policy";

    private static readonly IReadOnlyList<(
        PetAptitude Aptitude,
        int Minimum,
        int Maximum)> ExpectedBrackets =
    [
        (PetAptitude.Weak, 250, 349),
        (PetAptitude.Fool, 350, 449),
        (PetAptitude.Cowish, 450, 574),
        (PetAptitude.Moderate, 575, 699),
        (PetAptitude.Rational, 700, 849),
        (PetAptitude.Calm, 850, 1_024),
        (PetAptitude.Grumpy, 1_025, 1_224),
        (PetAptitude.Brave, 1_225, 1_474),
        (PetAptitude.Zealous, 1_475, 1_774),
        (PetAptitude.Smart, 1_775, 2_124),
        (PetAptitude.Overbearing, 2_125, 2_524),
        (PetAptitude.Ferocious, 2_525, 2_974),
        (PetAptitude.Almighty, 2_975, 3_474),
        (PetAptitude.Godly, 3_475, 4_024),
        (PetAptitude.Celestial, 4_025, 4_624),
        (PetAptitude.Transcendent, 4_625, 5_324)
    ];

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id == MigrationId);
        var sql = migration.Sql;
        var migrationIndex = PostgresSchemaMigrationCatalog.All
            .Select((candidate, index) => (candidate, index))
            .Single(entry => entry.candidate.Id == MigrationId)
            .index;
        Check.Equal(
            MigrationId,
            PostgresSchemaMigrationCatalog.All[migrationIndex].Id,
            "historical initial-savvy policy remains registered");
        Check.Equal(
            "20260729_020_pet_savvy_semantics",
            PostgresSchemaMigrationCatalog.All[migrationIndex + 1].Id,
            "initial-savvy policy remains immediately before its semantic correction");

        CheckExactPolicy(sql);
        CheckZeroOnlySelection(sql);
        CheckArchiveAndAtomicUpdate(sql);
        CheckProvenanceAndProgression(sql);
        CheckSqlStructure(sql);
        return Task.CompletedTask;
    }

    private static void CheckExactPolicy(string sql)
    {
        Check.Equal(
            "project-v3",
            PetInitialSavvyPolicy.Version,
            "runtime advances without rewriting historical migration 019");
        Check.Equal(
            ExpectedBrackets.Count,
            PetInitialSavvyPolicy.All.Count,
            "initial-savvy runtime policy has exactly 16 brackets");
        Check.Equal(
            ExpectedBrackets.Count,
            Regex.Matches(
                sql,
                @"\(\d+::smallint,\s*\d+,\s*\d+\)",
                RegexOptions.CultureInvariant).Count,
            "migration contains exactly 16 initial-savvy policy tuples");

        for (var index = 0; index < ExpectedBrackets.Count; index++)
        {
            var expected = ExpectedBrackets[index];
            var expectedSql = FormattableString.Invariant(
                $"({(short)expected.Aptitude}::smallint, {expected.Minimum}, {expected.Maximum})");
            Check.True(
                sql.Contains(expectedSql, StringComparison.Ordinal),
                $"{expected.Aptitude} migration bracket matches the frozen policy");
        }

        Check.True(
            sql.Contains(
                "maximum_initial_savvy_stat_deviation = 0.1200",
                StringComparison.Ordinal) &&
            sql.Contains(
                "initial_savvy_policy_version = 'project-v1'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "GET DIAGNOSTICS updated_aptitudes = ROW_COUNT",
                StringComparison.Ordinal) &&
            sql.Contains(
                "IF updated_aptitudes <> 16",
                StringComparison.Ordinal),
            "migration installs every exact policy row and fails closed");
    }

    private static void CheckZeroOnlySelection(string sql)
    {
        var reconciliation = Slice(
            sql,
            "WITH complete_zero_savvy_pets AS MATERIALIZED",
            "DO $validate_pet_initial_savvy_policy$");

        Check.True(
            reconciliation.Contains(
                "HAVING count(*) = 6",
                StringComparison.Ordinal) &&
            reconciliation.Contains(
                "count(DISTINCT stat.stat_code) = 6",
                StringComparison.Ordinal) &&
            Regex.IsMatch(
                reconciliation,
                @"count\(\*\)\s+FILTER\s*\(\s*WHERE\s+" +
                @"stat\.initial_savvy\s*=\s*0\s*\)\s*=\s*6",
                RegexOptions.CultureInvariant),
            "reconciliation selects only complete all-zero savvy vectors");
        Check.True(
            !reconciliation.Contains(
                "complete_out_of_range_pets",
                StringComparison.Ordinal) &&
            !Regex.IsMatch(
                reconciliation,
                @"sum\s*\(\s*stat\.initial_savvy\s*\)\s*[<>]",
                RegexOptions.CultureInvariant),
            "reconciliation never selects progressed pets by current total");
        Check.True(
            !reconciliation.Contains(
                "SET added_savvy",
                StringComparison.OrdinalIgnoreCase) &&
            !reconciliation.Contains(
                "SET base_growth_rate",
                StringComparison.OrdinalIgnoreCase) &&
            !reconciliation.Contains(
                "SET growth_acceleration",
                StringComparison.OrdinalIgnoreCase) &&
            !reconciliation.Contains(
                "DELETE ",
                StringComparison.OrdinalIgnoreCase) &&
            !reconciliation.Contains(
                "random(",
                StringComparison.OrdinalIgnoreCase),
            "deterministic backfill preserves unrelated pet state");
    }

    private static void CheckArchiveAndAtomicUpdate(string sql)
    {
        var archiveDdl = Slice(
            sql,
            "CREATE TABLE public.pet_initial_savvy_reconciliation_archive",
            "ALTER TABLE public.pet_aptitude_templates");

        Check.True(
            archiveDdl.Contains(
                "migration_id varchar(128) NOT NULL",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "pet_id_snapshot bigint NOT NULL",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "owner_user_id_snapshot integer NOT NULL",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "aptitude_snapshot smallint NOT NULL",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "stat_code smallint NOT NULL",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "old_initial_savvy numeric(18, 6) NOT NULL",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "old_revision bigint NOT NULL",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "old_pet_revision bigint NOT NULL",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "old_initial_savvy_baseline_total integer",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "old_initial_savvy_policy_version varchar(32)",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "archived_at timestamptz NOT NULL",
                StringComparison.Ordinal),
            "archive stores every required stat and parent before-image");
        Check.True(
            !archiveDdl.Contains(
                "REFERENCES",
                StringComparison.OrdinalIgnoreCase) &&
            archiveDdl.Contains(
                "UNIQUE (migration_id, pet_id_snapshot, stat_code)",
                StringComparison.Ordinal),
            "archive survives source deletion and rejects duplicate snapshots");

        Check.True(
            sql.Contains(
                "archived_before_images AS (",
                StringComparison.Ordinal) &&
            sql.Contains(
                "INSERT INTO",
                StringComparison.Ordinal) &&
            sql.Contains(
                "public.pet_initial_savvy_reconciliation_archive",
                StringComparison.Ordinal) &&
            sql.Contains(
                $"'{MigrationId}'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "existing.initial_savvy",
                StringComparison.Ordinal) &&
            sql.Contains(
                "existing.revision",
                StringComparison.Ordinal) &&
            sql.Contains(
                "distribution.old_pet_revision",
                StringComparison.Ordinal) &&
            sql.Contains(
                "RETURNING pet_id_snapshot, stat_code",
                StringComparison.Ordinal),
            "zero-savvy reconciliation archives old values, revisions, and provenance");
        Check.True(
            sql.Contains(
                "INNER JOIN archived_before_images archived",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.pet_id_snapshot",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.stat_code",
                StringComparison.Ordinal) &&
            sql.Contains(
                "revision = existing.revision + 1",
                StringComparison.Ordinal),
            "each changed stat depends on its archive row and advances once");
    }

    private static void CheckProvenanceAndProgression(string sql)
    {
        Check.True(
            sql.Contains(
                "ADD COLUMN initial_savvy_baseline_total integer",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ADD COLUMN initial_savvy_policy_version varchar(32)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ck_character_pets_initial_savvy_provenance",
                StringComparison.Ordinal) &&
            sql.Contains(
                "initial_savvy_baseline_total IS NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "initial_savvy_policy_version IS NULL",
                StringComparison.Ordinal),
            "migration distinguishes legacy pets from policy-backed baselines");
        Check.True(
            sql.Contains(
                "completely_updated_pets AS (",
                StringComparison.Ordinal) &&
            sql.Contains(
                "HAVING count(*) = 6",
                StringComparison.Ordinal) &&
            sql.Contains(
                "initial_savvy_baseline_total =",
                StringComparison.Ordinal) &&
            sql.Contains(
                "zero_pet.midpoint_total_savvy",
                StringComparison.Ordinal) &&
            sql.Contains(
                "initial_savvy_policy_version = 'project-v1'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "revision = pet.revision + 1",
                StringComparison.Ordinal),
            "provenance is published only after all six stats update");
        Check.True(
            sql.Contains(
                "ALTER COLUMN initial_savvy DROP DEFAULT",
                StringComparison.Ordinal),
            "future pet creation must provide explicit initial savvy");

        var validation = Slice(
            sql,
            "DO $validate_pet_initial_savvy_policy$",
            "$validate_pet_initial_savvy_policy$;");
        var normalized = Normalize(validation);
        Check.True(
            normalized.Contains(
                "pet.initial_savvy_baseline_total IS NULL AND COALESCE( sum(stat.initial_savvy), 0 ) = 0",
                StringComparison.Ordinal) &&
            normalized.Contains(
                "COALESCE( sum(stat.initial_savvy), 0 ) < pet.initial_savvy_baseline_total",
                StringComparison.Ordinal),
            "legacy zero totals fail while managed current totals cannot fall below baseline");
        Check.True(
            !Regex.IsMatch(
                validation,
                @"sum\s*\(\s*stat\.initial_savvy\s*\)" +
                @"[\s\S]{0,80}>\s*aptitude\.maximum_initial_savvy",
                RegexOptions.CultureInvariant),
            "current initial savvy has no permanent upper cap");
    }

    private static void CheckSqlStructure(string sql)
    {
        Check.True(
            Regex.IsMatch(
                sql,
                @"archived_before_images\s+AS\s*\([\s\S]+?" +
                @"RETURNING\s+pet_id_snapshot\s*,\s*stat_code\s*" +
                @"\)\s*,\s*updated_stats\s+AS\s*\(",
                RegexOptions.CultureInvariant),
            "archival and stat update are comma-separated data-modifying CTEs");
        Check.True(
            Regex.IsMatch(
                sql,
                @"updated_stats\s+AS\s*\([\s\S]+?" +
                @"RETURNING\s+existing\.pet_id\s*,\s*" +
                @"existing\.stat_code\s*\)\s*,\s*" +
                @"completely_updated_pets\s+AS\s*\(",
                RegexOptions.CultureInvariant),
            "stat update feeds the complete-pet provenance CTE");
        Check.True(
            sql.Contains(
                "ALTER COLUMN minimum_initial_savvy SET NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "VALIDATE CONSTRAINT",
                StringComparison.Ordinal) &&
            sql.Contains(
                "RAISE EXCEPTION",
                StringComparison.Ordinal),
            "policy schema and reconciliation fail closed before commit");
    }

    private static string Normalize(string value) =>
        string.Join(
            ' ',
            value.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries));

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
