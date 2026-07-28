namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetLevelProgression() => new(
            "20260729_022_pet_level_progression",
            "Register native pet leveling and permit authoritative level-up audits",
            """
            ALTER TABLE public.pet_operation_audit
                ADD CONSTRAINT ck_pet_operation_audit_operation_v4
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
                        'hatch',
                        'level_up'
                    )
                )
                NOT VALID;

            ALTER TABLE public.pet_operation_audit
                VALIDATE CONSTRAINT
                    ck_pet_operation_audit_operation_v4;

            ALTER TABLE public.pet_operation_audit
                DROP CONSTRAINT
                    pet_operation_audit_operation_check;

            ALTER TABLE public.pet_operation_audit
                RENAME CONSTRAINT
                    ck_pet_operation_audit_operation_v4
                TO pet_operation_audit_operation_check;

            INSERT INTO public.packet_opcodes (
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
                    10285,
                    'C2S',
                    'PetLevelUpgradeRequest',
                    'pets',
                    'known',
                    'Requests one authoritative level advancement for an owned pet.',
                    'Exact 8-byte native frame: length, opcode, and uint32 pet ID.'
                ),
                (
                    10286,
                    'S2C',
                    'PetLevelUpgrade',
                    'pets',
                    'known',
                    'Updates the native pet model after one committed level advancement.',
                    'Exact 20-byte native frame: pet ID, byte level, three reserved bytes, uint32 remaining experience, and uint32 next-level requirement.'
                )
            ON CONFLICT (opcode, direction) DO UPDATE
            SET name = EXCLUDED.name,
                category = EXCLUDED.category,
                confidence = EXCLUDED.confidence,
                description = EXCLUDED.description,
                notes = EXCLUDED.notes,
                updated_at = now();

            UPDATE public.packet_transactions
            SET opcode_name = CASE opcode
                WHEN 10285 THEN 'PetLevelUpgradeRequest'
                WHEN 10286 THEN 'PetLevelUpgrade'
            END
            WHERE (opcode = 10285 AND direction = 'C2S')
               OR (opcode = 10286 AND direction = 'S2C');
            """);
}
