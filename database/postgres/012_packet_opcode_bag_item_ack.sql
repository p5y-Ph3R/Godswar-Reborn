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
    10056,
    'S2C',
    'BagItemActionAck',
    'items',
    'observed',
    'Server acknowledgement for client bag item action follow-up.',
    'Observed after equipment unequip follow-up from the working service.'
)
ON CONFLICT (opcode, direction) DO UPDATE
SET name = EXCLUDED.name,
    category = EXCLUDED.category,
    confidence = EXCLUDED.confidence,
    description = EXCLUDED.description,
    notes = EXCLUDED.notes,
    updated_at = now();

UPDATE packet_transactions
SET opcode_name = 'BagItemActionAck'
WHERE opcode = 10056
  AND direction = 'S2C';
