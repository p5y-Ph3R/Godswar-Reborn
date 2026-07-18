CREATE TABLE IF NOT EXISTS packet_opcodes (
    opcode integer NOT NULL,
    direction varchar(4) NOT NULL DEFAULT 'ANY',
    name varchar(128) NOT NULL,
    category varchar(64) NOT NULL DEFAULT '',
    confidence varchar(16) NOT NULL DEFAULT 'known',
    description text NOT NULL DEFAULT '',
    notes text NOT NULL DEFAULT '',
    first_seen_at timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (opcode, direction),
    CONSTRAINT packet_opcodes_direction_check CHECK (direction IN ('ANY', 'C2S', 'S2C'))
);

ALTER TABLE packet_transactions
    ADD COLUMN IF NOT EXISTS opcode_name varchar(128) NOT NULL DEFAULT 'Unknown';

CREATE INDEX IF NOT EXISTS ix_packet_transactions_opcode_name
    ON packet_transactions (opcode_name);

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
    (1, 'C2S', 'Login', 'login', 'known', 'Client login request.', 'From current emulator opcode map.'),
    (4, 'C2S', 'SelectServer', 'login', 'known', 'Client selected login server/world.', 'From current emulator opcode map.'),
    (6, 'S2C', 'LoginReturnInfo', 'login', 'known', 'Server login response.', 'From current emulator opcode map.'),
    (10000, 'C2S', 'LoginGameServer', 'session', 'known', 'Client game-server login request.', 'From current emulator opcode map.'),
    (10001, 'S2C', 'ResponseGameServer', 'session', 'known', 'Server game redirect/session response.', 'From current emulator opcode map.'),
    (10002, 'C2S', 'RoleInfo', 'character', 'known', 'Client character-list request.', 'From current emulator opcode map.'),
    (10003, 'C2S', 'CreateRole', 'character', 'known', 'Client character creation request.', 'From current emulator opcode map.'),
    (10004, 'C2S', 'DeleteRole', 'character', 'known', 'Client character delete request.', 'From current emulator opcode map.'),
    (10005, 'C2S', 'GameServerReady', 'session', 'known', 'Client game-server ready signal.', 'From current emulator opcode map.'),
    (10006, 'C2S', 'EnterGame', 'character', 'known', 'Client enter-world request.', 'From current emulator opcode map.'),
    (10007, 'C2S', 'ClientReady', 'session', 'known', 'Client ready signal after enter.', 'From current emulator opcode map.'),
    (10008, 'C2S', 'GameServerInfo', 'session', 'known', 'Client game-server info request.', 'From current emulator opcode map.'),
    (10013, 'C2S', 'WalkBegin', 'movement', 'known', 'Client movement begin.', 'From current emulator opcode map.'),
    (10014, 'C2S', 'WalkEnd', 'movement', 'known', 'Client movement end.', 'From current emulator opcode map.'),
    (10015, 'ANY', 'Ping', 'session', 'observed', 'Heartbeat/keepalive packet.', 'Observed in both directions.'),
    (10019, 'S2C', 'EnterMain', 'character', 'observed', 'Main enter-world character payload; includes equipment records and talent point value.', 'Captured from working server on enter; talent points observed at payload offset 88 / packet offset 92.'),
    (10022, 'C2S', 'Kitbag', 'items', 'known', 'Client kitbag request.', 'From current emulator opcode map.'),
    (10023, 'C2S', 'Storage', 'items', 'known', 'Client storage request.', 'From current emulator opcode map.'),
    (10027, 'S2C', 'MonsterDeathReward', 'progression', 'observed', 'Monster death and recipient progression refresh.', 'Captured 116-byte packet: dead monster id, five recipient slots, money, current fighter EXP, current talent EXP, current talent points, bookkeeping monster id, and prestige.'),
    (10030, 'S2C', 'PlayerLevelUp', 'progression', 'observed', 'Player level-up state refresh.', 'Sent once for each carried level after a monster-kill reward.'),
    (10031, 'S2C', 'ExperienceGain', 'progression', 'observed', 'Fighter EXP gain notice.', 'The current client reads the gained EXP delta at packet offset 4; resulting fighter EXP is retained at offset 8.'),
    (10033, 'S2C', 'KitBagDetailPage', 'items', 'observed', 'Server kitbag detail page payload.', 'Captured from working server on enter.'),
    (10041, 'S2C', 'TalentSkillUnlockList', 'talents', 'observed', 'Server skill/talent UI unlock list.', 'Captured after TalentRankList in working-server enter/detail flow; 12-byte header plus 8-byte skill records.'),
    (10042, 'S2C', 'TalentRankList', 'talents', 'observed', 'Server talent rank/state list.', 'Captured 12-byte header plus 16-byte records: talent id, current rank, display/current value, next upgrade cost.'),
    (10035, 'C2S', 'Talk', 'chat', 'known', 'Client chat/talk packet.', 'From current emulator opcode map.'),
    (10048, 'C2S', 'PickupDrops', 'items', 'known', 'Client pickup-drops request.', 'From current emulator opcode map.'),
    (10049, 'C2S', 'UseOrEquip', 'items', 'observed', 'Client use/equip request; 28-byte shape is also used for talent upgrades.', 'Captured talent upgrade request: object id, talent id, current rank, unknown, current talent points, unknown.'),
    (10049, 'S2C', 'TalentUpgradeAck', 'talents', 'observed', 'Server talent-upgrade acknowledgement.', 'Captured response fields: object id, talent id, new rank, point cost, remaining talent points, display/current value.'),
    (10050, 'C2S', 'MoveItem', 'items', 'known', 'Client move-item request.', 'From current emulator opcode map.'),
    (10051, 'C2S', 'BreakItem', 'items', 'known', 'Client break-item request.', 'From current emulator opcode map.'),
    (10051, 'S2C', 'EquipmentItemSnapshot', 'items', 'observed', 'Server equipment item snapshot/clear packet.', 'Same numeric opcode as C2S BreakItem; direction is required.'),
    (10052, 'C2S', 'StorageItemRequest', 'items', 'observed', 'Client item/equipment action request; observed as unequip.', 'Captured from working server during unequip.'),
    (10052, 'S2C', 'StorageItemAck', 'items', 'observed', 'Server item/equipment action acknowledgement.', 'Captured from working server during unequip.'),
    (10053, 'C2S', 'Sell', 'items', 'known', 'Client sell request.', 'From current emulator opcode map.'),
    (10056, 'C2S', 'BagItemAction', 'items', 'known', 'Client bag item action.', 'From current emulator opcode map.'),
    (10090, 'S2C', 'SynGameData', 'session', 'observed', 'Working-server game-data sync block sent during enter before EnterComplete.', 'Captured as 2048-byte packets after kitbag slot indexes; likely required by skill/talent UI bootstrapping.'),
    (10097, 'S2C', 'PlayerVitalsUpdate', 'character', 'observed', 'Absolute current HP and MP refresh.', 'Captured 16-byte packet: player object id at offset 4, current HP at offset 8, current MP at offset 12; passive recovery cadence is six seconds.'),
    (10114, 'C2S', 'ItemInfoRequest', 'items', 'known', 'Client item-info request.', 'From current emulator opcode map.'),
    (10117, 'C2S', 'Forge', 'items', 'inferred', 'Client forge/refresh-style request.', 'Name from current emulator opcode map; exact behavior still needs packet confirmation.'),
    (10166, 'S2C', 'PlayerStatusUpdate', 'character', 'observed', 'Server player status/detail refresh.', 'Opcode 0x27B6 from PacketBuilder and working-server capture.'),
    (10196, 'S2C', 'SkillList', 'skills', 'observed', 'Server learned-skill list. Payload starts with current skill id, count, then 12-byte skill records.', 'Captured from working server on enter/open skill UI.'),
    (10192, 'C2S', 'ClientMovementOrLoad', 'movement', 'inferred', 'Client movement/load related packet.', 'Name from current emulator opcode map.'),
    (10194, 'C2S', 'Walk', 'movement', 'known', 'Client walk/movement packet.', 'From current emulator opcode map.'),
    (10200, 'C2S', 'PlayerDetailRequest', 'character', 'known', 'Client requests player details.', 'From current emulator opcode map.'),
    (10201, 'S2C', 'EquipmentVisualRefresh', 'items', 'observed', 'Server equipment visual refresh packet.', 'Captured after unequip; likely clears visual equipment slots.'),
    (10202, 'C2S', 'PlayerDetailAckRequest', 'character', 'known', 'Client player-detail follow-up request.', 'From current emulator opcode map.'),
    (10202, 'S2C', 'PlayerDetailRefreshAck', 'character', 'observed', 'Server player-detail refresh acknowledgement.', 'Opcode 0x27DA from PacketBuilder.'),
    (10237, 'S2C', 'SkillUiState', 'skills', 'observed', 'Skill/talent UI state payload sent by the working server before SkillList.', 'Captured on working-server enter flow; structure still under reverse engineering.'),
    (10311, 'C2S', 'ServerTimeRequest', 'session', 'known', 'Client server-time request.', 'From current emulator opcode map.'),
    (10312, 'ANY', 'UiHeartbeat', 'session', 'observed', 'Client heartbeat/UI refresh packet echoed by the working server.', 'Observed as 8-byte 0x2848 packet in both directions.'),
    (10329, 'S2C', 'EnterUiBootstrap', 'session', 'observed', 'Static UI/bootstrap payload sent by the working server immediately after EnterMain.', 'Captured before SkillList on working-server enter flow.'),
    (10342, 'C2S', 'PlayerInspectFollowup', 'character', 'known', 'Client player inspect follow-up.', 'From current emulator opcode map.'),
    (10357, 'C2S', 'EnterUnknown10357', 'character', 'inferred', 'Unknown packet seen around enter/load.', 'Name from current emulator opcode map.')
