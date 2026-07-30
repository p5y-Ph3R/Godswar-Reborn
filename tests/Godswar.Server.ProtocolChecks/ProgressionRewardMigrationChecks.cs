using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class ProgressionRewardMigrationChecks
{
    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            candidate => candidate.Id ==
                "20260731_032_progression_reward_foundation");
        var sql = migration.Sql;
        foreach (var fragment in new[]
                 {
                     "ADD COLUMN progression_reward_revision bigint",
                     "CHECK (progression_reward_revision >= 0)",
                     "CREATE TABLE public.monster_death_reward_settlements",
                     "death_event_id uuid PRIMARY KEY",
                     "runtime_instance_id uuid NOT NULL",
                     "command_inbox_id bigint NOT NULL",
                     "REFERENCES public.command_inbox (id)",
                     "REFERENCES public.command_audit (id)",
                     "REFERENCES public.outbox_events (event_id)",
                     "request_hash bytea NOT NULL",
                     "progression_revision bigint NOT NULL",
                     "trg_monster_reward_immutable_rows",
                     "trg_monster_reward_no_truncate",
                     "reject_monster_reward_settlement_mutation",
                     "ON DELETE RESTRICT"
                 })
        {
            Check.True(
                sql.Contains(fragment, StringComparison.Ordinal),
                $"progression reward migration contains {fragment}");
        }
        Check.True(
            !sql.Contains(
                "fk_monster_reward_character",
                StringComparison.Ordinal) &&
            !sql.Contains(
                "FOREIGN KEY (character_id)",
                StringComparison.Ordinal),
            "permanent reward evidence does not block character purge");
        return Task.CompletedTask;
    }
}
