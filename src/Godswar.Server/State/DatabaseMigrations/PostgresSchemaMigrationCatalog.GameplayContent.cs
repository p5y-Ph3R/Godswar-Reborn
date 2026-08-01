namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateGameplayContentRelease() =>
        new(
            "20260801_039_gameplay_content_release",
            "Create immutable, versioned gameplay configuration and its publication pointer",
            GameplayContentSchemaSql +
            GameplayProgressionContentSchemaSql +
            GameplayContentGuardSql +
            GameplayContentPolicySql);

    private const string GameplayContentSchemaSql =
        """
        CREATE TABLE public.gameplay_content_revisions (
            revision varchar(64) PRIMARY KEY,
            map_count integer NOT NULL,
            address_point_count integer NOT NULL,
            link_count integer NOT NULL,
            monster_template_count integer NOT NULL,
            world_boss_count integer NOT NULL,
            pending_world_boss_count integer NOT NULL,
            class_count integer NOT NULL,
            talent_effect_count integer NOT NULL,
            talent_count integer NOT NULL,
            skill_count integer NOT NULL,
            skill_book_count integer NOT NULL,
            source varchar(96) NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ck_gameplay_revisions_revision
                CHECK (revision ~ '^[0-9A-F]{64}$'),
            CONSTRAINT ck_gameplay_revisions_counts CHECK (
                map_count BETWEEN 1 AND 1024 AND
                address_point_count BETWEEN 0 AND 100000 AND
                link_count BETWEEN 0 AND 10000 AND
                monster_template_count BETWEEN 1 AND 100000 AND
                world_boss_count BETWEEN 0 AND 1024 AND
                pending_world_boss_count BETWEEN 0 AND 1024 AND
                class_count BETWEEN 1 AND 128 AND
                talent_effect_count BETWEEN 1 AND 100000 AND
                talent_count BETWEEN 1 AND 100000 AND
                skill_count BETWEEN 1 AND 100000 AND
                skill_book_count BETWEEN 0 AND 100000
            ),
            CONSTRAINT ck_gameplay_revisions_source
                CHECK (btrim(source) <> '')
        );

        CREATE TABLE public.gameplay_map_definitions (
            revision varchar(64) NOT NULL,
            map_id smallint NOT NULL,
            scene_key varchar(96) NOT NULL,
            display_name varchar(128) NOT NULL,
            client_scene_id integer,
            CONSTRAINT pk_gameplay_map_definitions
                PRIMARY KEY (revision, map_id),
            CONSTRAINT fk_gameplay_maps_revision FOREIGN KEY (revision)
                REFERENCES public.gameplay_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_gameplay_maps_id CHECK (map_id >= 0),
            CONSTRAINT ck_gameplay_maps_scene CHECK (btrim(scene_key) <> ''),
            CONSTRAINT ck_gameplay_maps_name CHECK (btrim(display_name) <> '')
        );

        CREATE TABLE public.gameplay_map_address_points (
            revision varchar(64) NOT NULL,
            map_id smallint NOT NULL,
            group_index smallint NOT NULL,
            point_index smallint NOT NULL,
            group_name varchar(128) NOT NULL,
            name varchar(128) NOT NULL,
            pos_x real NOT NULL,
            pos_z real NOT NULL,
            source varchar(255) NOT NULL,
            CONSTRAINT pk_gameplay_map_address_points
                PRIMARY KEY (revision, map_id, group_index, point_index),
            CONSTRAINT fk_gameplay_address_map
                FOREIGN KEY (revision, map_id)
                REFERENCES public.gameplay_map_definitions (revision, map_id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_gameplay_address_indexes
                CHECK (group_index >= 0 AND point_index >= 0),
            CONSTRAINT ck_gameplay_address_x CHECK (
                pos_x NOT IN (
                    'NaN'::real,
                    'Infinity'::real,
                    '-Infinity'::real
                )
            ),
            CONSTRAINT ck_gameplay_address_z CHECK (
                pos_z NOT IN (
                    'NaN'::real,
                    'Infinity'::real,
                    '-Infinity'::real
                )
            ),
            CONSTRAINT ck_gameplay_address_source CHECK (btrim(source) <> '')
        );

        CREATE TABLE public.gameplay_map_links (
            revision varchar(64) NOT NULL,
            map_id smallint NOT NULL,
            link_index smallint NOT NULL,
            target_map_id smallint NOT NULL,
            pos_x real NOT NULL,
            pos_z real NOT NULL,
            source varchar(255) NOT NULL,
            confidence varchar(48) NOT NULL,
            activation varchar(48) NOT NULL,
            note varchar(512) NOT NULL,
            CONSTRAINT pk_gameplay_map_links
                PRIMARY KEY (revision, map_id, link_index, target_map_id),
            CONSTRAINT fk_gameplay_links_source
                FOREIGN KEY (revision, map_id)
                REFERENCES public.gameplay_map_definitions (revision, map_id)
                ON DELETE RESTRICT,
            CONSTRAINT fk_gameplay_links_target
                FOREIGN KEY (revision, target_map_id)
                REFERENCES public.gameplay_map_definitions (revision, map_id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_gameplay_links_ids CHECK (
                link_index >= 0 AND map_id <> target_map_id
            ),
            CONSTRAINT ck_gameplay_links_x CHECK (
                pos_x NOT IN (
                    'NaN'::real,
                    'Infinity'::real,
                    '-Infinity'::real
                )
            ),
            CONSTRAINT ck_gameplay_links_z CHECK (
                pos_z NOT IN (
                    'NaN'::real,
                    'Infinity'::real,
                    '-Infinity'::real
                )
            ),
            CONSTRAINT ck_gameplay_links_source CHECK (btrim(source) <> ''),
            CONSTRAINT ck_gameplay_links_confidence CHECK (
                confidence IN (
                    'captured-span-map',
                    'reciprocal-address-point',
                    'excluded-by-observed-topology'
                )
            ),
            CONSTRAINT ck_gameplay_links_activation CHECK (
                activation IN (
                    'automatic',
                    'disabled-by-world-topology'
                )
            ),
            CONSTRAINT ck_gameplay_links_note CHECK (btrim(note) <> '')
        );

        CREATE TABLE public.gameplay_monster_templates (
            revision varchar(64) NOT NULL,
            source_key varchar(32) NOT NULL,
            source_kind varchar(16) NOT NULL,
            source_map_id smallint,
            scene_key varchar(96) NOT NULL,
            template_key varchar(128) NOT NULL,
            display_name varchar(255) NOT NULL,
            rank varchar(16) NOT NULL,
            is_boss boolean NOT NULL,
            is_elite boolean NOT NULL,
            is_pet boolean NOT NULL,
            collision_range real,
            CONSTRAINT pk_gameplay_monster_templates
                PRIMARY KEY (revision, source_key, template_key),
            CONSTRAINT fk_gameplay_monsters_revision FOREIGN KEY (revision)
                REFERENCES public.gameplay_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT fk_gameplay_monsters_map
                FOREIGN KEY (revision, source_map_id)
                REFERENCES public.gameplay_map_definitions (revision, map_id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_gameplay_monsters_source_key
                CHECK (btrim(source_key) <> ''),
            CONSTRAINT ck_gameplay_monsters_source_kind
                CHECK (btrim(source_kind) <> ''),
            CONSTRAINT ck_gameplay_monsters_template
                CHECK (btrim(template_key) <> ''),
            CONSTRAINT ck_gameplay_monsters_rank CHECK (btrim(rank) <> ''),
            CONSTRAINT ck_gameplay_monsters_collision CHECK (
                collision_range IS NULL OR (
                    collision_range >= 0 AND
                    collision_range NOT IN (
                        'NaN'::real,
                        'Infinity'::real,
                        '-Infinity'::real
                    )
                )
            )
        );

        CREATE INDEX ix_gameplay_monsters_map_template
            ON public.gameplay_monster_templates (
                revision,
                source_map_id,
                template_key
            );

        CREATE TABLE public.gameplay_world_boss_definitions (
            revision varchar(64) NOT NULL,
            map_id smallint NOT NULL,
            scene_key varchar(96) NOT NULL,
            template_key varchar(128) NOT NULL,
            display_name varchar(255) NOT NULL,
            bonus_basis_points integer NOT NULL,
            respawn_interval_seconds integer NOT NULL,
            CONSTRAINT pk_gameplay_world_bosses
                PRIMARY KEY (revision, map_id),
            CONSTRAINT fk_gameplay_bosses_map
                FOREIGN KEY (revision, map_id)
                REFERENCES public.gameplay_map_definitions (revision, map_id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_gameplay_bosses_text CHECK (
                btrim(scene_key) <> '' AND
                btrim(template_key) <> '' AND
                btrim(display_name) <> ''
            ),
            CONSTRAINT ck_gameplay_bosses_bonus
                CHECK (bonus_basis_points BETWEEN 0 AND 100000),
            CONSTRAINT ck_gameplay_bosses_respawn
                CHECK (respawn_interval_seconds BETWEEN 1 AND 2592000)
        );

        CREATE TABLE public.gameplay_pending_world_boss_areas (
            revision varchar(64) NOT NULL,
            map_id smallint NOT NULL,
            scene_key varchar(96) NOT NULL,
            reason varchar(512) NOT NULL,
            CONSTRAINT pk_gameplay_pending_bosses
                PRIMARY KEY (revision, map_id),
            CONSTRAINT fk_gameplay_pending_bosses_map
                FOREIGN KEY (revision, map_id)
                REFERENCES public.gameplay_map_definitions (revision, map_id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_gameplay_pending_bosses_text CHECK (
                btrim(scene_key) <> '' AND btrim(reason) <> ''
            )
        );

        CREATE TABLE public.gameplay_skill_combat_definitions (
            revision varchar(64) NOT NULL,
            skill_id integer NOT NULL,
            target integer NOT NULL,
            affect_obj integer NOT NULL,
            distance real NOT NULL,
            effect_range real NOT NULL,
            property integer NOT NULL,
            mp integer NOT NULL,
            power1 numeric NOT NULL,
            power2 numeric NOT NULL,
            cast_time_seconds numeric NOT NULL,
            cooldown_seconds numeric NOT NULL,
            display_name varchar(128) NOT NULL,
            base_name varchar(128) NOT NULL,
            skill_level smallint,
            class_ids smallint[] NOT NULL,
            previous_skill_id integer,
            min_level integer,
            max_level integer,
            description varchar(16384) NOT NULL,
            stats jsonb NOT NULL,
            CONSTRAINT pk_gameplay_skill_combat
                PRIMARY KEY (revision, skill_id),
            CONSTRAINT fk_gameplay_skills_revision FOREIGN KEY (revision)
                REFERENCES public.gameplay_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_gameplay_skills_id CHECK (skill_id >= 0),
            CONSTRAINT ck_gameplay_skills_distance CHECK (
                distance NOT IN (
                    'NaN'::real,
                    'Infinity'::real,
                    '-Infinity'::real
                )
            ),
            CONSTRAINT ck_gameplay_skills_range CHECK (
                effect_range NOT IN (
                    'NaN'::real,
                    'Infinity'::real,
                    '-Infinity'::real
                )
            ),
            CONSTRAINT ck_gameplay_skills_times CHECK (
                cast_time_seconds BETWEEN 0 AND 3600 AND
                cooldown_seconds BETWEEN 0 AND 2592000
            ),
            CONSTRAINT ck_gameplay_skills_text CHECK (
                btrim(display_name) <> '' AND btrim(base_name) <> ''
            ),
            CONSTRAINT ck_gameplay_skills_classes CHECK (
                array_ndims(class_ids) = 1 AND
                cardinality(class_ids) <= 128
            ),
            CONSTRAINT ck_gameplay_skills_levels CHECK (
                (skill_level IS NULL OR skill_level >= 0) AND
                (min_level IS NULL OR min_level >= 0) AND
                (max_level IS NULL OR max_level >= 0) AND
                (min_level IS NULL OR max_level IS NULL OR min_level <= max_level)
            ),
            CONSTRAINT ck_gameplay_skills_stats CHECK (
                jsonb_typeof(stats) = 'object' AND
                octet_length(stats::text) <= 65536
            )
        );

        CREATE TABLE public.gameplay_content_publication (
            family varchar(16) PRIMARY KEY,
            revision varchar(64) NOT NULL,
            published_at timestamptz NOT NULL DEFAULT now(),
            publisher varchar(64) NOT NULL,
            CONSTRAINT ck_gameplay_publication_family
                CHECK (family = 'gameplay'),
            CONSTRAINT ck_gameplay_publication_publisher
                CHECK (btrim(publisher) <> ''),
            CONSTRAINT fk_gameplay_publication_revision FOREIGN KEY (revision)
                REFERENCES public.gameplay_content_revisions (revision)
                ON DELETE RESTRICT
        );
        """;
}