ON CONFLICT (opcode, direction) DO UPDATE
SET name = EXCLUDED.name,
    category = EXCLUDED.category,
    confidence = EXCLUDED.confidence,
    description = EXCLUDED.description,
    notes = EXCLUDED.notes,
    updated_at = now();

CREATE OR REPLACE FUNCTION set_packet_transaction_opcode_name()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.opcode_name := COALESCE((
        SELECT packet_opcodes.name
        FROM packet_opcodes
        WHERE packet_opcodes.opcode = NEW.opcode
          AND packet_opcodes.direction IN (NEW.direction, 'ANY')
        ORDER BY CASE WHEN packet_opcodes.direction = NEW.direction THEN 0 ELSE 1 END
        LIMIT 1
    ), 'Unknown');

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_packet_transactions_opcode_name ON packet_transactions;

CREATE TRIGGER trg_packet_transactions_opcode_name
BEFORE INSERT OR UPDATE OF opcode, direction
ON packet_transactions
FOR EACH ROW
EXECUTE FUNCTION set_packet_transaction_opcode_name();

UPDATE packet_transactions
SET opcode_name = COALESCE((
    SELECT packet_opcodes.name
    FROM packet_opcodes
    WHERE packet_opcodes.opcode = packet_transactions.opcode
      AND packet_opcodes.direction IN (packet_transactions.direction, 'ANY')
    ORDER BY CASE WHEN packet_opcodes.direction = packet_transactions.direction THEN 0 ELSE 1 END
    LIMIT 1
), 'Unknown');
