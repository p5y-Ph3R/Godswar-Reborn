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
    10193,
    'C2S',
    'GearEnhancerItemSelection',
    'items',
    'observed',
    'Native Gear Mentor item selected/removed notification.',
    'Observed 16-byte packet: bag page, page slot, then selected flag in the low byte; remaining three bytes are unstable client scratch data.'
)
ON CONFLICT (opcode, direction) DO UPDATE
SET name = EXCLUDED.name,
    category = EXCLUDED.category,
    confidence = EXCLUDED.confidence,
    description = EXCLUDED.description,
    notes = EXCLUDED.notes,
    updated_at = now();
