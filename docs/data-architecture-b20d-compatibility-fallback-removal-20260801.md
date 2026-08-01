# B20D compatibility fallback removal

Status: completed local architecture increment on 2026-08-01; no database
schema, live player data, live container, or deployed environment was changed

Follow-on status (2026-08-01): B20E-G are implemented locally. B20H's real
seven-day deployed observation and final deletion remain pending.

## Outcome

B20D removes the five remaining broad compatibility mutation paths for pet
hatch, pet level, pet presence, online boost consumption, and Zodiac online
accrual. PostgreSQL gameplay now reaches those mutations only through the
existing focused durable command contracts.

The process-local `LegacyCharacterCheckpointStore` adapter is also removed.
PostgreSQL continues to use `PostgresCharacterCheckpointStore`. The temporary
local JSON authority implements `ICharacterCheckpointStore` directly, so the
checkpoint coordinator no longer calls `IGameStore` or a concrete JSON
checkpoint helper.

This is an application-boundary cutover. It does not add a migration or alter
the durable PostgreSQL schema.

## Pet command boundary

Pet hatch, level-up, carry, summon, and recall require an explicit operation
identity and `IPetDurableCommandExecutor`. The handler no longer downgrades an
unidentified request into a JSON or broad-store mutation.

- `GameClientHandler.InventoryActivation` refreshes the authoritative bag and
  rejects an unidentified pet egg.
- `GameClientHandler.PetLevel` and `GameClientHandler.Pets` reject unidentified
  mutation requests and record the finite unsupported-identity command metric.
- `GameClientHandler.PetEggs.cs` and the JSON pet mutation stubs are removed.
- Secure handler tests now use a narrow durable executor, ownership fence, and
  post-commit character/pet snapshot rather than overriding `IGameStore`.

This makes an existing production rule explicit: the semantic gateway must
attach the operation ID used by inbox replay protection. A raw original-client
connection cannot safely perform these mutations without that translation.

## Progression settlement boundary

`GameSessionRegistry` now treats
`IProgressionIntervalSettlementCommandExecutor` as the only online-duration
mutation authority. Missing executor composition is a no-op in compatibility
tests and fails the production composition guard; it never falls back to
`IGameStore`.

The removed JSON and broad PostgreSQL methods previously updated boost time
and Zodiac energy separately. The durable interval command already owns both
changes atomically, preserves an exact server operation ID across uncertain
retries, and excludes offline time.

B20D also closes a live-mirror race between an interval settlement and a
Zodiac-level upgrade. Both operations now serialize through the same
per-session durable progression gate. An unknown interval outcome is retried
before the level mutation, and the authoritative projection is applied in
commit order.

The serialization check covers both accrual-first ordering and the reverse
case where a pending interval retry starts the level-up while a newer accrual
is waiting. The live Zodiac projection must retain the newest committed
online timestamp and duration in both orders.

## Focused JSON checkpoints

`JsonGameStore` directly implements `ICharacterCheckpointStore` while JSON is
still selectable for local development. Its acquire, write, and release
operations share the path-scoped JSON lock and the process-local ownership
map. Writes compare the current persisted revision and payload while holding
that lock and report `Applied`, `AlreadyApplied`, `Superseded`,
`RevisionConflict`, `OwnershipLost`, or `CharacterNotFound` accurately.

The JSON fence remains deliberately process-local and is not scale-out
authority. B20E removes the selectable JSON server authority; the focused
implementation exists only to preserve truthful local checkpoint semantics
during that staged removal.

## Ratchet reduction

| Measure | B20C | B20D |
| --- | ---: | ---: |
| Broad `IGameStore` calls | 33 | 25 |
| Concrete JSON checkpoint calls | 1 | 0 |
| Total tracked legacy invocations | 34 | 25 |
| Read calls | 1 | 0 |
| Mutation or mixed calls | 32 | 24 |
| Bootstrap calls | 1 | 1 |
| Broad `IGameStore` methods | 29 | 24 |
| Broad caller files | 18 | 13 |
| Invoked broad members | 27 | 21 |
| External `IGameStore` type references | 7 | 7 |
| `_store` identifier references | 52 | 41 |
| Tracked `store` parameter references | 12 | 11 |
| Legacy-State Npgsql references | 323 | 315 |

Telemetry coverage shrinks with the reviewed source inventory: all 25
remaining invocations map to 21 finite operation names. Removed numeric enum
values were not reused or renumbered.

## Verification

- Release solution build: passed with zero warnings and zero errors.
- Data-boundary ratchet: 25/25 broad calls and 24/24 methods, with no new or
  stale debt and no layering violation.
- B20 retirement ratchet: 25/25 tracked calls, zero reads, 24 mutation/mixed,
  one bootstrap, and no new or stale debt.
- Legacy telemetry: 25/25 calls instrumented across 21 finite operations.
- Managed protocol suite: 300 passed and zero failed.
- Mandatory disposable PostgreSQL 17 gate: 45 required checks and five
  migration scenarios passed in 452,008 ms.
- The gate's loopback-only PostgreSQL container, temporary roles, and
  databases were removed. The live game and PostgreSQL containers remained
  healthy and unchanged.

## Limits and next slice

B20D does not remove the remaining 25 broad invocations, the broad store
implementations, selectable JSON authority, legacy schema bootstrap, legacy
loadout projection, or compiled/capture-backed content consumers. It does not
claim a production zero-use observation window.

B20E should next make runtime composition PostgreSQL-only and remove
`JsonGameStore*`, `GameDatabase`, JSON provider and snapshot switches,
`DataPath`/`GODSWAR_DATA_PATH`, JSON configurations, and the B18C JSON worker
profile. Tests that still need local state should use explicit narrow fakes,
not a selectable alternate server authority.
