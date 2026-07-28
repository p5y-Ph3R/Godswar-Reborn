namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateSkillCastInterruptOpcode() => new(
            "20260728_009_skill_cast_interrupt_opcode",
            "Reconcile the bidirectional native skill-cast interruption opcode",
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
                    10171,
                    'C2S',
                    'SkillCastInterrupt',
                    'skills',
                    'observed',
                    'Client cast-interruption report. The payload is the local player object ID.',
                    'Bidirectional 8-byte frame observed as 0800BB2748140000 for local object ID 0x1448.'
                ),
                (
                    10171,
                    'S2C',
                    'SkillCastInterrupt',
                    'skills',
                    'observed',
                    'Authoritative cast-interruption notification. The payload is the caster object ID in the receiver namespace.',
                    'Self receives local ID 0x1448; observers receive the caster world ID. The client displays Skill09 (Skill is disturbed).'
                )
            ON CONFLICT (opcode, direction) DO UPDATE
            SET name = EXCLUDED.name,
                category = EXCLUDED.category,
                confidence = EXCLUDED.confidence,
                description = EXCLUDED.description,
                notes = EXCLUDED.notes,
                updated_at = now();

            UPDATE packet_transactions
            SET opcode_name = 'SkillCastInterrupt'
            WHERE opcode = 10171;
            """);
}
