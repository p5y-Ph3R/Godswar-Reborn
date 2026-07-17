CREATE TABLE IF NOT EXISTS monster_spawn_packets (
    map_id smallint NOT NULL,
    scene_key varchar(96) NOT NULL,
    template_key varchar(128) NOT NULL,
    display_name varchar(255) NOT NULL DEFAULT '',
    object_id bigint NOT NULL,
    pos_x real NOT NULL,
    pos_z real NOT NULL,
    clear_bytes bytea NOT NULL,
    source varchar(64) NOT NULL DEFAULT 'capture_proxy',
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    capture_count integer NOT NULL DEFAULT 1,
    PRIMARY KEY (map_id, object_id)
);

CREATE INDEX IF NOT EXISTS ix_monster_spawn_packets_map
    ON monster_spawn_packets (map_id, template_key);
