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
        10046,
        'S2C',
        'SkillCastImpact',
        'skills',
        'observed',
        'Server skill impact/completion packet. Payload contains caster object id twice, skill id, and impact coordinates.',
        'Captured from working server immediately after SkillCastVisual; required to release the client from casting state.'
    ),
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
SET opcode_name = COALESCE((
    SELECT packet_opcodes.name
    FROM packet_opcodes
    WHERE packet_opcodes.opcode = packet_transactions.opcode
      AND packet_opcodes.direction IN (packet_transactions.direction, 'ANY')
    ORDER BY CASE WHEN packet_opcodes.direction = packet_transactions.direction THEN 0 ELSE 1 END
    LIMIT 1
), 'Unknown')
WHERE opcode IN (10046, 10171);
