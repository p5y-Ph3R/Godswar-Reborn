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
        'SkillCastFinishRequest',
        'skills',
        'observed',
        'Client cast-finish request sent after SkillCastVisual while the client is waiting for cast completion.',
        'Observed locally as repeated 8-byte packets 0800BB2748140000 when SkillCastImpact was missing.'
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
