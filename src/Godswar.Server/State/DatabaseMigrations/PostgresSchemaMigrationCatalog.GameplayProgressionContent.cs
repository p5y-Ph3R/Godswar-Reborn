namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string GameplayProgressionContentSchemaSql =
        """
        CREATE TABLE public.gameplay_class_definitions (
            revision varchar(64) NOT NULL,
            id smallint NOT NULL,
            name varchar(32) NOT NULL,
            display_name varchar(64) NOT NULL,
            source varchar(128) NOT NULL,
            CONSTRAINT pk_gameplay_class_definitions
                PRIMARY KEY (revision, id),
            CONSTRAINT fk_gameplay_classes_revision FOREIGN KEY (revision)
                REFERENCES public.gameplay_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_gameplay_classes_id CHECK (id >= 0),
            CONSTRAINT ck_gameplay_classes_text CHECK (
                btrim(name) <> '' AND btrim(display_name) <> ''
            )
        );

        CREATE TABLE public.gameplay_talent_effect_definitions (
            revision varchar(64) NOT NULL,
            id smallint NOT NULL,
            key varchar(32) NOT NULL,
            display_name varchar(128) NOT NULL,
            percent boolean NOT NULL,
            CONSTRAINT pk_gameplay_talent_effect_definitions
                PRIMARY KEY (revision, id),
            CONSTRAINT fk_gameplay_talent_effects_revision
                FOREIGN KEY (revision)
                REFERENCES public.gameplay_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_gameplay_talent_effects_id CHECK (id >= 0),
            CONSTRAINT ck_gameplay_talent_effects_text CHECK (
                btrim(key) <> '' AND btrim(display_name) <> ''
            )
        );

        CREATE TABLE public.gameplay_talent_definitions (
            revision varchar(64) NOT NULL,
            id integer NOT NULL,
            class_id smallint NOT NULL,
            tree_order smallint NOT NULL,
            name varchar(128) NOT NULL,
            prefix_id integer NOT NULL,
            required_prefix_rank integer NOT NULL,
            required_total_rank integer NOT NULL,
            equip_request integer NOT NULL,
            effect_type varchar(32) NOT NULL,
            effect_id smallint NOT NULL,
            effect_value numeric NOT NULL,
            is_percent boolean NOT NULL,
            icon_x integer NOT NULL,
            icon_y integer NOT NULL,
            icon_width integer NOT NULL,
            icon_height integer NOT NULL,
            stats jsonb NOT NULL,
            CONSTRAINT pk_gameplay_talent_definitions
                PRIMARY KEY (revision, id),
            CONSTRAINT fk_gameplay_talents_class
                FOREIGN KEY (revision, class_id)
                REFERENCES public.gameplay_class_definitions (revision, id)
                ON DELETE RESTRICT,
            CONSTRAINT fk_gameplay_talents_effect
                FOREIGN KEY (revision, effect_id)
                REFERENCES public.gameplay_talent_effect_definitions
                    (revision, id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_gameplay_talents_identity CHECK (
                id >= 0 AND tree_order >= 0
            ),
            CONSTRAINT ck_gameplay_talents_requirements CHECK (
                required_prefix_rank >= 0 AND required_total_rank >= 0
            ),
            CONSTRAINT ck_gameplay_talents_text CHECK (
                btrim(name) <> '' AND btrim(effect_type) <> ''
            ),
            CONSTRAINT ck_gameplay_talents_icon CHECK (
                icon_width >= 0 AND icon_height >= 0
            ),
            CONSTRAINT ck_gameplay_talents_stats CHECK (
                jsonb_typeof(stats) = 'object' AND
                octet_length(stats::text) <= 65536
            )
        );

        CREATE INDEX ix_gameplay_talents_class
            ON public.gameplay_talent_definitions (
                revision,
                class_id,
                tree_order
            );

        CREATE TABLE public.gameplay_skill_book_definitions (
            revision varchar(64) NOT NULL,
            item_id integer NOT NULL,
            name_key varchar(128) NOT NULL,
            display_name varchar(128) NOT NULL,
            skill_id integer NOT NULL,
            base_name varchar(128) NOT NULL,
            skill_level smallint,
            class_ids smallint[] NOT NULL,
            min_level integer,
            max_level integer,
            previous_skill_id integer,
            stats jsonb NOT NULL,
            CONSTRAINT pk_gameplay_skill_book_definitions
                PRIMARY KEY (revision, item_id),
            CONSTRAINT fk_gameplay_skill_books_skill
                FOREIGN KEY (revision, skill_id)
                REFERENCES public.gameplay_skill_combat_definitions
                    (revision, skill_id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_gameplay_skill_books_id CHECK (item_id > 0),
            CONSTRAINT ck_gameplay_skill_books_text CHECK (
                btrim(name_key) <> '' AND
                btrim(display_name) <> '' AND
                btrim(base_name) <> ''
            ),
            CONSTRAINT ck_gameplay_skill_books_classes CHECK (
                array_ndims(class_ids) = 1 AND
                cardinality(class_ids) <= 128
            ),
            CONSTRAINT ck_gameplay_skill_books_levels CHECK (
                (skill_level IS NULL OR skill_level >= 0) AND
                (min_level IS NULL OR min_level >= 0) AND
                (max_level IS NULL OR max_level >= 0) AND
                (min_level IS NULL OR max_level IS NULL OR min_level <= max_level)
            ),
            CONSTRAINT ck_gameplay_skill_books_stats CHECK (
                jsonb_typeof(stats) = 'object' AND
                octet_length(stats::text) <= 65536
            )
        );
        """;
}
