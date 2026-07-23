namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateStarterConsumableTemplates() => new(
        "20260723_005_starter_consumable_templates",
        "Reconcile client-authoritative starter HP and MP potion templates",
        """
        INSERT INTO public.item_templates (
            id,
            kind,
            name_key,
            display_name,
            equipment_slot,
            class_ids,
            min_level,
            max_level,
            hand,
            skill_flag,
            texture,
            icon,
            stats
        )
        VALUES
            (
                4000,
                'consume item',
                'HPPotion_a',
                'Small Healing Potion',
                -1,
                '{}'::smallint[],
                NULL,
                NULL,
                NULL,
                NULL,
                './Localization/en_us/UI/Texture/Icon.gwo',
                '252,972',
                '{
                    "ID": "4000",
                    "Type": "consume item",
                    "Texture": "./Localization/en_us/UI/Texture/Icon.gwo",
                    "Icon": "252,972",
                    "Random": "250",
                    "Distribution": "50,200",
                    "Money": "5",
                    "Overlap": "99",
                    "Use": "1",
                    "Skill": "3100",
                    "ItemType": "10"
                }'::jsonb
            ),
            (
                4030,
                'consume item',
                'MPPotion_a',
                'Small Mana Potion',
                -1,
                '{}'::smallint[],
                NULL,
                NULL,
                NULL,
                NULL,
                './Localization/en_us/UI/Texture/Icon.gwo',
                '432,972',
                '{
                    "ID": "4030",
                    "Type": "consume item",
                    "Texture": "./Localization/en_us/UI/Texture/Icon.gwo",
                    "Icon": "432,972",
                    "Random": "0",
                    "Distribution": "50,200",
                    "Money": "5",
                    "Overlap": "99",
                    "Use": "1",
                    "Skill": "3120",
                    "ItemType": "11"
                }'::jsonb
            )
        ON CONFLICT (id) DO UPDATE
        SET kind = EXCLUDED.kind,
            name_key = EXCLUDED.name_key,
            display_name = EXCLUDED.display_name,
            equipment_slot = EXCLUDED.equipment_slot,
            class_ids = EXCLUDED.class_ids,
            min_level = EXCLUDED.min_level,
            max_level = EXCLUDED.max_level,
            hand = EXCLUDED.hand,
            skill_flag = EXCLUDED.skill_flag,
            texture = EXCLUDED.texture,
            icon = EXCLUDED.icon,
            stats = EXCLUDED.stats;
        """);

    private static PostgresSchemaMigration CreateArchiveLegacyCharacterKitbag() => new(
        "20260723_006_archive_legacy_character_kitbag",
        "Archive and retire the obsolete compact character kitbag table",
        """
        CREATE SCHEMA IF NOT EXISTS legacy;

        CREATE TABLE IF NOT EXISTS legacy.character_kitbag_archive (
            user_id integer PRIMARY KEY,
            kitbag_1 varchar(4000),
            kitbag_2 varchar(4000),
            kitbag_3 varchar(4000),
            kitbag_4 varchar(4000),
            storage varchar(4000),
            equip varchar(2000),
            archived_at timestamptz NOT NULL
        );

        DO $archive_character_kitbag$
        DECLARE
            source_table regclass := to_regclass('public.character_kitbag');
            blocking_object text;
        BEGIN
            IF source_table IS NULL THEN
                RAISE EXCEPTION
                    'Cannot retire public.character_kitbag because the source table is absent';
            END IF;

            IF to_regclass('public.character_items') IS NULL
               OR to_regclass('public.character_item_loadout') IS NULL THEN
                RAISE EXCEPTION
                    'Cannot retire public.character_kitbag before the authoritative inventory table and projection exist';
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM public.server_data_migrations
                WHERE migration_key = '20260721_legacy_character_kitbag_import'
            ) THEN
                RAISE EXCEPTION
                    'Cannot retire public.character_kitbag before its one-time import is recorded';
            END IF;

            EXECUTE 'LOCK TABLE public.character_kitbag IN ACCESS EXCLUSIVE MODE';

            SELECT constraint_name
            INTO blocking_object
            FROM (
                SELECT constraint_name
                FROM information_schema.referential_constraints
                WHERE unique_constraint_schema = 'public'
                  AND unique_constraint_name IN (
                      SELECT constraint_name
                      FROM information_schema.table_constraints
                      WHERE table_schema = 'public'
                        AND table_name = 'character_kitbag'
                  )
            ) AS inbound_foreign_key
            LIMIT 1;

            IF blocking_object IS NOT NULL THEN
                RAISE EXCEPTION
                    'Cannot retire public.character_kitbag; foreign key % still references it',
                    blocking_object;
            END IF;

            SELECT format('%I.%I', namespace.nspname, dependent.relname)
            INTO blocking_object
            FROM pg_depend dependency
            JOIN pg_rewrite rewrite
              ON rewrite.oid = dependency.objid
            JOIN pg_class dependent
              ON dependent.oid = rewrite.ev_class
            JOIN pg_namespace namespace
              ON namespace.oid = dependent.relnamespace
            WHERE dependency.refobjid = source_table
              AND dependent.oid <> source_table
            LIMIT 1;

            IF blocking_object IS NOT NULL THEN
                RAISE EXCEPTION
                    'Cannot retire public.character_kitbag; view % still depends on it',
                    blocking_object;
            END IF;

            SELECT trigger_name
            INTO blocking_object
            FROM information_schema.triggers
            WHERE event_object_schema = 'public'
              AND event_object_table = 'character_kitbag'
            LIMIT 1;

            IF blocking_object IS NOT NULL THEN
                RAISE EXCEPTION
                    'Cannot retire public.character_kitbag; trigger % still depends on it',
                    blocking_object;
            END IF;

            INSERT INTO legacy.character_kitbag_archive (
                user_id,
                kitbag_1,
                kitbag_2,
                kitbag_3,
                kitbag_4,
                storage,
                equip,
                archived_at
            )
            SELECT
                user_id,
                kitbag_1,
                kitbag_2,
                kitbag_3,
                kitbag_4,
                storage,
                equip,
                clock_timestamp()
            FROM public.character_kitbag
            ON CONFLICT (user_id) DO UPDATE
            SET kitbag_1 = EXCLUDED.kitbag_1,
                kitbag_2 = EXCLUDED.kitbag_2,
                kitbag_3 = EXCLUDED.kitbag_3,
                kitbag_4 = EXCLUDED.kitbag_4,
                storage = EXCLUDED.storage,
                equip = EXCLUDED.equip,
                archived_at = EXCLUDED.archived_at;

            -- Verify exact row parity in both directions. The archive timestamp is
            -- provenance metadata and is intentionally excluded from this comparison.
            IF EXISTS (
                SELECT
                    user_id,
                    kitbag_1,
                    kitbag_2,
                    kitbag_3,
                    kitbag_4,
                    storage,
                    equip
                FROM public.character_kitbag
                EXCEPT
                SELECT
                    user_id,
                    kitbag_1,
                    kitbag_2,
                    kitbag_3,
                    kitbag_4,
                    storage,
                    equip
                FROM legacy.character_kitbag_archive
            )
            OR EXISTS (
                SELECT
                    user_id,
                    kitbag_1,
                    kitbag_2,
                    kitbag_3,
                    kitbag_4,
                    storage,
                    equip
                FROM legacy.character_kitbag_archive
                EXCEPT
                SELECT
                    user_id,
                    kitbag_1,
                    kitbag_2,
                    kitbag_3,
                    kitbag_4,
                    storage,
                    equip
                FROM public.character_kitbag
            ) THEN
                RAISE EXCEPTION
                    'Cannot retire public.character_kitbag; its archive does not exactly match the source';
            END IF;
        END
        $archive_character_kitbag$;

        -- RESTRICT is the final dependency proof and prevents a cascading drop
        -- if an unanticipated database object was created concurrently.
        DROP TABLE IF EXISTS public.character_kitbag RESTRICT;
        """);

    private static PostgresSchemaMigration CreateCharacterItemTemplateForeignKey() => new(
        "20260723_007_character_item_template_foreign_key",
        "Require every authoritative inventory row to reference a known item template",
        """
        DO $add_character_item_template_foreign_key$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE conrelid = 'public.character_items'::regclass
                  AND conname = 'fk_character_items_prop_id_item_templates'
            ) THEN
                ALTER TABLE public.character_items
                    ADD CONSTRAINT fk_character_items_prop_id_item_templates
                    FOREIGN KEY (prop_id)
                    REFERENCES public.item_templates (id)
                    ON DELETE RESTRICT
                    NOT VALID;
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_constraint constraint_definition
                JOIN pg_attribute source_column
                  ON source_column.attrelid = constraint_definition.conrelid
                 AND source_column.attname = 'prop_id'
                 AND constraint_definition.conkey = ARRAY[source_column.attnum]::smallint[]
                JOIN pg_attribute target_column
                  ON target_column.attrelid = constraint_definition.confrelid
                 AND target_column.attname = 'id'
                 AND constraint_definition.confkey = ARRAY[target_column.attnum]::smallint[]
                WHERE constraint_definition.conrelid = 'public.character_items'::regclass
                  AND constraint_definition.conname = 'fk_character_items_prop_id_item_templates'
                  AND constraint_definition.contype = 'f'
                  AND constraint_definition.confrelid = 'public.item_templates'::regclass
                  AND constraint_definition.confdeltype = 'r'
            ) THEN
                RAISE EXCEPTION
                    'Constraint fk_character_items_prop_id_item_templates exists with an unexpected definition';
            END IF;
        END
        $add_character_item_template_foreign_key$;

        -- Adding the constraint as NOT VALID makes the operation explicit:
        -- new writes are protected first, then all retained inventory is
        -- proven valid without deleting or rewriting a single item row.
        ALTER TABLE public.character_items
            VALIDATE CONSTRAINT fk_character_items_prop_id_item_templates;
        """);
}
