namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetSkillsAndTalentsFoundation() => new(
        "20260810_061_pet_skills_and_talents",
        "Persist twelve pet skill cells and the five stock pet talents",
        """
        ALTER TABLE public.character_pets
            ADD COLUMN talent_mask smallint NOT NULL DEFAULT 0
                CONSTRAINT ck_character_pets_talent_mask
                CHECK (talent_mask BETWEEN 0 AND 31),
            ADD COLUMN opened_skill_slots smallint NOT NULL DEFAULT 1
                CONSTRAINT ck_character_pets_opened_skill_slots
                CHECK (opened_skill_slots BETWEEN 1 AND 12),
            ADD COLUMN available_skill_slots smallint NOT NULL DEFAULT 1
                CONSTRAINT ck_character_pets_available_skill_slots
                CHECK (available_skill_slots BETWEEN 1 AND 12);

        WITH published_native_talents AS (
            SELECT profile.species_id,
                   profile.aptitude,
                   profile.native_genius::smallint AS talent_mask
            FROM public.pet_content_publication publication
            JOIN public.pet_content_native_profiles profile
              ON profile.revision = publication.revision
            WHERE publication.family = 'pets'
        )
        UPDATE public.character_pets pet
        SET talent_mask = (
                COALESCE(native.talent_mask, 0)::integer |
                CASE WHEN pet.has_owner_merge_talent THEN 16 ELSE 0 END
            )::smallint
        FROM published_native_talents native
        WHERE native.species_id = pet.species_id
          AND native.aptitude = pet.aptitude;

        UPDATE public.character_pets
        SET talent_mask = talent_mask | 16
        WHERE has_owner_merge_talent
          AND (talent_mask & 16) = 0;

        UPDATE public.character_pets
        SET has_owner_merge_talent = (talent_mask & 16) = 16;

        WITH learned_boundaries AS (
            SELECT pet.id,
                   GREATEST(
                       1,
                       COALESCE(MAX(skill.slot_index) + 1, 1),
                       CASE WHEN pet.aptitude >= 10 THEN 2 ELSE 1 END
                   )::smallint AS slot_boundary
            FROM public.character_pets pet
            LEFT JOIN public.character_pet_skills skill
              ON skill.pet_id = pet.id
             AND skill.is_active
            GROUP BY pet.id, pet.aptitude
        )
        UPDATE public.character_pets pet
        SET opened_skill_slots = LEAST(12, boundary.slot_boundary),
            available_skill_slots = LEAST(12, boundary.slot_boundary)
        FROM learned_boundaries boundary
        WHERE boundary.id = pet.id;

        ALTER TABLE public.character_pet_skills
            DROP CONSTRAINT IF EXISTS
                character_pet_skills_slot_index_check;
        ALTER TABLE public.character_pet_skills
            ADD CONSTRAINT ck_character_pet_skills_slot_index_v2
            CHECK (slot_index BETWEEN 0 AND 11);

        ALTER TABLE public.character_pets
            ADD CONSTRAINT ck_character_pets_skill_slot_boundaries
            CHECK (opened_skill_slots <= available_skill_slots),
            ADD CONSTRAINT ck_character_pets_merge_talent_projection
            CHECK (
                has_owner_merge_talent = ((talent_mask & 16) = 16)
            );

        -- The old names are a permutation of the new names and display_name
        -- is unique. Move all five rows through collision-free temporary
        -- values before assigning the reviewed ordering.
        UPDATE public.pet_aptitude_templates
        SET display_name = '__pet_aptitude_061_' || aptitude::text || '__'
        WHERE aptitude BETWEEN 6 AND 10;

        UPDATE public.pet_aptitude_templates
        SET display_name = CASE aptitude
            WHEN 6 THEN 'Calm'
            WHEN 7 THEN 'Grumpy'
            WHEN 8 THEN 'Brave'
            WHEN 9 THEN 'Zealous'
            WHEN 10 THEN 'Smart'
            ELSE display_name
        END
        WHERE aptitude BETWEEN 6 AND 10;

        WITH pet_items(
            id, name_key, display_name, icon, overlap,
            use_value, item_type, values_value
        ) AS (
            VALUES
                (10099, 'Pet10099', 'Pet Enhance Spring', '648,936', '99', '1', '5', NULL::text),
                (10100, 'Pet10100', 'Golden Apple Juice', '504,936', '99', '1', '1', NULL),
                (10101, 'Pet10101', 'Strong Purge Potion', '612,936', '99', NULL, NULL, NULL),
                (10102, 'Pet10102', 'Weak Purge Potion', '468,936', '99', '1', '2', NULL),
                (10110, 'Pet10110', 'Stick: Random Event', '720,936', '1', '1', '9', '1'),
                (10111, 'Pet10111', 'Stick: Quest Dispatch', '720,936', '1', '1', '9', '2'),
                (10112, 'Pet10112', 'Stick: Work', '720,936', '1', '1', '9', '4'),
                (10113, 'Pet10113', 'Stick: Healing', '720,936', '1', '1', '9', '8'),
                (10114, 'Pet10114', 'Stick: Merge', '720,936', '1', '1', '9', '16')
        )
        INSERT INTO public.item_templates (
            id, kind, name_key, display_name, equipment_slot, class_ids,
            min_level, max_level, hand, skill_flag, texture, icon, stats
        )
        SELECT item.id,
               'consume item',
               item.name_key,
               item.display_name,
               0,
               '{}'::smallint[],
               NULL,
               NULL,
               NULL,
               NULL,
               './Localization/en_us/UI/Texture/Icon2.gwo',
               item.icon,
               jsonb_strip_nulls(jsonb_build_object(
                   'ID', item.id::text,
                   'Type', 'consume item',
                   'Texture',
                       './Localization/en_us/UI/Texture/Icon2.gwo',
                   'Icon', item.icon,
                   'Random', '0',
                   'Distribution', '0,0',
                   'Money', '0',
                   'Overlap', item.overlap,
                   'Use', item.use_value,
                   'ItemType', item.item_type,
                   'Values', item.values_value
               ))
        FROM pet_items item
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
