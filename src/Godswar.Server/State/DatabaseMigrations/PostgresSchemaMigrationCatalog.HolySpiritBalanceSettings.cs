namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    internal static PostgresSchemaMigration
        CreateHolySpiritBalanceSettings() => new(
        "20260821_099_holy_spirit_balance_settings",
        "Create mutable Holy Spirit balance authority and lower Cooled caps",
        """
        CREATE TABLE public.holy_spirit_balance_settings (
            setting_id smallint PRIMARY KEY DEFAULT 1
                CHECK (setting_id = 1),
            cooled_physical_reduction_grade_one_maximum smallint NOT NULL
                CHECK (
                    cooled_physical_reduction_grade_one_maximum
                        BETWEEN 22 AND 80),
            cooled_magic_reduction_grade_one_maximum smallint NOT NULL
                CHECK (
                    cooled_magic_reduction_grade_one_maximum
                        BETWEEN 22 AND 80),
            cooled_critical_reduction_grade_one_maximum smallint NOT NULL
                CHECK (
                    cooled_critical_reduction_grade_one_maximum
                        BETWEEN 28 AND 70),
            revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
            updated_at timestamptz NOT NULL DEFAULT now(),
            updated_by text NOT NULL
                CHECK (length(btrim(updated_by)) BETWEEN 1 AND 128)
        );

        INSERT INTO public.holy_spirit_balance_settings (
            setting_id,
            cooled_physical_reduction_grade_one_maximum,
            cooled_magic_reduction_grade_one_maximum,
            cooled_critical_reduction_grade_one_maximum,
            revision,
            updated_by
        )
        VALUES (1, 55, 55, 60, 0, 'migration-099');

        COMMENT ON TABLE public.holy_spirit_balance_settings IS
            'Mutable singleton authority for management-controlled Holy Spirit balance values.';
        COMMENT ON COLUMN public.holy_spirit_balance_settings.cooled_physical_reduction_grade_one_maximum IS
            'Maximum effect-9 roll per Holy Stone grade in hundredths of one percentage point.';
        COMMENT ON COLUMN public.holy_spirit_balance_settings.cooled_magic_reduction_grade_one_maximum IS
            'Maximum effect-10 roll per Holy Stone grade in hundredths of one percentage point.';
        COMMENT ON COLUMN public.holy_spirit_balance_settings.cooled_critical_reduction_grade_one_maximum IS
            'Maximum effect-13 roll per Holy Stone grade in hundredths of one percentage point.';

        CREATE FUNCTION public.stamp_holy_spirit_balance_settings_update()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $stamp_holy_spirit_balance_settings_update$
        BEGIN
            NEW.revision := OLD.revision + 1;
            NEW.updated_at := now();
            RETURN NEW;
        END;
        $stamp_holy_spirit_balance_settings_update$;

        CREATE TRIGGER trg_holy_spirit_balance_settings_update
        BEFORE UPDATE ON public.holy_spirit_balance_settings
        FOR EACH ROW
        EXECUTE FUNCTION
            public.stamp_holy_spirit_balance_settings_update();

        UPDATE public.character_items
        SET holy_socket1_value = CASE
                WHEN holy_socket1_effect_id IN (9, 10, 13)
                     AND holy_socket1_level BETWEEN 1 AND 10
                     AND COALESCE(
                         holy_socket1_value,
                         (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                             ::smallint[])[holy_socket1_level]) >
                         holy_socket1_level * CASE holy_socket1_effect_id
                             WHEN 9 THEN 55
                             WHEN 10 THEN 55
                             WHEN 13 THEN 60
                         END
                    THEN holy_socket1_level *
                         CASE holy_socket1_effect_id
                             WHEN 9 THEN 55
                             WHEN 10 THEN 55
                             WHEN 13 THEN 60
                         END
                ELSE holy_socket1_value
            END,
            holy_socket2_value = CASE
                WHEN holy_socket2_effect_id IN (9, 10, 13)
                     AND holy_socket2_level BETWEEN 1 AND 10
                     AND COALESCE(
                         holy_socket2_value,
                         (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                             ::smallint[])[holy_socket2_level]) >
                         holy_socket2_level * CASE holy_socket2_effect_id
                             WHEN 9 THEN 55
                             WHEN 10 THEN 55
                             WHEN 13 THEN 60
                         END
                    THEN holy_socket2_level *
                         CASE holy_socket2_effect_id
                             WHEN 9 THEN 55
                             WHEN 10 THEN 55
                             WHEN 13 THEN 60
                         END
                ELSE holy_socket2_value
            END,
            holy_socket3_value = CASE
                WHEN holy_socket3_effect_id IN (9, 10, 13)
                     AND holy_socket3_level BETWEEN 1 AND 10
                     AND COALESCE(
                         holy_socket3_value,
                         (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                             ::smallint[])[holy_socket3_level]) >
                         holy_socket3_level * CASE holy_socket3_effect_id
                             WHEN 9 THEN 55
                             WHEN 10 THEN 55
                             WHEN 13 THEN 60
                         END
                    THEN holy_socket3_level *
                         CASE holy_socket3_effect_id
                             WHEN 9 THEN 55
                             WHEN 10 THEN 55
                             WHEN 13 THEN 60
                         END
                ELSE holy_socket3_value
            END,
            holy_socket4_value = CASE
                WHEN holy_socket4_effect_id IN (9, 10, 13)
                     AND holy_socket4_level BETWEEN 1 AND 10
                     AND COALESCE(
                         holy_socket4_value,
                         (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                             ::smallint[])[holy_socket4_level]) >
                         holy_socket4_level * CASE holy_socket4_effect_id
                             WHEN 9 THEN 55
                             WHEN 10 THEN 55
                             WHEN 13 THEN 60
                         END
                    THEN holy_socket4_level *
                         CASE holy_socket4_effect_id
                             WHEN 9 THEN 55
                             WHEN 10 THEN 55
                             WHEN 13 THEN 60
                         END
                ELSE holy_socket4_value
            END
        WHERE holy_socket1_effect_id IN (9, 10, 13)
                  AND holy_socket1_level BETWEEN 1 AND 10
                  AND COALESCE(
                      holy_socket1_value,
                      (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                          ::smallint[])[holy_socket1_level]) >
                      holy_socket1_level * CASE holy_socket1_effect_id
                          WHEN 9 THEN 55
                          WHEN 10 THEN 55
                          WHEN 13 THEN 60
                      END
               OR holy_socket2_effect_id IN (9, 10, 13)
                  AND holy_socket2_level BETWEEN 1 AND 10
                  AND COALESCE(
                      holy_socket2_value,
                      (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                          ::smallint[])[holy_socket2_level]) >
                      holy_socket2_level * CASE holy_socket2_effect_id
                          WHEN 9 THEN 55
                          WHEN 10 THEN 55
                          WHEN 13 THEN 60
                      END
               OR holy_socket3_effect_id IN (9, 10, 13)
                  AND holy_socket3_level BETWEEN 1 AND 10
                  AND COALESCE(
                      holy_socket3_value,
                      (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                          ::smallint[])[holy_socket3_level]) >
                      holy_socket3_level * CASE holy_socket3_effect_id
                          WHEN 9 THEN 55
                          WHEN 10 THEN 55
                          WHEN 13 THEN 60
                      END
               OR holy_socket4_effect_id IN (9, 10, 13)
                  AND holy_socket4_level BETWEEN 1 AND 10
                  AND COALESCE(
                      holy_socket4_value,
                      (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                          ::smallint[])[holy_socket4_level]) >
                      holy_socket4_level * CASE holy_socket4_effect_id
                          WHEN 9 THEN 55
                          WHEN 10 THEN 55
                          WHEN 13 THEN 60
                      END;
        """);
}
