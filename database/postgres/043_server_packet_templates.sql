CREATE TABLE IF NOT EXISTS server_packet_templates (
    template_key varchar(96) NOT NULL,
    sequence smallint NOT NULL,
    opcode integer NOT NULL,
    direction varchar(4) NOT NULL DEFAULT 'S2C',
    clear_bytes bytea NOT NULL,
    notes text NOT NULL DEFAULT '',
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (template_key, sequence)
);

INSERT INTO server_packet_templates (
    template_key,
    sequence,
    opcode,
    direction,
    clear_bytes,
    notes
)
SELECT
    'enter_syn_game_data',
    row_number() OVER (ORDER BY id)::smallint,
    opcode,
    direction,
    clear_bytes,
    'Historical accepted-quest snapshot from the working server. Character-specific; retained for protocol research and blocked from login replay.'
FROM packet_transactions
WHERE direction = 'S2C'
  AND opcode = 10090
  AND actual_length = 2048
  AND id BETWEEN 85538 AND 85542
ON CONFLICT (template_key, sequence) DO UPDATE
SET opcode = EXCLUDED.opcode,
    direction = EXCLUDED.direction,
    clear_bytes = EXCLUDED.clear_bytes,
    notes = EXCLUDED.notes,
    updated_at = now();
