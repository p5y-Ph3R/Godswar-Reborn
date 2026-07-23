# PostgreSQL migration and cleanup policy

`PostgresSchemaMigrationRunner` is the only runtime schema-migration path.
It takes a session advisory lock, validates immutable SHA-256 checksums, and
applies each explicitly registered migration in its own transaction.
Recorded history must be an exact ordered prefix of the migrations registered
by the running server. Startup fails closed for unknown IDs, gaps, reordered
history, checksum drift, or a database whose history is ahead of the binary.

The files under `database/postgres` are the historical bootstrap source. The
runtime never scans that directory, so local character fixtures cannot be
replayed accidentally. Test-character scripts live under `database/fixtures`.

## 2026-07-23 cleanup

The pre-change logical dump, schema dump, checksums, and row-count invariants
are recorded in
`backups/postgres-pre-ecs-20260723/manifest.md`.

The forward migrations:

1. normalize the `accounts.username` unique constraint and remove only
   genuinely redundant indexes;
2. restore stock client templates for HP potion `4000` and MP potion `4030`,
   preserving the existing inventory rows;
3. require the obsolete `public.character_kitbag` source to exist, copy every
   row byte-for-byte to `legacy.character_kitbag_archive`, prove exact
   source/archive parity in both directions (excluding the archive-only
   `archived_at` timestamp), and drop the source table with `RESTRICT`;
4. add and validate a restrictive foreign key from
   `character_items.prop_id` to `item_templates.id`.

No cleanup migration deletes or rewrites an authoritative
`character_items` row. The `character_item_loadout` and `character_equip`
views remain compatibility projections over that table.

## Table ownership after cleanup

The database intentionally contains more than live character state. Cleanup
classifies the relations instead of deleting tables merely because their
current row count is zero:

- `accounts`, `character_base`, `character_items`, skills, talents, boosts,
  faction-area control, and item audit are mutable game state;
- item, attribute, class, skill, talent, rank, map, NPC, monster, holy-suit,
  and world-boss tables are server catalogs;
- `packet_capture_sessions`, `packet_transactions`, captured spawn packets,
  packet opcodes, and packet templates are protocol-research evidence used by
  the importer and fallback synchronizers;
- `character_*` summary/loadout relations are read-only compatibility views;
- retired projections live under the `legacy` schema and are never read by
  normal game operations.

Moving the capture corpus into a separate physical database is a future
retention change, not part of this data-integrity migration. The current
cleanup therefore removes only a relation proven redundant
(`public.character_kitbag`) and duplicate indexes whose authoritative
constraint/index remains intact.

## Restored-backup validation

The custom-format pre-ECS dump was restored into an isolated PostgreSQL 17
database. The restored invariants were 9 accounts, 7 characters, 87
authoritative items, 6 legacy kitbag rows, 1,301 item templates, 10
missing-template item references, and 10,105 captured packet transactions.

The final hardened rehearsal of migrations `20260723_000` through
`20260723_007` and the complete PostgreSQL protocol/integration suite
produced:

- 99 checks passed and 0 failed;
- all 8 migration IDs/checksums recorded;
- 6 archived legacy rows and no remaining public kitbag table;
- all 87 authoritative item rows preserved;
- 1,303 item templates and 0 missing-template item references;
- a validated restrictive item-template foreign key; and
- only the authoritative username and item-location indexes remaining.

This rehearsal used a newly restored disposable PostgreSQL 17 database after
migration `20260723_006` gained the fail-closed source-existence and
bidirectional parity checks. The live database was not used as an integration
test target.

## Adding a migration

- Add it explicitly to `PostgresSchemaMigrationCatalog` with an ascending,
  immutable ID in `YYYYMMDD_NNN_lowercase_name` form.
- Make the SQL safe inside one transaction and fail closed when prerequisites
  are missing.
- Reconcile retained data before validating a new constraint.
- Add both plan/checksum coverage and a PostgreSQL integration check.
- Never edit a migration after it has reached a database. Add a new forward
  migration instead.
