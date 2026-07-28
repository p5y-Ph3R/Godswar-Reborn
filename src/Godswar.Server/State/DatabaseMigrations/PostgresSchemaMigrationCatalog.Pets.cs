namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetSystemFoundation() => new(
        "20260728_010_pet_foundation",
        "Create the authoritative pet persistence foundation and seed client facts",
        """
        CREATE TABLE IF NOT EXISTS public.pet_templates (
            species_id smallint PRIMARY KEY
                CHECK (species_id BETWEEN 1 AND 32767),
            display_name varchar(64) NOT NULL
                CHECK (btrim(display_name) <> ''),
            female_model varchar(128) NOT NULL
                CHECK (lower(right(female_model, 4)) = '.jcs'),
            female_texture varchar(128) NOT NULL
                CHECK (lower(right(female_texture, 4)) = '.gwo'),
            female_icon varchar(32) NOT NULL
                CHECK (female_icon ~ '^[0-9]+,[0-9]+$'),
            male_model varchar(128) NOT NULL
                CHECK (lower(right(male_model, 4)) = '.jcs'),
            male_texture varchar(128) NOT NULL
                CHECK (lower(right(male_texture, 4)) = '.gwo'),
            male_icon varchar(32) NOT NULL
                CHECK (male_icon ~ '^[0-9]+,[0-9]+$'),
            samsara_appearance_thresholds smallint[] NOT NULL
                DEFAULT ARRAY[0, 8, 20]::smallint[],
            source_path varchar(255) NOT NULL
                DEFAULT 'Localization/en_us/Settings/Sys/Pet.xml',
            CONSTRAINT ck_pet_templates_samsara_thresholds CHECK (
                cardinality(samsara_appearance_thresholds) = 3
                AND samsara_appearance_thresholds[1] = 0
                AND samsara_appearance_thresholds[2]
                    > samsara_appearance_thresholds[1]
                AND samsara_appearance_thresholds[3]
                    > samsara_appearance_thresholds[2]
            )
        );

        INSERT INTO public.pet_templates (
            species_id,
            display_name,
            female_model,
            female_texture,
            female_icon,
            male_model,
            male_texture,
            male_icon
        )
        VALUES
            (1, 'Rock Elf', 'stone_female_001.jcs', 'stone_female_001.gwo', '0,900', 'stone_male_001.jcs', 'stone_male_001.gwo', '36,900'),
            (2, 'Flower Pixie', 'flower_female_001.jcs', 'flower_female_001.gwo', '216,900', 'flower_male_001.jcs', 'flower_male_001.gwo', '252,900'),
            (3, 'Minotaur', 'cow_female_001.jcs', 'cow_female_001.gwo', '72,900', 'cow_male_001.jcs', 'cow_male_001.gwo', '108,900'),
            (4, 'Panda', 'panda_female_001.jcs', 'panda_female_001.gwo', '144,900', 'panda_male_001.jcs', 'panda_male_001.gwo', '180,900'),
            (5, 'Easter Bunny', 'rabbit_female_001.jcs', 'rabbit_female_001.gwo', '288,900', 'rabbit_male_001.jcs', 'rabbit_male_001.gwo', '324,900'),
            (6, 'Puppet', 'puppet_female_001.jcs', 'puppet_female_001.gwo', '360,900', 'puppet_male_001.jcs', 'puppet_male_001.gwo', '396,900'),
            (7, 'Wing Race', 'bird_famale_001.jcs', 'bird_famale_001.gwo', '432,900', 'bird_male_001.jcs', 'bird_male_001.gwo', '468,900'),
            (8, 'Ghost', 'corpse_famale_001.jcs', 'corpse_famale_001.gwo', '504,900', 'death_male_001.jcs', 'death_male_001.gwo', '540,900'),
            (9, 'Merman', 'fish_famale_001.jcs', 'fish_famale_001.gwo', '576,900', 'fish_male_001.jcs', 'fish_male_001.gwo', '612,900'),
            (10, 'Loyal Dog', 'dog_famale_001.jcs', 'dog_famale_001.gwo', '648,900', 'dog_male_001.jcs', 'dog_male_001.gwo', '684,900'),
            (11, 'Tiger Baby', 'tiger_female_001.jcs', 'tiger_female_001.gwo', '828,900', 'tiger_male_001.jcs', 'tiger_male_001.gwo', '792,900'),
            (12, 'Blue Crystal Dragon', 'Dragon_famale_001.jcs', 'Dragon_famale_001.gwo', '900,900', 'Dragon_male_001.jcs', 'Dragon_male_001.gwo', '864,900'),
            (13, 'Dodo', 'fatbird_female_001.jcs', 'fatbird_female_001.gwo', '720,900', 'fatbird_male_001.jcs', 'fatbird_male_001.gwo', '756,900'),
            (14, 'Elf Guardian', 'fairy_female_001.jcs', 'fairy_female_001.gwo', '936,900', 'fairy_male_001.jcs', 'fairy_male_001.gwo', '972,900'),
            (15, 'Wandering Spirit', 'stoneghost_female_001.jcs', 'stoneghost_female_001.gwo', '972,864', 'stoneghost_male_001.jcs', 'stoneghost_male_001.gwo', '972,864'),
            (16, 'Young Yeti', 'snowman_female_001.jcs', 'snowman_female_001.gwo', '936,936', 'snowman_male_001.jcs', 'snowman_male_001.gwo', '936,936'),
            (17, 'Sphinx', 'Sphinx_female_001.jcs', 'Sphinx_female_001.gwo', '972,936', 'Sphinx_male_001.jcs', 'Sphinx_male_001.gwo', '972,936'),
            (18, 'Lil QT', 'Lilqt_female_001.jcs', 'Lilqt_female_001.gwo', '864,864', 'Lilqt_male_001.jcs', 'Lilqt_male_001.gwo', '864,864'),
            (19, 'Impi', 'Impi_female_001.jcs', 'Impi_female_001.gwo', '828,864', 'Impi_male_001.jcs', 'Impi_male_001.gwo', '828,864'),
            (20, 'Hell Hound', 'Hellhound_female_001.jcs', 'Hellhound_female_001.gwo', '936,864', 'Hellhound_male_001.jcs', 'Hellhound_male_001.gwo', '936,864'),
            (21, 'Troodon', 'Troodon_female_001.jcs', 'Troodon_female_001.gwo', '900,864', 'Troodon_male_001.jcs', 'Troodon_male_001.gwo', '900,864'),
            (22, 'Poison Cactus', 'Cactus_female_001.jcs', 'Cactus_female_001.gwo', '972,684', 'Cactus_male_001.jcs', 'Cactus_male_001.gwo', '972,684'),
            (23, 'Angelic', 'Angelic_female_001.jcs', 'Angelic_female_001.gwo', '792,864', 'Angelic_male_001.jcs', 'Angelic_male_001.gwo', '792,864'),
            (24, 'Kung-Fu Kenny', 'Kenny_female_001.jcs', 'Kenny_female_001.gwo', '900,684', 'Kenny_male_001.jcs', 'Kenny_male_001.gwo', '900,684'),
            (25, 'Cretan Bull', 'Bull_female_001.jcs', 'Bull_female_001.gwo', '828,684', 'Bull_male_001.jcs', 'Bull_male_001.gwo', '828,684'),
            (26, 'Gryphon', 'Gryphon_female_001.jcs', 'Gryphon_female_001.gwo', '864,684', 'Gryphon_male_001.jcs', 'Gryphon_male_001.gwo', '864,684'),
            (27, 'Jungle Boar', 'Boar_female_001.jcs', 'Boar_female_001.gwo', '936,684', 'Boar_male_001.jcs', 'Boar_male_001.gwo', '936,684'),
            (28, 'Spirit Cat', 'Spiritcat_female_001.jcs', 'Spiritcat_female_001.gwo', '288,684', 'Spiritcat_male_001.jcs', 'Spiritcat_male_001.gwo', '252,684'),
            (29, 'Totoro', 'Totoro_female_001.jcs', 'Totoro _female_001.gwo', '144,648', 'Totoro_male_001.jcs', 'Totoro_male_001.gwo', '108,648'),
            (30, 'Fox Spirit', 'Foxspirit_female_001.jcs', 'Foxspirit_female_001.gwo', '432,684', 'Foxspirit_male_001.jcs', 'Foxspirit_male_001.gwo', '396,684'),
            (31, 'Platypus', 'Platypus_female_001.jcs', 'Platypus_female_001.gwo', '360,648', 'Platypus_male_001.jcs', 'Platypus_male_001.gwo', '324,648'),
            (32, 'Hops', 'Hops_female_001.jcs', 'Hops_female_001.gwo', '288,648', 'Hops_male_001.jcs', 'Hops_male_001.gwo', '252,648'),
            (33, 'Monkey', 'Monkey_female_001.jcs', 'Monkey_female_001.gwo', '214,684', 'Monkey_male_001.jcs', 'Monkey_male_001.gwo', '178,684'),
            (34, 'Mouse', 'Mouse_female_001.jcs', 'Mouse_female_001.gwo', '144,684', 'Mouse_male_001.jcs', 'Mouse_male_001.gwo', '108,684'),
            (35, 'Maneater Flower', 'Maneaterflower_female_001.jcs', 'Maneaterflower_female_001.gwo', '360,684', 'Maneaterflower_male_001.jcs', 'Maneaterflower_male_001.gwo', '324,684'),
            (36, 'Penguin', 'Penguin_female_001.jcs', 'Penguin_female_001.gwo', '432,648', 'Penguin_male_001.jcs', 'Penguin_male_001.gwo', '396,648'),
            (37, 'King Lion', 'Kinglion_female_001.jcs', 'Kinglion_female_001.gwo', '214,648', 'Kinglion_male_001.jcs', 'Kinglion_male_001.gwo', '178,648'),
            (38, 'Thunder Pixie', 'Thunder_female_001.jcs', 'Thunder_female_001.gwo', '0,648', 'Thunder_male_001.jcs', 'Thunder_male_001.gwo', '0,684'),
            (39, 'Bloodmoon Fox', 'Bloodmoon_female_001.jcs', 'Bloodmoon_female_001.gwo', '36,648', 'Bloodmoon_male_001.jcs', 'Bloodmoon_male_001.gwo', '36,684'),
            (40, 'Kratortle', 'Kratortle_female_001.jcs', 'Kratortle_female_001.gwo', '468,540', 'Kratortle_male_001.jcs', 'Kratortle_male_001.gwo', '468,504'),
            (41, 'Beelzeebub', 'Beelzeebub_female_001.jcs', 'Beelzeebub_female_001.gwo', '432,612', 'Beelzeebub_male_001.jcs', 'Beelzeebub_male_001.gwo', '432,576'),
            (42, 'Billy Bear', 'beer_female_001.jcs', 'beer_female_001.gwo', '432,540', 'beer_male_001.jcs', 'beer_male_001.gwo', '432,504'),
            (43, 'Roly Poly', 'panpan_female_001.jcs', 'panpan_female_001.gwo', '396,540', 'panpan_male_001.jcs', 'panpan_male_001.gwo', '396,504'),
            (44, 'Hedgehog', 'hedgehog_female_001.jcs', 'hedgehog_female_001.gwo', '396,612', 'hedgehog_male_001.jcs', 'hedgehog_male_001.gwo', '396,576'),
            (45, 'Cupid', 'xheiqbt.jcs', 'xheiqbt.gwo', '792,864', 'xbaiqiubt.jcs', 'xbaiqiubt.gwo', '792,864')
        ON CONFLICT (species_id) DO UPDATE
        SET display_name = EXCLUDED.display_name,
            female_model = EXCLUDED.female_model,
            female_texture = EXCLUDED.female_texture,
            female_icon = EXCLUDED.female_icon,
            male_model = EXCLUDED.male_model,
            male_texture = EXCLUDED.male_texture,
            male_icon = EXCLUDED.male_icon,
            samsara_appearance_thresholds =
                EXCLUDED.samsara_appearance_thresholds,
            source_path = EXCLUDED.source_path;

        CREATE TABLE IF NOT EXISTS public.character_pets (
            id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            user_id integer NOT NULL
                REFERENCES public.character_base(id) ON DELETE CASCADE,
            species_id smallint NOT NULL
                REFERENCES public.pet_templates(species_id)
                ON DELETE RESTRICT,
            name varchar(32) NOT NULL CHECK (btrim(name) <> ''),
            sex smallint NOT NULL CHECK (sex IN (0, 1)),
            level smallint NOT NULL DEFAULT 1
                CHECK (level BETWEEN 1 AND 120),
            experience bigint NOT NULL DEFAULT 0 CHECK (experience >= 0),
            aptitude smallint CHECK (aptitude BETWEEN 1 AND 14),
            rank numeric(18, 6) NOT NULL DEFAULT 0 CHECK (rank >= 0),
            completed_rebirths smallint NOT NULL DEFAULT 0
                CHECK (completed_rebirths >= 0),
            rebirths_remaining smallint NOT NULL DEFAULT 0
                CHECK (rebirths_remaining >= 0),
            completed_pet_merges integer NOT NULL DEFAULT 0
                CHECK (completed_pet_merges >= 0),
            has_soul_contract boolean NOT NULL DEFAULT false,
            has_owner_merge_talent boolean NOT NULL DEFAULT false,
            current_energy integer NOT NULL DEFAULT 0
                CHECK (current_energy >= 0),
            maximum_energy integer NOT NULL DEFAULT 100
                CHECK (maximum_energy > 0),
            amity integer NOT NULL DEFAULT 0 CHECK (amity >= 0),
            satiety integer NOT NULL DEFAULT 0 CHECK (satiety >= 0),
            remaining_lifetime integer NOT NULL DEFAULT 0
                CHECK (remaining_lifetime >= 0),
            available_stat_points integer NOT NULL DEFAULT 0
                CHECK (available_stat_points >= 0),
            growth_revealed boolean NOT NULL DEFAULT false,
            bound boolean NOT NULL DEFAULT false,
            activity_state varchar(16) NOT NULL DEFAULT 'owned'
                CHECK (
                    activity_state IN (
                        'owned',
                        'sealed',
                        'dispatched',
                        'working'
                    )
                ),
            is_summoned boolean NOT NULL DEFAULT false,
            contributes_to_character boolean NOT NULL DEFAULT false,
            revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
            created_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ck_character_pets_energy CHECK (
                current_energy <= maximum_energy
            ),
            CONSTRAINT ck_character_pets_away_inactive CHECK (
                activity_state = 'owned'
                OR (NOT is_summoned AND NOT contributes_to_character)
            ),
            CONSTRAINT ck_character_pets_merge_state CHECK (
                NOT contributes_to_character
                OR (is_summoned AND has_owner_merge_talent)
            )
        );

        CREATE INDEX IF NOT EXISTS ix_character_pets_user
            ON public.character_pets (user_id, id);

        CREATE UNIQUE INDEX IF NOT EXISTS
            ux_character_pets_one_summoned
            ON public.character_pets (user_id)
            WHERE is_summoned;

        CREATE UNIQUE INDEX IF NOT EXISTS
            ux_character_pets_one_contributing
            ON public.character_pets (user_id)
            WHERE contributes_to_character;

        CREATE TABLE IF NOT EXISTS public.character_pet_stat_values (
            pet_id bigint NOT NULL
                REFERENCES public.character_pets(id) ON DELETE CASCADE,
            stat_code smallint NOT NULL CHECK (stat_code BETWEEN 1 AND 6),
            initial_savvy numeric(18, 6) NOT NULL DEFAULT 0
                CHECK (initial_savvy >= 0),
            added_savvy numeric(18, 6) NOT NULL DEFAULT 0
                CHECK (added_savvy >= 0),
            growth_acceleration numeric(18, 6) NOT NULL DEFAULT 0
                CHECK (growth_acceleration >= 0),
            revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
            PRIMARY KEY (pet_id, stat_code)
        );

        CREATE TABLE IF NOT EXISTS
            public.character_pet_character_bonuses (
            pet_id bigint NOT NULL
                REFERENCES public.character_pets(id) ON DELETE CASCADE,
            effect_code smallint NOT NULL CHECK (
                effect_code IN (
                    0, 1, 2, 3, 4, 5, 6, 7,
                    10, 23, 24, 29, 30, 32, 34, 38
                )
            ),
            effect_value numeric(18, 6) NOT NULL
                CHECK (effect_value >= 0),
            revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
            PRIMARY KEY (pet_id, effect_code)
        );

        CREATE TABLE IF NOT EXISTS public.character_pet_skills (
            pet_id bigint NOT NULL
                REFERENCES public.character_pets(id) ON DELETE CASCADE,
            skill_id integer NOT NULL CHECK (skill_id > 0),
            slot_index smallint NOT NULL CHECK (slot_index BETWEEN 0 AND 5),
            skill_rank smallint NOT NULL DEFAULT 1 CHECK (skill_rank > 0),
            skill_experience integer NOT NULL DEFAULT 0
                CHECK (skill_experience >= 0),
            is_active boolean NOT NULL DEFAULT true,
            revision bigint NOT NULL DEFAULT 0 CHECK (revision >= 0),
            PRIMARY KEY (pet_id, skill_id),
            CONSTRAINT ux_character_pet_skills_slot
                UNIQUE (pet_id, slot_index)
        );

        CREATE TABLE IF NOT EXISTS public.pet_operation_audit (
            id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            request_id uuid NOT NULL,
            user_id integer
                REFERENCES public.character_base(id) ON DELETE SET NULL,
            user_id_snapshot integer NOT NULL CHECK (user_id_snapshot > 0),
            pet_id bigint
                REFERENCES public.character_pets(id) ON DELETE SET NULL,
            pet_id_snapshot bigint CHECK (pet_id_snapshot > 0),
            operation varchar(32) NOT NULL CHECK (
                operation IN (
                    'owner_merge',
                    'pet_merge',
                    'rebirth',
                    'soul_contract',
                    'summon',
                    'dismiss',
                    'reveal_growth',
                    'seal',
                    'unseal'
                )
            ),
            outcome varchar(16) NOT NULL
                CHECK (outcome IN ('committed', 'rejected')),
            before_state jsonb,
            after_state jsonb,
            consumed_items jsonb NOT NULL DEFAULT '[]'::jsonb
                CHECK (jsonb_typeof(consumed_items) = 'array'),
            reason_code varchar(64),
            created_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ux_pet_operation_audit_request
                UNIQUE (user_id_snapshot, request_id)
        );

        CREATE INDEX IF NOT EXISTS ix_pet_operation_audit_user_time
            ON public.pet_operation_audit (
                user_id_snapshot,
                created_at DESC
            );

        CREATE INDEX IF NOT EXISTS ix_pet_operation_audit_pet_time
            ON public.pet_operation_audit (
                pet_id_snapshot,
                created_at DESC
            )
            WHERE pet_id_snapshot IS NOT NULL;

        WITH pet_items (
            id,
            name_key,
            display_name,
            icon,
            overlap,
            use_value,
            skill,
            item_type
        ) AS (
            VALUES
                (10097, 'Pet10097', 'Fused Harpyia', '756,972', '99', NULL::text, NULL::text, '15'),
                (10098, 'Pet10098', 'Reborn Harpyia', '792,972', '99', NULL, NULL, '16'),
                (10103, 'Pet10103', 'Merged Spirit', '756,972', '99', NULL, NULL, '15'),
                (10104, 'Pet10104', 'Rebirth Spirit', '792,972', '99', NULL, NULL, '16'),
                (10105, 'Pet10105', 'Contract Spirit', '828,972', '99', NULL, NULL, '17'),
                (10106, 'Pet10106', 'Pixie Tear', '864,972', '99', NULL, NULL, NULL),
                (10107, 'Pet10107', 'Spring Water', '900,972', '99', '1', NULL, '12'),
                (10108, 'Pet10108', 'Seal Jade (Empty)', '936,972', '99', NULL, NULL, NULL),
                (11000, 'Pet11000', 'Fairy''s Feather', '288,756', '99', NULL, NULL, NULL),
                (11003, 'Pet11003', 'Charm: Pet Call', '432,756', '1', '1', '4721', '20'),
                (11004, 'Pet11004', 'Charm: Merge', '864,936', '1', '1', '4721', '21'),
                (11005, 'Pet11005', 'Phoenix''s Feather', '288,828', '99', NULL, NULL, NULL)
        )
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
        SELECT
            id,
            'consume item',
            name_key,
            display_name,
            -1,
            '{}'::smallint[],
            NULL,
            NULL,
            NULL,
            NULL,
            './Localization/en_us/UI/Texture/Icon2.gwo',
            icon,
            jsonb_strip_nulls(jsonb_build_object(
                'ID', id::text,
                'Type', 'consume item',
                'Texture', './Localization/en_us/UI/Texture/Icon2.gwo',
                'Icon', icon,
                'Random', '0',
                'Distribution', '0,0',
                'Money', '0',
                'Overlap', overlap,
                'Use', use_value,
                'Skill', skill,
                'ItemType', item_type
            ))
        FROM pet_items
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
}
