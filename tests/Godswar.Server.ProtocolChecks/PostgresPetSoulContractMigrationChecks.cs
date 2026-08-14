using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetSoulContractMigrationChecks
{
    public const string CheckName =
        "PostgreSQL durable pet Soul Contract migration";

    public static Task RunAsync()
    {
        var migrations = PostgresSchemaMigrationCatalog.All;
        var position = migrations
            .Select((migration, index) => (migration, index))
            .Single(static value => value.migration.Id ==
                "20260813_088_pet_soul_contract");
        var sql = position.migration.Sql;
        Check.True(
            migrations[position.index - 1].Id ==
                "20260812_087_pet_bind" &&
            sql.Contains(
                "soul_contract_stage smallint",
                StringComparison.Ordinal) &&
            sql.Contains(
                "soul_contract_stage BETWEEN 0 AND 6",
                StringComparison.Ordinal) &&
            sql.Contains(
                "sync_character_pet_soul_contract",
                StringComparison.Ordinal) &&
            sql.Contains(
                "NEW.has_soul_contract :=",
                StringComparison.Ordinal) &&
            sql.Contains(
                "SET CONSTRAINTS ALL IMMEDIATE;",
                StringComparison.Ordinal) &&
            sql.Contains(
                "'pet_soul_contract'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE OR REPLACE VIEW public.pet_durable_command_evidence",
                StringComparison.Ordinal),
            "migration 088 persists stage 0..6 and durable Soul evidence");
        return Task.CompletedTask;
    }
}
