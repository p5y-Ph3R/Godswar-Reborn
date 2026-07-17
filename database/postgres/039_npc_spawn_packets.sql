CREATE TABLE IF NOT EXISTS npc_spawn_packets (
    map_id smallint NOT NULL,
    scene_key varchar(64) NOT NULL,
    npc_key varchar(96) NOT NULL,
    template_key varchar(128) NOT NULL,
    object_id bigint NOT NULL,
    pos_x real NOT NULL,
    pos_z real NOT NULL,
    clear_bytes bytea NOT NULL,
    detail_10077 bytea NOT NULL DEFAULT '\x'::bytea,
    detail_10080 bytea NOT NULL DEFAULT '\x'::bytea,
    source varchar(64) NOT NULL DEFAULT 'capture_proxy',
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    capture_count integer NOT NULL DEFAULT 1,
    PRIMARY KEY (map_id, template_key)
);

ALTER TABLE npc_spawn_packets
    ADD COLUMN IF NOT EXISTS detail_10077 bytea NOT NULL DEFAULT '\x'::bytea;

ALTER TABLE npc_spawn_packets
    ADD COLUMN IF NOT EXISTS detail_10080 bytea NOT NULL DEFAULT '\x'::bytea;

CREATE INDEX IF NOT EXISTS ix_npc_spawn_packets_map
    ON npc_spawn_packets (map_id, npc_key);

CREATE INDEX IF NOT EXISTS ix_npc_spawn_packets_object
    ON npc_spawn_packets (map_id, object_id);
