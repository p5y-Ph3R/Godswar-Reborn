using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetBindMigrationChecks
{
    public const string CheckName =
        "PostgreSQL durable pet-bind migration";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id == "20260812_087_pet_bind");
        Check.True(
            migration.Sql.Contains(
                "'bind'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'pet_bind'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "VALIDATE CONSTRAINT",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "CREATE OR REPLACE VIEW public.pet_durable_command_evidence",
                StringComparison.Ordinal),
            "migration 087 authorizes bind audit and durable evidence without rewriting history");
        return Task.CompletedTask;
    }
}
