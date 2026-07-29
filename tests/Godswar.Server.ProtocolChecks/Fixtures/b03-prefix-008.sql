BEGIN;

DO $fixture_history$
DECLARE
    migration_count integer;
    migration_head text;
BEGIN
    SELECT count(*)::integer, max(migration_id)
    INTO migration_count, migration_head
    FROM public.schema_migrations;

    IF migration_count <> 9 OR
       migration_head <> '20260723_008_zodiac_skill_grid_state' THEN
        RAISE EXCEPTION
            'B03 fixture requires the exact migration-008 prefix, got % rows through %',
            migration_count,
            migration_head;
    END IF;
END
$fixture_history$;

INSERT INTO public.accounts (
    id,
    uuid,
    email,
    username,
    last_login_time,
    last_logout_time
)
VALUES (
    -903,
    'b03-synthetic-account',
    'b03-fixture@invalid.example',
    'b03_fixture_008',
    '2026-07-29 00:00:00+00',
    '2026-07-29 00:00:00+00'
);

INSERT INTO public.character_base (
    id,
    account_id,
    server_id,
    name,
    camp,
    profession,
    fighter_job_lv,
    "Map",
    "Pos_X",
    "Pos_Z",
    "Register_time",
    "LastLogin_time"
)
VALUES (
    -903,
    -903,
    1,
    'B03Fixture008',
    1,
    0,
    17,
    1,
    165.0,
    -97.0,
    '2026-07-29 00:00:00+00',
    '2026-07-29 00:00:00+00'
);

INSERT INTO public.character_items (
    id,
    user_id,
    item_location,
    slot_index,
    prop_id,
    item_quality,
    item_grade,
    bound,
    stack,
    created_at,
    updated_at
)
VALUES (
    -903,
    -903,
    1,
    119,
    4000,
    1,
    1,
    1,
    7,
    '2026-07-29 00:00:00+00',
    '2026-07-29 00:00:00+00'
);

INSERT INTO public.packet_capture_sessions (
    id,
    started_at,
    capture_name,
    output_path
)
VALUES (
    'b0300000-0000-4000-8000-000000000008',
    '2026-07-29 00:00:00+00',
    'B03 synthetic prefix-008 fixture',
    'synthetic://b03/prefix-008'
);

INSERT INTO public.packet_transactions (
    id,
    capture_session_id,
    captured_at,
    connection_id,
    connection_name,
    direction,
    chunk_sequence,
    packet_sequence,
    packet_index,
    stream_offset,
    declared_length,
    actual_length,
    opcode,
    clear_bytes,
    raw_bytes,
    notes
)
VALUES (
    -903,
    'b0300000-0000-4000-8000-000000000008',
    '2026-07-29 00:00:01+00',
    'b0300000-0000-4000-8000-000000000009',
    'game',
    'S2C',
    1,
    1,
    0,
    0,
    4,
    4,
    10090,
    decode('04010203', 'hex'),
    decode('0401a2b3', 'hex'),
    'B03 synthetic durability sentinel'
);

COMMIT;
