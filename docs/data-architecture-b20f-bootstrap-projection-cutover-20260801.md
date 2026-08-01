# B20F bootstrap and projection cutover

Status: completed local code increment on 2026-08-01; no live database,
player data, or deployed environment was changed. Verification used only a
named disposable PostgreSQL database.

## Outcome

B20F separates PostgreSQL schema startup from the broad gameplay store,
removes the historical Docker init-directory bootstrap, and moves every
production and operational-tool loadout reader to authoritative
`character_items` rows. The compatibility views remain installed for a later,
measured B20H observation and rollback window; this slice does not drop them.

The SecureSmoke transient-account fixture also no longer composes
`PostgresGameStore`. It uses the focused PostgreSQL account adapter and the
durable character-lifecycle command executor.

## Schema startup boundary

`Infrastructure/Database/PostgresSchemaStartup.cs` owns the migration runner
and its bounded PostgreSQL readiness retry. `Program.cs` completes schema
startup before composing persistence adapters. The semantic gateway uses the
same boundary with its owned `NpgsqlDataSource`.

`PostgresGameStore.EnsureSeedDataAsync` no longer applies migrations. Its
remaining compatibility publication work is intentionally separate and is
retired by B20G. The release-path checks now invoke schema startup explicitly,
so empty-install, restored-prefix upgrade, and current-schema idempotence all
exercise the production startup boundary.

The Compose PostgreSQL service no longer mounts `database/postgres` at
`/docker-entrypoint-initdb.d`. Embedded, ordered, checksum-verified migrations
are therefore the only server schema path.

## Authoritative item projection

`PostgresCharacterItemProjectionSql` constructs the native 24-slot equipment
and 96-slot bag strings directly from `character_items` and the exact
process-pinned `item_template_content_definitions` revision. It preserves the
previous wire representation exactly:

- sparse slots remain `[]#`;
- quality and grade retain item-template and G25 clamps;
- bound, stack, item experience, holy-suit code, five attributes and levels,
  socket count, and all six socket pairs retain their field order; and
- the existing unique `(user_id, item_location, slot_index)` key bounds each
  lookup.

The character snapshot reader, broad-store compatibility reads, equipment
movement checks, and `tools/SetEquippedWeapon.ps1` no longer query
`character_item_loadout`. A PostgreSQL parity check compares direct projection
bytes with the retained compatibility view for empty and sparse last-slot
fixtures and is registered in the mandatory B03 disposable-database gate.

The view is deliberately retained. Dropping it before the approved zero-use
window would break prior-binary rollback and would turn staged retirement into
an irreversible cutover.

## SecureSmoke fixture boundary

`TransientAccountFixture` now owns one pooled data source and composes:

- `PostgresAccountStore` for versioned transient-account registration; and
- `ICharacterLifecycleCommandExecutor` through
  `PostgresCharacterLifecycleCommandExecutor` for authoritative character
  creation.

Its random account is still removed after the session becomes offline.
Account-owned character rows are removed by the schema's cascade, while the
durable lifecycle audit/outbox evidence remains governed by its permanent
retention policy.

## Ratchet reduction

| B20F dependency | Before | After |
| --- | ---: | ---: |
| Production/tool `character_item_loadout` readers | 8 | 0 |
| Legacy Docker init mounts | 1 | 0 |
| SecureSmoke `PostgresGameStore` references | 3 | 0 |

The compatibility-view declarations and migration preconditions are not
runtime readers and remain until B20H. Historical SQL fixtures are likewise
not runtime authority.

## Verification

- Release solution build: passed with zero warnings and zero errors.
- SecureSmoke Release build: passed with zero warnings and zero errors.
- B20F startup/projection architecture check: passed.
- Data-boundary and B20 legacy-persistence ratchets: passed with no new or
  stale boundary debt.
- Source/tool search: zero production or operational-tool reads from
  `character_item_loadout`; only migration definitions/preconditions and a
  non-reader explanatory comment remain.
- Diff whitespace validation: passed.

The PostgreSQL byte-parity check is part of the mandatory B03 gate. It also
changes mutable staging values and proves that both the direct projection and
retained compatibility view continue to use the immutable official item
publication.

## Limits and next slices

B20F does not remove compiled content seed consumers, publish content through
the B20G boundary, claim a production zero-use window, or drop either
compatibility view. B20G owns content publication and research-capture
separation. B20H owns the measured observation window, reconciliation,
backup/restore and rollback evidence, and only then the archive/drop decision.
