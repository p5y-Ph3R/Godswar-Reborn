namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string WarehouseSchemaSql =
        """
        DO $warehouse_legacy_storage_preflight$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM legacy.character_kitbag_archive archive
                WHERE length(btrim(COALESCE(archive.storage, ''))) > 0
            ) THEN
                RAISE EXCEPTION
                    'Legacy warehouse payloads require an audited importer before warehouse activation.'
                    USING ERRCODE = '55000';
            END IF;
        END;
        $warehouse_legacy_storage_preflight$;

        ALTER TABLE public.character_base
            ADD COLUMN warehouse_capacity smallint NOT NULL DEFAULT 40,
            ADD COLUMN warehouse_revision bigint NOT NULL DEFAULT 0,
            ADD CONSTRAINT ck_character_base_warehouse_capacity CHECK (
                warehouse_capacity IN (40, 80, 120, 160)
            ),
            ADD CONSTRAINT ck_character_base_warehouse_revision
                CHECK (warehouse_revision >= 0);

        ALTER TABLE public.character_items
            DROP CONSTRAINT ck_character_items_location,
            DROP CONSTRAINT ck_character_items_location_slot_domain,
            ADD CONSTRAINT ck_character_items_location
                CHECK (item_location IN (0, 1, 2, 3)),
            ADD CONSTRAINT ck_character_items_location_slot_domain CHECK (
                (item_location = 0 AND slot_index BETWEEN 0 AND 23)
                OR (item_location = 1 AND slot_index BETWEEN 0 AND 32767)
                OR (item_location = 2 AND slot_index BETWEEN -32768 AND -1)
                OR (item_location = 3 AND slot_index BETWEEN 0 AND 159)
            );

        CREATE TABLE public.warehouse_expansion_policy_revisions (
            revision bigint PRIMARY KEY,
            sha256 varchar(64) NOT NULL,
            level_count smallint NOT NULL,
            source varchar(128) NOT NULL,
            created_by varchar(128) NOT NULL,
            created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            sealed_at timestamptz,
            CONSTRAINT uq_warehouse_policy_revision_identity
                UNIQUE (revision, sha256),
            CONSTRAINT ck_warehouse_policy_revision CHECK (revision > 0),
            CONSTRAINT ck_warehouse_policy_sha256
                CHECK (sha256 ~ '^[0-9A-F]{64}$'),
            CONSTRAINT ck_warehouse_policy_level_count
                CHECK (level_count = 4),
            CONSTRAINT ck_warehouse_policy_source
                CHECK (length(btrim(source)) BETWEEN 1 AND 128),
            CONSTRAINT ck_warehouse_policy_created_by
                CHECK (length(btrim(created_by)) BETWEEN 1 AND 128),
            CONSTRAINT ck_warehouse_policy_sealed_at
                CHECK (sealed_at IS NULL OR sealed_at >= created_at)
        );

        CREATE TABLE public.warehouse_expansion_policy_levels (
            revision bigint NOT NULL REFERENCES
                public.warehouse_expansion_policy_revisions(revision)
                ON DELETE RESTRICT,
            capacity smallint NOT NULL,
            key_cost smallint NOT NULL,
            key_item_id integer NOT NULL,
            PRIMARY KEY (revision, capacity),
            CONSTRAINT ck_warehouse_policy_level_capacity
                CHECK (capacity IN (40, 80, 120, 160)),
            CONSTRAINT ck_warehouse_policy_level_stock_shape CHECK (
                (capacity, key_cost, key_item_id) IN (
                    (40, 0, 4102),
                    (80, 1, 4102),
                    (120, 2, 4102),
                    (160, 3, 4102)
                )
            )
        );

        CREATE TABLE public.warehouse_expansion_policy_publication (
            family varchar(32) PRIMARY KEY,
            revision bigint NOT NULL,
            policy_sha256 varchar(64) NOT NULL,
            publication_version bigint NOT NULL,
            updated_by varchar(128) NOT NULL,
            updated_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            CONSTRAINT fk_warehouse_policy_publication_identity
                FOREIGN KEY (revision, policy_sha256) REFERENCES
                    public.warehouse_expansion_policy_revisions(
                        revision, sha256),
            CONSTRAINT ck_warehouse_policy_publication_family
                CHECK (family = 'warehouse-expansion'),
            CONSTRAINT ck_warehouse_policy_publication_version
                CHECK (publication_version > 0),
            CONSTRAINT ck_warehouse_policy_publication_sha256
                CHECK (policy_sha256 ~ '^[0-9A-F]{64}$'),
            CONSTRAINT ck_warehouse_policy_publication_actor
                CHECK (length(btrim(updated_by)) BETWEEN 1 AND 128)
        );

        CREATE TABLE public.warehouse_expansion_policy_audit (
            publication_version bigint PRIMARY KEY,
            previous_revision bigint,
            revision bigint NOT NULL,
            previous_sha256 varchar(64),
            policy_sha256 varchar(64) NOT NULL,
            changed_at timestamptz NOT NULL,
            changed_by varchar(128) NOT NULL,
            CONSTRAINT fk_warehouse_policy_audit_identity
                FOREIGN KEY (revision, policy_sha256) REFERENCES
                    public.warehouse_expansion_policy_revisions(
                        revision, sha256),
            CONSTRAINT ck_warehouse_policy_audit_version
                CHECK (publication_version > 0),
            CONSTRAINT ck_warehouse_policy_audit_previous CHECK (
                (previous_revision IS NULL) = (previous_sha256 IS NULL)
            ),
            CONSTRAINT ck_warehouse_policy_audit_sha256 CHECK (
                policy_sha256 ~ '^[0-9A-F]{64}$'
                AND (previous_sha256 IS NULL
                    OR previous_sha256 ~ '^[0-9A-F]{64}$')
            ),
            CONSTRAINT ck_warehouse_policy_audit_actor
                CHECK (length(btrim(changed_by)) BETWEEN 1 AND 128)
        );

        CREATE TABLE public.warehouse_expansion_settlements (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            account_id integer NOT NULL,
            character_id integer NOT NULL,
            previous_capacity smallint NOT NULL,
            current_capacity smallint NOT NULL,
            keys_consumed smallint NOT NULL,
            key_item_id integer NOT NULL,
            policy_revision bigint NOT NULL,
            policy_sha256 varchar(64) NOT NULL,
            warehouse_revision bigint NOT NULL,
            inventory_revision bigint NOT NULL,
            item_mutations jsonb NOT NULL,
            command_inbox_id bigint NOT NULL UNIQUE REFERENCES
                public.command_inbox(id) ON DELETE RESTRICT,
            audit_id bigint NOT NULL UNIQUE REFERENCES
                public.command_audit(id) ON DELETE RESTRICT,
            capacity_event_id uuid NOT NULL UNIQUE,
            inventory_event_id uuid NOT NULL UNIQUE,
            expanded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            CONSTRAINT fk_warehouse_expansion_character_identity
                FOREIGN KEY (character_id, account_id) REFERENCES
                    public.character_economy_baseline(
                        character_id, account_id) ON DELETE RESTRICT,
            CONSTRAINT fk_warehouse_expansion_policy_identity
                FOREIGN KEY (policy_revision, policy_sha256) REFERENCES
                    public.warehouse_expansion_policy_revisions(
                        revision, sha256) ON DELETE RESTRICT,
            CONSTRAINT fk_warehouse_expansion_capacity_event
                FOREIGN KEY (capacity_event_id) REFERENCES
                    public.outbox_events(event_id)
                    ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED,
            CONSTRAINT fk_warehouse_expansion_inventory_event
                FOREIGN KEY (inventory_event_id) REFERENCES
                    public.outbox_events(event_id)
                    ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED,
            CONSTRAINT ck_warehouse_expansion_capacities CHECK (
                previous_capacity IN (40, 80, 120)
                AND current_capacity = previous_capacity + 40
            ),
            CONSTRAINT ck_warehouse_expansion_keys CHECK (
                keys_consumed BETWEEN 1 AND 99
                AND key_item_id > 0
            ),
            CONSTRAINT ck_warehouse_expansion_revisions CHECK (
                warehouse_revision > 0 AND inventory_revision > 0
            ),
            CONSTRAINT ck_warehouse_expansion_mutations CHECK (
                jsonb_typeof(item_mutations) = 'array'
                AND jsonb_array_length(item_mutations) BETWEEN 1 AND 96
                AND octet_length(item_mutations::text) <= 65536
            )
        );

        CREATE INDEX ix_warehouse_expansion_character_time
            ON public.warehouse_expansion_settlements (
                character_id, expanded_at DESC, id);
        """;
}
