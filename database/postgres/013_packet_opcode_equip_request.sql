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
    10051,
    'C2S',
    'EquipmentItemEquipRequest',
    'items',
    'observed',
    'Client request to equip an item from the kitbag.',
    'Observed as opcode 0x2743 length 92 during equip-back on the working service. Older label BreakItem was misleading for this flow.'
)
ON CONFLICT (opcode, direction) DO UPDATE
SET name = EXCLUDED.name,
    category = EXCLUDED.category,
    confidence = EXCLUDED.confidence,
    description = EXCLUDED.description,
    notes = EXCLUDED.notes,
    updated_at = now();

UPDATE packet_transactions
SET opcode_name = 'EquipmentItemEquipRequest'
WHERE opcode = 10051
  AND direction = 'C2S';
