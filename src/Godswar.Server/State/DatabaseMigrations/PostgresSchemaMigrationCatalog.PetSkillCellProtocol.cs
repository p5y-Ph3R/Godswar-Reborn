namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreatePetSkillCellProtocol() => new(
            "20260810_066_pet_skill_cell_protocol",
            "Register the verified live pet skill-cell response",
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
            VALUES (
                10245,
                'S2C',
                'PetSkillState',
                'pets',
                'known',
                'Refreshes one pet skill list and its available/opened cell boundaries.',
                '36-byte frame: uint32 pet ID, available/opened/learned uint8 fields, one reserved byte, then twelve uint16 skill IDs. Native handler 0x0069CC90.'
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
