namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        ReconcileQualityDerivedPetTalents() => new(
        "20260811_072_pet_quality_innate_talents",
        "Publish and reconcile quality-derived innate pet talents",
        """
        ALTER TABLE public.pet_content_aptitude_definitions
            ADD COLUMN innate_talent_mask smallint;

        -- Historical sealed V1-V4 rows predate this field and retain NULL.
        -- Every aptitude publication that defines the field must follow the
        -- exact quality rule. The V5 reader rejects NULL active definitions.
        ALTER TABLE public.pet_content_aptitude_definitions
            ADD CONSTRAINT ck_pet_content_aptitude_innate_talents
            CHECK (
                innate_talent_mask IS NULL OR
                innate_talent_mask = CASE
                        WHEN aptitude >= 14 THEN 31
                        WHEN aptitude >= 10 THEN 26
                        ELSE 0
                    END
            );

        CREATE TABLE public.character_pet_talent_reconciliation_072 (
            pet_id bigint PRIMARY KEY,
            aptitude smallint NOT NULL,
            talent_mask_before smallint NOT NULL,
            has_owner_merge_talent_before boolean NOT NULL,
            contributes_to_character_before boolean NOT NULL,
            pet_revision_before bigint NOT NULL,
            talent_mask_after smallint NOT NULL,
            archived_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            CONSTRAINT ck_pet_talent_reconciliation_072_aptitude
                CHECK (aptitude BETWEEN 1 AND 16),
            CONSTRAINT ck_pet_talent_reconciliation_072_masks
                CHECK (
                    talent_mask_before BETWEEN 0 AND 31 AND
                    talent_mask_after IN (0, 26, 31)
                ),
            CONSTRAINT ck_pet_talent_reconciliation_072_revision
                CHECK (pet_revision_before >= 0)
        );

        WITH desired AS (
            SELECT pet.id,
                   pet.aptitude,
                   pet.talent_mask AS talent_mask_before,
                   pet.has_owner_merge_talent AS merge_before,
                   pet.contributes_to_character AS contributes_before,
                   pet.revision AS revision_before,
                   CASE
                       WHEN pet.aptitude >= 14 THEN 31
                       WHEN pet.aptitude >= 10 THEN 26
                       ELSE 0
                   END::smallint AS talent_mask_after
            FROM public.character_pets pet
        ), archived AS (
            INSERT INTO public.character_pet_talent_reconciliation_072 (
                pet_id,
                aptitude,
                talent_mask_before,
                has_owner_merge_talent_before,
                contributes_to_character_before,
                pet_revision_before,
                talent_mask_after
            )
            SELECT id,
                   aptitude,
                   talent_mask_before,
                   merge_before,
                   contributes_before,
                   revision_before,
                   talent_mask_after
            FROM desired
            WHERE talent_mask_before <> talent_mask_after
               OR merge_before <> ((talent_mask_after & 16) = 16)
            RETURNING pet_id, talent_mask_after
        )
        UPDATE public.character_pets pet
        SET talent_mask = archived.talent_mask_after,
            has_owner_merge_talent =
                ((archived.talent_mask_after & 16) = 16),
            contributes_to_character = CASE
                WHEN (archived.talent_mask_after & 16) = 16
                    THEN pet.contributes_to_character
                ELSE false
            END,
            revision = pet.revision + 1,
            updated_at = transaction_timestamp()
        FROM archived
        WHERE pet.id = archived.pet_id;

        ALTER TABLE public.character_pets
            ADD CONSTRAINT ck_character_pets_quality_innate_talents
            CHECK (
                talent_mask = CASE
                    WHEN aptitude >= 14 THEN 31
                    WHEN aptitude >= 10 THEN 26
                    ELSE 0
                END
            );

        DO $validate_pet_quality_innate_talents$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM public.character_pet_talent_reconciliation_072 archived
                JOIN public.character_pets pet ON pet.id = archived.pet_id
                WHERE pet.aptitude <> archived.aptitude
                   OR pet.talent_mask <> archived.talent_mask_after
                   OR pet.has_owner_merge_talent <>
                        ((archived.talent_mask_after & 16) = 16)
                   OR pet.revision <> archived.pet_revision_before + 1
                   OR (
                        (archived.talent_mask_after & 16) = 0 AND
                        pet.contributes_to_character
                   )
            ) THEN
                RAISE EXCEPTION
                    'Quality-derived pet-talent reconciliation failed parity validation';
            END IF;
        END
        $validate_pet_quality_innate_talents$;

        -- Stock talent-stick identities remain as inert client artifacts.
        -- Aptitude is the only authoritative talent source.
        UPDATE public.item_templates
        SET stats = stats - 'Use' - 'ItemType' - 'Values'
        WHERE id BETWEEN 10110 AND 10114;
        """);
}
