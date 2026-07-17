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
        10051,
        'C2S',
        'EquipmentItemEquipRequest',
        'items',
        'observed',
        'Client request to equip an item from the kitbag.',
        'Observed as opcode 0x2743 length 92 during equip-back on the working service. Older label BreakItem is misleading for this flow.'
    ),
    (
        10067,
        'C2S',
        'NpcDialogOpenRequest',
        'npc',
        'observed',
        'Client opens/interacts with an NPC dialog.',
        'Holy Stone Artisan capture: payload starts with npc id 5083. Server responds with the NPC key such as Sparta_086.'
    ),
    (
        10067,
        'S2C',
        'NpcDialogOpenAck',
        'npc',
        'observed',
        'Server acknowledges NPC dialog open and sends the NPC key.',
        'Observed response contains npc id 5083, type/index fields 512 and 30, then fixed ASCII npc key Sparta_086.'
    ),
    (
        10068,
        'C2S',
        'NpcDialogPageRequest',
        'npc',
        'observed',
        'Client requests/continues the selected NPC dialog page.',
        'Holy Stone Artisan capture: 8-byte packet containing npc id 5083.'
    ),
    (
        10069,
        'C2S',
        'NpcFunctionActionRequest',
        'npc',
        'observed',
        'Client invokes an NPC function/action with sub-id and item slots.',
        'Holy Stone Artisan capture: sub-id at packet offset 16. Examples: 101 mount stone, 201 remove stone, 301 equipment drilling.'
    ),
    (
        10070,
        'S2C',
        'NpcFunctionActionResponse',
        'npc',
        'observed',
        'Server returns NPC function menu entries or result sub-ids.',
        'Holy Stone Artisan capture: initial menu returns 101,201,301,401,501,601,701. Results include 800 mount success, 1200 remove success, 1400 insufficient funds, 1500 drill success.'
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
), 'Unknown');
