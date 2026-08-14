namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string PetOwnerMergeContentSchemaSql =
        """
        CREATE TABLE public.pet_owner_merge_effect_types (
            effect_code smallint PRIMARY KEY,
            effect_key varchar(48) NOT NULL UNIQUE,
            display_name varchar(64) NOT NULL,
            CONSTRAINT ck_pet_owner_merge_effect_types_text CHECK (
                btrim(effect_key) <> '' AND btrim(display_name) <> ''
            )
        );

        INSERT INTO public.pet_owner_merge_effect_types (
            effect_code, effect_key, display_name
        ) VALUES
            (0,  'max_health', 'Maximum HP'),
            (1,  'max_mana', 'Maximum MP'),
            (2,  'hit_rate', 'Hit Rate'),
            (3,  'dodge_rate', 'Dodge Rate'),
            (4,  'physical_attack', 'Physical Attack'),
            (5,  'physical_defense', 'Physical Defense'),
            (6,  'magic_attack', 'Magical Attack'),
            (7,  'magic_defense', 'Magical Defense'),
            (10, 'damage_absorption', 'Damage Absorption'),
            (23, 'physical_damage_increase', 'Physical Damage Increase'),
            (24, 'magic_damage_increase', 'Magical Damage Increase'),
            (29, 'physical_damage_reduction', 'Physical Damage Reduction'),
            (30, 'magic_damage_reduction', 'Magical Damage Reduction'),
            (32, 'critical_damage_reduction', 'Critical Damage Reduction'),
            (34, 'life_absorption', 'Life Absorption'),
            (38, 'damage_rebound', 'Damage Rebound');

        CREATE TABLE public.pet_owner_merge_savvy_types (
            savvy_key varchar(16) PRIMARY KEY,
            stat_code smallint NOT NULL UNIQUE,
            display_name varchar(32) NOT NULL,
            CONSTRAINT ck_pet_owner_merge_savvy_types_code
                CHECK (stat_code BETWEEN 1 AND 6),
            CONSTRAINT ck_pet_owner_merge_savvy_types_text CHECK (
                btrim(savvy_key) <> '' AND btrim(display_name) <> ''
            )
        );

        INSERT INTO public.pet_owner_merge_savvy_types (
            savvy_key, stat_code, display_name
        ) VALUES
            ('agility', 1, 'Agility'),
            ('strength', 2, 'Strength'),
            ('accuracy', 3, 'Accuracy'),
            ('technique', 4, 'Technique'),
            ('wisdom', 5, 'Wisdom'),
            ('luck', 6, 'Luck');

        CREATE TABLE public.pet_owner_merge_content_revisions (
            revision varchar(64) PRIMARY KEY,
            policy_version varchar(64) NOT NULL,
            effect_base_count smallint NOT NULL,
            band_count smallint NOT NULL,
            rate_count smallint NOT NULL,
            source varchar(96) NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            sealed_at timestamptz,
            CONSTRAINT ck_pet_owner_merge_revisions_revision
                CHECK (revision ~ '^[0-9A-F]{64}$'),
            CONSTRAINT ck_pet_owner_merge_revisions_text CHECK (
                btrim(policy_version) <> '' AND
                btrim(source) <> ''
            ),
            CONSTRAINT ck_pet_owner_merge_revisions_counts CHECK (
                effect_base_count = 16 AND
                band_count = 5 AND
                rate_count = 95
            )
        );

        CREATE TABLE public.pet_owner_merge_effect_bases (
            revision varchar(64) NOT NULL,
            effect_code smallint NOT NULL,
            base_value numeric(20, 9) NOT NULL,
            CONSTRAINT pk_pet_owner_merge_effect_bases
                PRIMARY KEY (revision, effect_code),
            CONSTRAINT fk_pet_owner_merge_effect_bases_revision
                FOREIGN KEY (revision)
                REFERENCES public.pet_owner_merge_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT fk_pet_owner_merge_effect_bases_type
                FOREIGN KEY (effect_code)
                REFERENCES public.pet_owner_merge_effect_types (effect_code)
                ON DELETE RESTRICT,
            CONSTRAINT ck_pet_owner_merge_effect_code CHECK (
                effect_code IN (
                    0, 1, 2, 3, 4, 5, 6, 7,
                    10, 23, 24, 29, 30, 32, 34, 38
                )
            ),
            CONSTRAINT ck_pet_owner_merge_effect_base_value
                CHECK (base_value >= 0)
        );

        CREATE TABLE public.pet_owner_merge_savvy_bands (
            revision varchar(64) NOT NULL,
            band_index smallint NOT NULL,
            minimum_savvy numeric(20, 6) NOT NULL,
            maximum_savvy numeric(20, 6),
            CONSTRAINT pk_pet_owner_merge_savvy_bands
                PRIMARY KEY (revision, band_index),
            CONSTRAINT fk_pet_owner_merge_savvy_bands_revision
                FOREIGN KEY (revision)
                REFERENCES public.pet_owner_merge_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_pet_owner_merge_savvy_band_index
                CHECK (band_index BETWEEN 1 AND 5),
            CONSTRAINT ck_pet_owner_merge_savvy_band_bounds CHECK (
                minimum_savvy >= 0 AND
                (
                    maximum_savvy IS NULL OR
                    maximum_savvy > minimum_savvy
                )
            )
        );

        CREATE TABLE public.pet_owner_merge_rates (
            revision varchar(64) NOT NULL,
            source_savvy varchar(16) NOT NULL,
            effect_code smallint NOT NULL,
            band_index smallint NOT NULL,
            rate_per_savvy numeric(20, 9) NOT NULL,
            CONSTRAINT pk_pet_owner_merge_rates PRIMARY KEY (
                revision, source_savvy, effect_code, band_index
            ),
            CONSTRAINT fk_pet_owner_merge_rates_effect FOREIGN KEY (
                revision, effect_code
            ) REFERENCES public.pet_owner_merge_effect_bases (
                revision, effect_code
            ) ON DELETE RESTRICT,
            CONSTRAINT fk_pet_owner_merge_rates_band FOREIGN KEY (
                revision, band_index
            ) REFERENCES public.pet_owner_merge_savvy_bands (
                revision, band_index
            ) ON DELETE RESTRICT,
            CONSTRAINT fk_pet_owner_merge_rates_savvy
                FOREIGN KEY (source_savvy)
                REFERENCES public.pet_owner_merge_savvy_types (savvy_key)
                ON DELETE RESTRICT,
            CONSTRAINT ck_pet_owner_merge_rate_value
                CHECK (rate_per_savvy >= 0),
            CONSTRAINT ck_pet_owner_merge_rate_mapping CHECK (
                (
                    source_savvy = 'agility' AND
                    effect_code IN (1, 2, 6, 38)
                ) OR (
                    source_savvy = 'strength' AND
                    effect_code IN (0, 5, 34)
                ) OR (
                    source_savvy = 'accuracy' AND
                    effect_code IN (2, 4, 7)
                ) OR (
                    source_savvy = 'technique' AND
                    effect_code IN (3, 29, 30)
                ) OR (
                    source_savvy = 'wisdom' AND
                    effect_code IN (0, 23, 32)
                ) OR (
                    source_savvy = 'luck' AND
                    effect_code IN (10, 24, 38)
                )
            )
        );

        CREATE TABLE public.pet_owner_merge_content_publication (
            family varchar(32) PRIMARY KEY,
            revision varchar(64) NOT NULL,
            published_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ck_pet_owner_merge_publication_family
                CHECK (family = 'pet-owner-merge'),
            CONSTRAINT fk_pet_owner_merge_publication_revision
                FOREIGN KEY (revision)
                REFERENCES public.pet_owner_merge_content_revisions (revision)
                ON DELETE RESTRICT
        );

        CREATE VIEW public.published_pet_owner_merge_balance AS
        SELECT publication.revision,
               revision.policy_version,
               revision.source,
               publication.published_at,
               savvy.stat_code AS source_stat_code,
               savvy.savvy_key AS source_savvy,
               savvy.display_name AS source_display_name,
               effect.effect_code,
               effect.effect_key,
               effect.display_name AS effect_display_name,
               effect_base.base_value,
               band.band_index,
               band.minimum_savvy,
               band.maximum_savvy,
               rate.rate_per_savvy
        FROM public.pet_owner_merge_content_publication publication
        JOIN public.pet_owner_merge_content_revisions revision
          ON revision.revision = publication.revision
        JOIN public.pet_owner_merge_rates rate
          ON rate.revision = publication.revision
        JOIN public.pet_owner_merge_effect_bases effect_base
          ON effect_base.revision = rate.revision
         AND effect_base.effect_code = rate.effect_code
        JOIN public.pet_owner_merge_savvy_bands band
          ON band.revision = rate.revision
         AND band.band_index = rate.band_index
        JOIN public.pet_owner_merge_effect_types effect
          ON effect.effect_code = rate.effect_code
        JOIN public.pet_owner_merge_savvy_types savvy
          ON savvy.savvy_key = rate.source_savvy
        WHERE publication.family = 'pet-owner-merge';

        ALTER TABLE public.character_pet_character_bonuses
            ADD COLUMN balance_revision varchar(64),
            ADD CONSTRAINT fk_character_pet_bonuses_balance_revision
                FOREIGN KEY (balance_revision)
                REFERENCES public.pet_owner_merge_content_revisions (revision)
                ON DELETE RESTRICT;

        CREATE INDEX ix_character_pet_bonuses_balance_revision
            ON public.character_pet_character_bonuses (
                balance_revision, pet_id
            );
        """;
}
