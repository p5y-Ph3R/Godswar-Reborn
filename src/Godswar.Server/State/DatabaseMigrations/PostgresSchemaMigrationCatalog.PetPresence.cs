namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetPresenceProtocol() => new(
            "20260728_014_pet_presence_protocol",
            "Persist carried pets and register native carry, summon, and recall opcodes",
            """
            ALTER TABLE public.character_pets
                ADD COLUMN IF NOT EXISTS is_carried boolean
                    NOT NULL DEFAULT false;

            UPDATE public.character_pets
            SET is_carried = true
            WHERE is_summoned
              AND NOT is_carried;

            ALTER TABLE public.character_pets
                ADD CONSTRAINT ck_character_pets_carried_available
                    CHECK (NOT is_carried OR activity_state = 'owned')
                    NOT VALID;

            ALTER TABLE public.character_pets
                ADD CONSTRAINT ck_character_pets_summoned_carried
                    CHECK (NOT is_summoned OR is_carried)
                    NOT VALID;

            ALTER TABLE public.character_pets
                VALIDATE CONSTRAINT
                    ck_character_pets_carried_available;

            ALTER TABLE public.character_pets
                VALIDATE CONSTRAINT
                    ck_character_pets_summoned_carried;

            CREATE UNIQUE INDEX IF NOT EXISTS
                ux_character_pets_one_carried
                ON public.character_pets (user_id)
                WHERE is_carried;

            INSERT INTO packet_opcodes (
                opcode,
                direction,
                name,
                category,
                confidence,
                description,
                notes
            )
            VALUES
                (
                    10239,
                    'C2S',
                    'PetTakeRequest',
                    'pets',
                    'known',
                    'Selects an owned pet as the character carried pet.',
                    'Eight-byte frame: header followed by the uint32 pet ID.'
                ),
                (
                    10240,
                    'C2S',
                    'PetCallOutRequest',
                    'pets',
                    'known',
                    'Summons the currently carried pet.',
                    'Eight-byte frame: header followed by the uint32 pet ID.'
                ),
                (
                    10241,
                    'C2S',
                    'PetRecallRequest',
                    'pets',
                    'known',
                    'Recalls the currently summoned pet.',
                    'Eight-byte frame: header followed by the uint32 pet ID.'
                ),
                (
                    10244,
                    'S2C',
                    'PetOperationResult',
                    'pets',
                    'known',
                    'Reports the authoritative result of a pet operation.',
                    'Nine-byte frame: uint32 pet ID and uint8 native result code.'
                )
            ON CONFLICT (opcode, direction) DO UPDATE
            SET name = EXCLUDED.name,
                category = EXCLUDED.category,
                confidence = EXCLUDED.confidence,
                description = EXCLUDED.description,
                notes = EXCLUDED.notes,
                updated_at = now();
            """);
}
