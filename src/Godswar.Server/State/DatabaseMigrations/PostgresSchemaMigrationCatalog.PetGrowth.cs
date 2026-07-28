namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetGrowthPolicy() => new(
        "20260728_016_pet_growth_policy",
        "Persist quality growth brackets and authoritative pet-egg hatching",
        """
        ALTER TABLE public.pet_aptitude_templates
            ADD COLUMN minimum_total_growth numeric(8, 2),
            ADD COLUMN maximum_total_growth numeric(8, 2),
            ADD COLUMN maximum_stat_deviation numeric(5, 4),
            ADD COLUMN growth_policy_version varchar(32);

        UPDATE public.pet_aptitude_templates aptitude
        SET minimum_total_growth = policy.minimum_total_growth,
            maximum_total_growth = policy.maximum_total_growth,
            maximum_stat_deviation = 0.1200,
            growth_policy_version = 'project-v1'
        FROM (
            VALUES
                (1::smallint, 2.00::numeric, 3.00::numeric),
                (2::smallint, 3.00::numeric, 4.00::numeric),
                (3::smallint, 4.00::numeric, 5.00::numeric),
                (4::smallint, 5.00::numeric, 7.00::numeric),
                (5::smallint, 7.00::numeric, 9.00::numeric),
                (6::smallint, 9.00::numeric, 11.00::numeric),
                (7::smallint, 11.00::numeric, 14.00::numeric),
                (8::smallint, 14.00::numeric, 17.00::numeric),
                (9::smallint, 17.00::numeric, 21.00::numeric),
                (10::smallint, 21.00::numeric, 26.00::numeric),
                (11::smallint, 26.00::numeric, 32.00::numeric),
                (12::smallint, 32.00::numeric, 39.00::numeric),
                (13::smallint, 39.00::numeric, 47.00::numeric),
                (14::smallint, 47.00::numeric, 56.00::numeric),
                (15::smallint, 56.00::numeric, 67.00::numeric),
                (16::smallint, 67.00::numeric, 80.00::numeric)
        ) AS policy(
            aptitude,
            minimum_total_growth,
            maximum_total_growth)
        WHERE aptitude.aptitude = policy.aptitude;

        ALTER TABLE public.pet_aptitude_templates
            ALTER COLUMN minimum_total_growth SET NOT NULL,
            ALTER COLUMN maximum_total_growth SET NOT NULL,
            ALTER COLUMN maximum_stat_deviation SET NOT NULL,
            ALTER COLUMN growth_policy_version SET NOT NULL;

        ALTER TABLE public.pet_aptitude_templates
            ADD CONSTRAINT ck_pet_aptitude_growth_bracket
            CHECK (
                minimum_total_growth > 0
                AND maximum_total_growth >= minimum_total_growth
            ) NOT VALID,
            ADD CONSTRAINT ck_pet_aptitude_growth_deviation
            CHECK (
                maximum_stat_deviation > 0
                AND maximum_stat_deviation <= 0.2500
            ) NOT VALID,
            ADD CONSTRAINT ck_pet_aptitude_growth_policy_version
            CHECK (btrim(growth_policy_version) <> '') NOT VALID;

        ALTER TABLE public.pet_aptitude_templates
            VALIDATE CONSTRAINT ck_pet_aptitude_growth_bracket,
            VALIDATE CONSTRAINT ck_pet_aptitude_growth_deviation,
            VALIDATE CONSTRAINT ck_pet_aptitude_growth_policy_version;

        ALTER TABLE public.character_pet_stat_values
            ADD COLUMN base_growth_rate numeric(18, 6) NOT NULL DEFAULT 0;

        ALTER TABLE public.character_pet_stat_values
            ADD CONSTRAINT ck_character_pet_stat_base_growth
            CHECK (base_growth_rate >= 0) NOT VALID;

        ALTER TABLE public.character_pet_stat_values
            VALIDATE CONSTRAINT ck_character_pet_stat_base_growth;

        WITH pet_eggs AS (
            SELECT
                item_id AS id,
                (item_id - 10149)::smallint
                    AS authoritative_species_type,
                CASE
                    WHEN item_id BETWEEN 10187 AND 10190
                        THEN (item_id - 10151)::smallint
                    ELSE (item_id - 10149)::smallint
                END AS client_declared_species_type,
                CASE item_id
                    WHEN 10150 THEN '36,972'
                    WHEN 10151 THEN '0,972'
                    WHEN 10152 THEN '0,972'
                    WHEN 10153 THEN '108,972'
                    WHEN 10154 THEN '108,972'
                    WHEN 10155 THEN '0,972'
                    WHEN 10156 THEN '36,972'
                    WHEN 10159 THEN '0,972'
                    ELSE '108,972'
                END AS icon
            FROM generate_series(10150, 10193) AS egg(item_id)
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
            egg.id,
            'consume item',
            concat('Pet', egg.id),
            template.display_name || ' Egg',
            -1,
            '{}'::smallint[],
            NULL,
            NULL,
            NULL,
            NULL,
            './Localization/en_us/UI/Texture/Icon2.gwo',
            egg.icon,
            jsonb_build_object(
                'ID', egg.id::text,
                'Type', 'consume item',
                'Texture',
                    './Localization/en_us/UI/Texture/Icon2.gwo',
                'Icon', egg.icon,
                'Random', '0',
                'Distribution', '0,0',
                'Money', '0',
                'Overlap', '1',
                'Use', '1',
                'Skill', '4740',
                'ItemType', '6',
                'Values',
                    egg.authoritative_species_type::text,
                'ClientDeclaredValues',
                    egg.client_declared_species_type::text,
                'EggMappingPolicy',
                    'display-name-species-v1',
                'Source',
                    'ItemBaseAttribute.xml+EquipName.dat+PetSpeciesCatalog'
            )
        FROM pet_eggs egg
        INNER JOIN public.pet_templates template
            ON template.species_id =
                egg.authoritative_species_type
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

        ALTER TABLE public.pet_operation_audit
            ADD CONSTRAINT ck_pet_operation_audit_operation_v3
            CHECK (
                operation IN (
                    'owner_merge',
                    'pet_merge',
                    'rebirth',
                    'soul_contract',
                    'take',
                    'summon',
                    'dismiss',
                    'reveal_growth',
                    'seal',
                    'unseal',
                    'hatch'
                )
            )
            NOT VALID;

        ALTER TABLE public.pet_operation_audit
            VALIDATE CONSTRAINT
                ck_pet_operation_audit_operation_v3;

        ALTER TABLE public.pet_operation_audit
            DROP CONSTRAINT
                pet_operation_audit_operation_check;

        ALTER TABLE public.pet_operation_audit
            RENAME CONSTRAINT
                ck_pet_operation_audit_operation_v3
            TO pet_operation_audit_operation_check;
        """);
}
