namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateOwnedPetBootstrapOpcode() => new(
            "20260728_013_owned_pet_bootstrap_opcode",
            "Correct opcode 10237 metadata to the verified owned-pet bootstrap",
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
                10237,
                'S2C',
                'OwnedPetList',
                'pets',
                'known',
                'Complete owned-pet list sent during authenticated character bootstrap.',
                'Header is 8 bytes followed by count fixed 0xA8-byte pet records. Native handler 0x0069C950 and record copy routine 0x006A6340.'
            )
            ON CONFLICT (opcode, direction) DO UPDATE
            SET name = EXCLUDED.name,
                category = EXCLUDED.category,
                confidence = EXCLUDED.confidence,
                description = EXCLUDED.description,
                notes = EXCLUDED.notes,
                updated_at = now();

            UPDATE packet_transactions
            SET opcode_name = 'OwnedPetList'
            WHERE opcode = 10237
              AND direction = 'S2C';
            """);
}
