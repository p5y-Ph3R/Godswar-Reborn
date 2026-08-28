namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateMedusaDailyEntryLimit() => new(
        "20260827_116_medusa_daily_entry_limit",
        "Make the Medusa daily-entry limit database-owned",
        """
        CREATE TABLE public.medusa_instance_settings (
            instance_key text PRIMARY KEY,
            daily_entry_limit smallint NOT NULL
                CHECK (daily_entry_limit BETWEEN 1 AND 99),
            updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
            CONSTRAINT ck_medusa_instance_settings_key CHECK (
                instance_key = 'medusa')
        );

        INSERT INTO public.medusa_instance_settings (
            instance_key,
            daily_entry_limit)
        VALUES ('medusa', 3);

        ALTER TABLE public.medusa_daily_entries
            DROP CONSTRAINT medusa_daily_entries_pkey;
        ALTER TABLE public.medusa_daily_entries
            ADD CONSTRAINT medusa_daily_entries_pkey PRIMARY KEY (
                realm_id,
                realm_day,
                character_id,
                reservation_id);
        """);
}
