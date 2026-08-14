namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CorrectPetSkillCellProtocol() => new(
            "20260810_067_pet_skill_cell_protocol_correction",
            "Separate verified pet-care and pet skill-cell response opcodes",
            """
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
                    10245,
                    'S2C',
                    'PetCareState',
                    'pets',
                    'known',
                    'Refreshes one pet satiety, amity, and current lifetime.',
                    '16-byte frame: uint32 pet ID, uint8 satiety at +9, uint8 amity at +11, and uint16 current lifetime at +14. Native handler 0x0069CC90.'
                ),
                (
                    10247,
                    'S2C',
                    'PetSkillState',
                    'pets',
                    'known',
                    'Refreshes one pet skill list and its available/opened cell boundaries.',
                    '36-byte frame: uint32 pet ID, available/opened/learned uint8 fields at +8/+9/+10, one reserved byte, then twelve uint16 skill IDs at +12. Native handler 0x0069D310.'
                )
            ON CONFLICT (opcode, direction) DO UPDATE
            SET name = EXCLUDED.name,
                category = EXCLUDED.category,
                confidence = EXCLUDED.confidence,
                description = EXCLUDED.description,
                notes = EXCLUDED.notes,
                updated_at = now();

            UPDATE packet_transactions
            SET opcode_name = CASE opcode
                WHEN 10245 THEN 'PetCareState'
                WHEN 10247 THEN 'PetSkillState'
            END
            WHERE direction = 'S2C'
              AND opcode IN (10245, 10247);
            """);
}
