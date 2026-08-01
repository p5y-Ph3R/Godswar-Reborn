# B20E PostgreSQL-only runtime composition

Status: completed local architecture increment on 2026-08-01; no database
schema, live player data, live container, trust store, or deployed environment
was changed

## Outcome

B20E removes selectable JSON authority from every production server entry
point. `Program.cs`, the semantic-gateway command, gameplay persistence,
account persistence, snapshot telemetry, and world-content startup now compose
PostgreSQL implementations only. A missing connection string or any storage
provider other than `Postgres` fails before the server creates runtime state.

This is a composition and compatibility-fixture cutover. It does not remove
the still-observed broad `IGameStore` PostgreSQL calls, legacy PostgreSQL
schema bootstrap, or generated/capture-backed content consumers assigned to
later B20 slices.

## Production boundary

The production source tree no longer contains any of these selectable
authority mechanisms:

- `GameStorageProviderKind.Json`;
- `CharacterSnapshotProvider.Json`;
- `DataPath` or `GODSWAR_DATA_PATH`;
- `JsonGameStore*` or `GameDatabase`;
- the JSON semantic-gateway session;
- the generated world-content fallback; or
- a JSON branch in account/gameplay persistence composition.

`Storage.Provider` and `GODSWAR_STORAGE_PROVIDER` remain explicit fail-closed
markers. Their only accepted value is `Postgres`; keeping the marker makes a
stale deployment configuration fail visibly instead of silently choosing a
different authority.

The checked-in local, Docker, and backhaul-worker settings now select
PostgreSQL. The local examples use the same loopback development database
identity already documented by the Docker stack. Production credentials must
continue to arrive through deployment secret management rather than these
development defaults.

## Test-only JSON compatibility fixtures

The retired JSON implementation remains temporarily available only under:

`tests/Godswar.Server.ProtocolChecks/CompatibilityFixtures/JsonAuthority`

That folder contains the ten `JsonGameStore*` files, `GameDatabase`, the
legacy semantic-gateway data session, and the generated content reader. Their
namespaces are retained so existing deterministic compatibility checks can
exercise historical behavior without making JSON selectable by the server.

`JsonCompatibilityGameplayPersistenceComposition` is an explicitly test-only
helper. Production `ServerGameplayPersistenceComposition.Create` accepts a
non-null `PostgresApplicationDataRuntime` only. The production snapshot metric
therefore has one finite provider label: `postgresql`.

The shared `FactionAreaExperienceControl` DTO was extracted into
`src/Godswar.Server/State/FactionAreaExperienceControl.cs` because live
PostgreSQL gameplay still uses it; it is not part of the retired JSON
aggregate.

## PostgreSQL B18C smoke

The two-process relay/worker smoke no longer creates a JSON data directory or
sets `GODSWAR_DATA_PATH`. It requires an explicit isolated connection string
through `GODSWAR_B18C_POSTGRES_CONNECTION_STRING`, passes that value to the
worker as `GODSWAR_POSTGRES_CONNECTION_STRING`, and selects `Postgres` in both
the generated configuration and environment.

The smoke remains bounded and loopback-only. Callers own creation and cleanup
of its isolated PostgreSQL database; the smoke will not fall back to a local
file when the connection option is missing.

## Ratchet reduction

| Measure | B20D | B20E |
| --- | ---: | ---: |
| Production `JsonGameStore*` implementation files | 10 | 0 |
| Production JSON provider branches | 7 | 0 |
| Production JSON snapshot branches | 2 | 0 |
| Production generated world-content fallbacks | 2 | 0 |
| Checked-in JSON provider configurations | 2 | 0 |
| B18C JSON provider selections | 2 | 0 |
| Production `DataPath` / `GODSWAR_DATA_PATH` references | present | 0 |
| Broad `IGameStore` calls | 25 | 25 |

The broad-call count deliberately does not move in B20E. Those calls are
PostgreSQL compatibility paths still subject to the B20 observation and
retirement gates.

## Verification

Local verification on the combined B20E/F/G working tree established:

- Release solution build: passed with zero warnings and zero errors;
- data-boundary ratchet: 25/25 broad calls, 24/24 methods, no new or stale
  debt, and no layering violation;
- B20 retirement ratchet: 25/25 tracked calls, zero reads, 24
  mutation/mixed calls, one bootstrap call, zero production JSON store files,
  and no new or stale debt;
- production/config/B18C scan: zero JSON authority, `DataPath`, JSON snapshot,
  or generated fallback references;
- JSON compatibility snapshot and semantic-gateway fixture checks: passed;
- PostgreSQL-only runtime-profile and snapshot-metric checks: passed;
- B17 coordination and B19 reconciliation configuration checks: passed; and
- checked-in configuration JSON and the B18C PowerShell wrapper parse
  successfully.

The final combined B20 gate also includes content-publication work from B20G;
its evidence is recorded by that slice rather than attributed to B20E.

## Rollback and limits

Rollback is an application-release rollback, not a data rollback. Keep the
current PostgreSQL data and additive migrations, stop the new binary, and
restore a previously verified compatibility release only on a controlled
local host. Do not copy the test fixture back into production composition or
restore unknown-provider-to-JSON behavior.

B20E does not claim that broad PostgreSQL compatibility paths have completed
their production zero-use observation window. It does not authorize deleting
legacy schema objects, captured evidence, or test fixtures. Those actions
require their own verified B20 gates and a backup-aware rollback point.
