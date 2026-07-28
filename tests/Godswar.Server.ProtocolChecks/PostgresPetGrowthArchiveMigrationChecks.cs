using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetGrowthArchiveMigrationChecks
{
    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate =>
                candidate.Id ==
                "20260728_018_pet_growth_policy_v2");
        var sql = migration.Sql;
        var archiveDdl = Slice(
            sql,
            "CREATE TABLE public.pet_growth_reconciliation_archive",
            "DO $update_pet_growth_policy_v2$");

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
                "old_base_growth_rate numeric(18, 6) NOT NULL",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "old_revision bigint NOT NULL",
                StringComparison.Ordinal) &&
            archiveDdl.Contains(
                "archived_at timestamptz NOT NULL",
                StringComparison.Ordinal),
            "growth reconciliation archive retains every required before-image field");
        Check.True(
            !archiveDdl.Contains(
                "REFERENCES",
                StringComparison.OrdinalIgnoreCase) &&
            archiveDdl.Contains(
                "UNIQUE (migration_id, pet_id_snapshot, stat_code)",
                StringComparison.Ordinal),
            "growth archive survives source deletion and rejects duplicate before-images");
        Check.True(
            sql.Contains(
                "archived_before_images AS (",
                StringComparison.Ordinal) &&
            sql.Contains(
                "INSERT INTO public.pet_growth_reconciliation_archive",
                StringComparison.Ordinal) &&
            sql.Contains(
                "'20260728_018_pet_growth_policy_v2'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "existing.base_growth_rate",
                StringComparison.Ordinal) &&
            sql.Contains(
                "existing.revision",
                StringComparison.Ordinal) &&
            sql.Contains(
                "RETURNING pet_id_snapshot, stat_code",
                StringComparison.Ordinal),
            "v2 reconciliation archives the old value and revision under its migration identity");
        Check.True(
            sql.Contains(
                "INNER JOIN archived_before_images archived",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.pet_id_snapshot = distribution.pet_id",
                StringComparison.Ordinal) &&
            sql.Contains(
                "archived.stat_code = distribution.stat_code",
                StringComparison.Ordinal),
            "each reconciled stat update depends on its atomic archived before-image");

        return Task.CompletedTask;
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
            start,
            StringComparison.Ordinal);
        Check.True(
            start >= 0 && end > start,
            "growth reconciliation archive DDL is bounded");
        return text[start..end];
    }
}
