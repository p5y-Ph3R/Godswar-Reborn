namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string PetContentSchemaSql =
        """
        CREATE TABLE public.pet_content_revisions (
            revision varchar(64) PRIMARY KEY,
            species_count integer NOT NULL,
            aptitude_count integer NOT NULL,
            native_profile_count integer NOT NULL,
            experience_step_count integer NOT NULL,
            rebirth_step_count integer NOT NULL,
            source varchar(96) NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            sealed_at timestamptz,
            CONSTRAINT ck_pet_content_revisions_revision
                CHECK (revision ~ '^[0-9A-F]{64}$'),
            CONSTRAINT ck_pet_content_revisions_counts CHECK (
                species_count BETWEEN 1 AND 1024 AND
                aptitude_count BETWEEN 1 AND 255 AND
                native_profile_count BETWEEN 1 AND 100000 AND
                experience_step_count BETWEEN 1 AND 254 AND
                rebirth_step_count BETWEEN 1 AND 1000
            ),
            CONSTRAINT ck_pet_content_revisions_source
                CHECK (btrim(source) <> '')
        );

        CREATE TABLE public.pet_content_settings (
            revision varchar(64) PRIMARY KEY,
            minimum_level smallint NOT NULL,
            maximum_level smallint NOT NULL,
            maximum_owned_pet_count smallint NOT NULL,
            maximum_skill_count smallint NOT NULL,
            minimum_merge_level smallint NOT NULL,
            minimum_owner_merge_amity smallint NOT NULL,
            maximum_spirit_items smallint NOT NULL,
            maximum_rebirth_count smallint NOT NULL,
            required_rebirth_spirit_count smallint NOT NULL,
            egg_hatch_runtime_skill_id integer NOT NULL,
            merge_spirit_item_id integer NOT NULL,
            restricted_merge_spirit_item_id integer NOT NULL,
            rebirth_spirit_item_id integer NOT NULL,
            restricted_rebirth_spirit_item_id integer NOT NULL,
            growth_policy_version varchar(48) NOT NULL,
            initial_savvy_policy_version varchar(48) NOT NULL,
            added_savvy_policy_version varchar(48) NOT NULL,
            added_savvy_weights smallint[] NOT NULL,
            CONSTRAINT fk_pet_content_settings_revision
                FOREIGN KEY (revision)
                REFERENCES public.pet_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_settings_levels CHECK (
                minimum_level BETWEEN 1 AND 255 AND
                maximum_level BETWEEN minimum_level AND 255 AND
                minimum_merge_level BETWEEN minimum_level AND maximum_level
            ),
            CONSTRAINT ck_pet_content_settings_limits CHECK (
                maximum_owned_pet_count BETWEEN 1 AND 64 AND
                maximum_skill_count BETWEEN 1 AND 12 AND
                minimum_owner_merge_amity BETWEEN 0 AND 100 AND
                maximum_spirit_items BETWEEN 1 AND 100 AND
                maximum_rebirth_count BETWEEN 1 AND 1000 AND
                required_rebirth_spirit_count BETWEEN 1 AND maximum_spirit_items AND
                egg_hatch_runtime_skill_id > 0
            ),
            CONSTRAINT ck_pet_content_settings_items CHECK (
                merge_spirit_item_id > 0 AND
                restricted_merge_spirit_item_id > 0 AND
                rebirth_spirit_item_id > 0 AND
                restricted_rebirth_spirit_item_id > 0
            ),
            CONSTRAINT ck_pet_content_settings_versions CHECK (
                btrim(growth_policy_version) <> '' AND
                btrim(initial_savvy_policy_version) <> '' AND
                btrim(added_savvy_policy_version) <> ''
            ),
            CONSTRAINT ck_pet_content_settings_weights CHECK (
                cardinality(added_savvy_weights) = 6 AND
                0 < ALL(added_savvy_weights)
            )
        );

        CREATE TABLE public.pet_content_species_definitions (
            revision varchar(64) NOT NULL,
            species_id smallint NOT NULL,
            display_name varchar(64) NOT NULL,
            food_kind smallint NOT NULL,
            starter_skill_id integer NOT NULL,
            starter_skill_name varchar(128) NOT NULL,
            lifetime_values integer[] NOT NULL,
            egg_item_id integer,
            egg_declared_species_id smallint,
            magic_jade_item_id integer NOT NULL,
            CONSTRAINT pk_pet_content_species
                PRIMARY KEY (revision, species_id),
            CONSTRAINT fk_pet_content_species_revision
                FOREIGN KEY (revision)
                REFERENCES public.pet_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT fk_pet_content_species_identity
                FOREIGN KEY (species_id)
                REFERENCES public.pet_templates (species_id)
                ON DELETE RESTRICT,
            CONSTRAINT fk_pet_content_species_declared_identity
                FOREIGN KEY (egg_declared_species_id)
                REFERENCES public.pet_templates (species_id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_species_id
                CHECK (species_id BETWEEN 1 AND 255),
            CONSTRAINT ck_pet_content_species_text CHECK (
                btrim(display_name) <> '' AND
                btrim(starter_skill_name) <> ''
            ),
            CONSTRAINT ck_pet_content_species_values CHECK (
                food_kind BETWEEN 1 AND 3 AND
                starter_skill_id > 0 AND
                cardinality(lifetime_values) BETWEEN 1 AND 64 AND
                0 < ALL(lifetime_values) AND
                (egg_item_id IS NULL OR egg_item_id > 0) AND
                magic_jade_item_id > 0
            )
        );

        CREATE UNIQUE INDEX ux_pet_content_species_egg
            ON public.pet_content_species_definitions (revision, egg_item_id)
            WHERE egg_item_id IS NOT NULL;

        CREATE TABLE public.pet_content_aptitude_definitions (
            revision varchar(64) NOT NULL,
            aptitude smallint NOT NULL,
            name_key varchar(32) NOT NULL,
            display_name varchar(32) NOT NULL,
            is_server_extension boolean NOT NULL,
            minimum_total_growth numeric(18, 6) NOT NULL,
            maximum_total_growth numeric(18, 6) NOT NULL,
            maximum_growth_stat_deviation numeric(8, 6) NOT NULL,
            minimum_initial_savvy integer NOT NULL,
            maximum_initial_savvy integer NOT NULL,
            maximum_initial_savvy_stat_deviation numeric(8, 6) NOT NULL,
            minimum_added_savvy integer NOT NULL,
            maximum_added_savvy integer NOT NULL,
            CONSTRAINT pk_pet_content_aptitudes
                PRIMARY KEY (revision, aptitude),
            CONSTRAINT fk_pet_content_aptitudes_revision
                FOREIGN KEY (revision)
                REFERENCES public.pet_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT fk_pet_content_aptitudes_identity
                FOREIGN KEY (aptitude)
                REFERENCES public.pet_aptitude_templates (aptitude)
                ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_aptitudes_id
                CHECK (aptitude BETWEEN 1 AND 255),
            CONSTRAINT ck_pet_content_aptitudes_text CHECK (
                btrim(name_key) <> '' AND btrim(display_name) <> ''
            ),
            CONSTRAINT ck_pet_content_aptitudes_growth CHECK (
                minimum_total_growth > 0 AND
                maximum_total_growth >= minimum_total_growth AND
                maximum_growth_stat_deviation > 0 AND
                maximum_growth_stat_deviation <= 0.25
            ),
            CONSTRAINT ck_pet_content_aptitudes_savvy CHECK (
                minimum_initial_savvy > 0 AND
                maximum_initial_savvy >= minimum_initial_savvy AND
                maximum_initial_savvy_stat_deviation > 0 AND
                maximum_initial_savvy_stat_deviation <= 0.25 AND
                minimum_added_savvy > 0 AND
                maximum_added_savvy >= minimum_added_savvy
            )
        );

        CREATE TABLE public.pet_content_native_profiles (
            revision varchar(64) NOT NULL,
            species_id smallint NOT NULL,
            aptitude smallint NOT NULL,
            starting_agility numeric(18, 6) NOT NULL,
            starting_strength numeric(18, 6) NOT NULL,
            starting_accuracy numeric(18, 6) NOT NULL,
            starting_technique numeric(18, 6) NOT NULL,
            starting_wisdom numeric(18, 6) NOT NULL,
            starting_luck numeric(18, 6) NOT NULL,
            genius_agility numeric(18, 6) NOT NULL,
            genius_strength numeric(18, 6) NOT NULL,
            genius_accuracy numeric(18, 6) NOT NULL,
            genius_technique numeric(18, 6) NOT NULL,
            genius_wisdom numeric(18, 6) NOT NULL,
            genius_luck numeric(18, 6) NOT NULL,
            native_quality integer NOT NULL,
            native_samsara integer NOT NULL,
            native_genius integer NOT NULL,
            starter_skill_id integer NOT NULL,
            native_skill_count integer NOT NULL,
            native_procreate integer NOT NULL,
            lifetime integer NOT NULL,
            CONSTRAINT pk_pet_content_native_profiles
                PRIMARY KEY (revision, species_id, aptitude),
            CONSTRAINT fk_pet_content_native_species
                FOREIGN KEY (revision, species_id)
                REFERENCES public.pet_content_species_definitions
                    (revision, species_id)
                ON DELETE RESTRICT,
            CONSTRAINT fk_pet_content_native_aptitude
                FOREIGN KEY (revision, aptitude)
                REFERENCES public.pet_content_aptitude_definitions
                    (revision, aptitude)
                ON DELETE RESTRICT,
            CONSTRAINT ck_pet_content_native_traits CHECK (
                starting_agility >= 0 AND starting_strength >= 0 AND
                starting_accuracy >= 0 AND starting_technique >= 0 AND
                starting_wisdom >= 0 AND starting_luck >= 0 AND
                genius_agility >= 0 AND genius_strength >= 0 AND
                genius_accuracy >= 0 AND genius_technique >= 0 AND
                genius_wisdom >= 0 AND genius_luck >= 0
            ),
            CONSTRAINT ck_pet_content_native_values CHECK (
                native_quality >= 0 AND native_samsara >= 0 AND
                native_genius >= 0 AND starter_skill_id > 0 AND
                native_skill_count > 0 AND native_procreate >= 0 AND
                lifetime > 0
            )
        );
        """;
}
