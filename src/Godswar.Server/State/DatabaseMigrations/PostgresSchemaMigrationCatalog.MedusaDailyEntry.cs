namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateMedusaDailyEntry() => new(
        "20260824_112_medusa_daily_entry",
        "Persist one shared daily Medusa entry per realm character",
        """
        CREATE TABLE IF NOT EXISTS medusa_daily_entries (
            realm_id smallint NOT NULL,
            realm_day date NOT NULL,
            character_id integer NOT NULL
                REFERENCES character_base(id) ON DELETE CASCADE,
            reservation_id uuid NOT NULL,
            difficulty smallint NOT NULL CHECK (difficulty BETWEEN 1 AND 3),
            claimed_at timestamptz NOT NULL,
            PRIMARY KEY (realm_id, realm_day, character_id)
        );

        CREATE INDEX IF NOT EXISTS ix_medusa_daily_entries_reservation
            ON medusa_daily_entries (reservation_id);
        """);
}
