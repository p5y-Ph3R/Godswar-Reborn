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
        10040,
        'C2S',
        'SkillCast',
        'skills',
        'observed',
        'Client skill cast request. Payload includes caster object id, skill id, optional target id, and ground coordinates for area skills.',
        'Observed when clicking Champion AOE skills such as Spear Blast, Meteor Blast, and Sacred Zeal.'
    ),
    (
        10040,
        'S2C',
        'SkillCastVisual',
        'skills',
        'observed',
        'Server skill cast visual/broadcast packet. Same 40-byte shape as the client cast request.',
        'Captured from the working server and now echoed by the local server for caster animation/area skill effects.'
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
WHERE opcode = 10040;
