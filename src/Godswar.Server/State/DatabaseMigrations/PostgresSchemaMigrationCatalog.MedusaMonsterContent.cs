namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateMedusaMonsterContent() => new(
        "20260828_118_medusa_monster_content",
        "Publish editable Medusa monsters, loot, pickup claims, and pet EXP",
        """
        INSERT INTO public.item_templates (
            id, kind, name_key, display_name, equipment_slot, class_ids,
            min_level, max_level, hand, skill_flag, texture, icon, stats)
        VALUES
            (9916, 'consume item', 'Rmaterial16', 'Punishment Dust', 0,
                ARRAY[]::smallint[], NULL, NULL, NULL, NULL,
                './Localization/en_us/UI/Texture/Icon2.gwo', '720,504',
                '{"ID":"9916","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"720,504","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'::jsonb),
            (9940, 'consume item', 'Material10', 'Accuracy Stone', 0,
                ARRAY[]::smallint[], NULL, NULL, NULL, NULL,
                './Localization/en_us/UI/Texture/Icon2.gwo', '504,540',
                '{"ID":"9940","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"504,540","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'::jsonb),
            (9941, 'consume item', 'Material11', 'Psychic Stone', 0,
                ARRAY[]::smallint[], NULL, NULL, NULL, NULL,
                './Localization/en_us/UI/Texture/Icon2.gwo', '540,540',
                '{"ID":"9941","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"540,540","Random":"0","Distribution":"50,150","Money":"0","Overlap":"99"}'::jsonb),
            (10001, 'consume item', 'Pet10001', 'Anemone', 0,
                ARRAY[]::smallint[], NULL, NULL, NULL, NULL,
                './Localization/en_us/UI/Texture/Icon2.gwo', '180,936',
                '{"ID":"10001","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"180,936","Random":"15","Distribution":"50,200","Money":"0","Overlap":"99","Use":"1","Skill":"4721","ItemType":"7","Food":"1","Fill":"20","Favor":"6"}'::jsonb),
            (12010, 'consume item', 'Lifing12010', 'Copper Ore', 0,
                ARRAY[]::smallint[], NULL, NULL, NULL, NULL,
                './Localization/en_us/UI/Texture/Icon2.gwo', '540,648',
                '{"ID":"12010","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"540,648","Random":"2400","Distribution":"50,200","Money":"205","Overlap":"99"}'::jsonb),
            (12030, 'consume item', 'Lifing12030', 'Herb', 0,
                ARRAY[]::smallint[], NULL, NULL, NULL, NULL,
                './Localization/en_us/UI/Texture/Icon2.gwo', '612,648',
                '{"ID":"12030","Type":"consume item","Texture":"./Localization/en_us/UI/Texture/Icon2.gwo","Icon":"612,648","Random":"1400","Distribution":"50,200","Money":"165","Overlap":"99"}'::jsonb)
        ON CONFLICT (id) DO NOTHING;

        CREATE TABLE IF NOT EXISTS public.medusa_monster_rules (
            difficulty smallint NOT NULL CHECK (difficulty BETWEEN 1 AND 3),
            template_alias varchar(64) COLLATE "C" NOT NULL,
            monster_level smallint NOT NULL CHECK (monster_level BETWEEN 1 AND 200),
            maximum_health integer NOT NULL CHECK (maximum_health > 0),
            score integer NOT NULL CHECK (score BETWEEN 0 AND 10000),
            movement_speed_basis_points integer NOT NULL
                CHECK (movement_speed_basis_points BETWEEN 1 AND 10000),
            corpse_without_loot_ms integer NULL
                CHECK (corpse_without_loot_ms BETWEEN 1000 AND 300000),
            corpse_with_loot_ms integer NULL
                CHECK (corpse_with_loot_ms BETWEEN 1000 AND 300000),
            pet_experience integer NOT NULL
                CHECK (pet_experience BETWEEN 0 AND 100000000),
            enabled boolean NOT NULL DEFAULT true,
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (difficulty, template_alias),
            CHECK (corpse_with_loot_ms IS NULL OR
                   corpse_without_loot_ms IS NULL OR
                   corpse_with_loot_ms >= corpse_without_loot_ms)
        );

        WITH aliases(alias, role, level, score, speed, pet_exp) AS (
            VALUES
                ('boss-stheno','stheno',200,1000,5000,1116),
                ('boss-euryale','euryale',130,50,7368,712),
                ('boss-chrysaor','chrysaor',100,50,10000,548),
                ('boss-medusa','medusa',200,1100,5000,1116),
                ('elite-gorgon-archer','elite',95,50,10000,524),
                ('elite-crazy-axeman-a','elite',95,50,10000,524),
                ('elite-gorgon-shaman-006','elite',95,50,10000,524),
                ('elite-gorgon-shaman-008','elite',95,50,10000,524),
                ('elite-mud-crocodile','elite',95,50,10000,524),
                ('elite-gorgon-demon','elite',100,50,10000,548),
                ('elite-jungle-wizard-c5','elite',100,50,10000,548),
                ('elite-jungle-wizard-c6','elite',100,50,10000,548),
                ('elite-dark-gorgon-shaman','elite',95,50,10000,524),
                ('elite-dark-gorgon-priest','elite',100,50,10000,548),
                ('elite-gorgon-astrologer','elite',95,50,10000,524),
                ('elite-gorgon-guardian-a','elite',95,50,10000,524),
                ('elite-gorgon-axeman','elite',95,50,10000,524),
                ('elite-gorgon-hammer-soldier','elite',100,50,10000,548),
                ('elite-crazy-axeman-c','elite',95,50,10000,524),
                ('elite-jungle-wizard-b','elite',100,50,10000,548),
                ('elite-gorgon-guardian-b','elite',95,50,10000,524),
                ('elite-gorgon-wizard','elite',95,50,10000,524),
                ('elite-cyclops-swordsman','elite',95,50,10000,524),
                ('elite-priest-a-012','elite',95,50,10000,524),
                ('elite-priest-b-012','elite',100,50,10000,548),
                ('elite-shaman-c-009','elite',100,50,10000,548),
                ('elite-shaman-c-008','elite',95,50,10000,524),
                ('elite-gorgon-priest-c-014','elite',100,50,10000,548),
                ('elite-astrologer-b-009','elite',95,50,10000,524),
                ('elite-astrologer-a-006','elite',95,50,10000,524),
                ('normal-gorgon-pikeman-b','ordinary',95,1,10000,524),
                ('normal-gorgon-pikeman-a','ordinary',95,1,10000,524),
                ('normal-gorgon-shaman','ordinary',95,1,10000,524),
                ('normal-mud-crocodile','ordinary',95,1,10000,524),
                ('normal-jungle-deer','ordinary',95,1,10000,524),
                ('normal-gorgon-jungle-wizard','ordinary',95,1,10000,524),
                ('normal-giant-gorgon-axeman','ordinary',95,1,10000,524),
                ('normal-gorgon-astrologer','ordinary',95,1,10000,524),
                ('normal-gorgon-axeman-a','ordinary',95,1,10000,524),
                ('normal-gorgon-axeman-b','ordinary',95,1,10000,524)
        ), difficulties(difficulty, health_multiplier) AS (
            VALUES (1,1), (2,2), (3,5)
        )
        INSERT INTO public.medusa_monster_rules (
            difficulty, template_alias, monster_level, maximum_health,
            score, movement_speed_basis_points, corpse_without_loot_ms,
            corpse_with_loot_ms, pet_experience)
        SELECT
            difficulty, alias, level,
            (CASE role
                WHEN 'ordinary' THEN 75000
                WHEN 'elite' THEN 300000
                WHEN 'euryale' THEN 750000
                WHEN 'chrysaor' THEN 875000
                WHEN 'stheno' THEN 3000000
                WHEN 'medusa' THEN 3500000
             END * health_multiplier)::integer,
            score, speed,
            CASE WHEN role IN ('stheno','medusa') THEN NULL ELSE 4200 END,
            CASE WHEN role IN ('stheno','medusa') THEN NULL ELSE 20000 END,
            pet_exp
        FROM aliases CROSS JOIN difficulties
        ON CONFLICT (difficulty, template_alias) DO NOTHING;

        CREATE TABLE IF NOT EXISTS public.medusa_monster_loot_rules (
            difficulty smallint NOT NULL,
            template_alias varchar(64) COLLATE "C" NOT NULL,
            loot_index smallint NOT NULL CHECK (loot_index BETWEEN 0 AND 31),
            item_id integer NOT NULL REFERENCES public.item_templates(id),
            chance_basis_points integer NOT NULL
                CHECK (chance_basis_points BETWEEN 1 AND 10000),
            minimum_quantity smallint NOT NULL
                CHECK (minimum_quantity BETWEEN 1 AND 255),
            maximum_quantity smallint NOT NULL
                CHECK (maximum_quantity BETWEEN minimum_quantity AND 255),
            enabled boolean NOT NULL DEFAULT true,
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (difficulty, template_alias, loot_index),
            FOREIGN KEY (difficulty, template_alias)
                REFERENCES public.medusa_monster_rules(
                    difficulty, template_alias) ON DELETE CASCADE
        );

        WITH loot(alias, loot_index, item_id, chance, minimum, maximum) AS (
            VALUES
                ('normal-mud-crocodile',0,10001,500,1,1),
                ('normal-jungle-deer',0,12030,500,1,1),
                ('normal-gorgon-pikeman-a',0,12010,500,1,1),
                ('normal-gorgon-pikeman-b',0,12010,500,1,1),
                ('boss-stheno',0,9941,10000,1,1),
                ('boss-stheno',1,9941,10000,1,1),
                ('boss-medusa',0,9941,10000,1,1),
                ('boss-medusa',1,9940,10000,1,1),
                ('boss-medusa',2,9916,10000,6,6)
        )
        INSERT INTO public.medusa_monster_loot_rules (
            difficulty, template_alias, loot_index, item_id,
            chance_basis_points, minimum_quantity, maximum_quantity)
        SELECT difficulty, alias, loot_index, item_id, chance, minimum, maximum
        FROM loot CROSS JOIN (VALUES (1), (2), (3)) AS d(difficulty)
        ON CONFLICT (difficulty, template_alias, loot_index) DO NOTHING;

        CREATE TABLE IF NOT EXISTS public.monster_loot_pickup_claims (
            death_event_id uuid NOT NULL,
            loot_index smallint NOT NULL,
            account_id integer NOT NULL REFERENCES public.accounts(id),
            character_id integer NOT NULL REFERENCES public.character_base(id),
            item_id integer NOT NULL REFERENCES public.item_templates(id),
            quantity smallint NOT NULL CHECK (quantity BETWEEN 1 AND 255),
            inventory_revision bigint NOT NULL CHECK (inventory_revision > 0),
            created_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (death_event_id, loot_index)
        );

        CREATE TABLE IF NOT EXISTS public.monster_death_pet_experience (
            death_event_id uuid PRIMARY KEY,
            account_id integer NOT NULL REFERENCES public.accounts(id),
            character_id integer NOT NULL REFERENCES public.character_base(id),
            requested_experience integer NOT NULL
                CHECK (requested_experience BETWEEN 0 AND 100000000),
            pet_id bigint NULL REFERENCES public.character_pets(id),
            experience_before bigint NULL,
            experience_after bigint NULL,
            pet_revision bigint NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            CHECK ((pet_id IS NULL AND experience_before IS NULL AND
                    experience_after IS NULL AND pet_revision IS NULL) OR
                   (pet_id IS NOT NULL AND experience_before IS NOT NULL AND
                    experience_after IS NOT NULL AND pet_revision IS NOT NULL))
        );
        """);
}
