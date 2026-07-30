# B18A realm and world-instance identity foundation

Status: completed and verified on 2026-07-31

## Outcome

B18A establishes the local identities and placement contract required before
maps, dungeons, battlefields, or realms can be split across processes:

- Tempest is the authoritative first logical realm with `RealmId = 1`.
- A server process/node, logical realm, reusable map definition, and live
  world instance now have distinct types.
- World-instance lifecycle and bounded local placement behavior are explicit.
- `MapInstance` carries the typed identity while preserving the legacy
  client-facing byte map ID.
- PostgreSQL enforces the current Tempest-only character invariant.

[ADR 0004](adr/0004-realm-and-world-instance-topology.md) defines the target
gateway/worker and cross-realm topology. B18A remains a local-first
foundation; it does not implement that distributed deployment.

## PostgreSQL Tempest authority

The implementation reuses the existing `public.server` table as the logical
realm catalog. It does not create a duplicate realm table or rename the
legacy catalog.

Migration
`20260731_035_tempest_realm_authority` is defined in
[PostgresSchemaMigrationCatalog.Realms.cs](../src/Godswar.Server/State/DatabaseMigrations/PostgresSchemaMigrationCatalog.Realms.cs)
and registered by
[PostgresSchemaMigrationCatalog.cs](../src/Godswar.Server/State/DatabaseMigrations/PostgresSchemaMigrationCatalog.cs).
It:

1. requires realm row `id = 1` to retain the exact Tempest name and legacy
   identifier;
2. requires the existing validated
   `character_base.server_id -> server.id` foreign key;
3. rejects any pre-existing non-null character realm other than Tempest;
4. backfills only null `character_base.server_id` values to `1`;
5. sets default `1` and `NOT NULL`;
6. adds validated check `ck_character_base_tempest_realm` with
   `server_id = 1`; and
7. adds `ix_character_base_server`.

The migration is applied to the live development database:

| Evidence | Result |
| --- | --- |
| Applied migration count/head | `36`; `20260731_035_tempest_realm_authority` |
| Character realm rows | `9/9` have `server_id = 1`; zero are null |
| Column contract | `NOT NULL`, default `1` |
| Foreign key | `character_base_server_id_fkey`, validated |
| Tempest-only check | `ck_character_base_tempest_realm`, validated |
| Lookup index | `ix_character_base_server` present |

Both character-create paths now use `RealmId.Tempest.Value` rather than an
unexplained numeric literal:

- [PostgresCharacterLifecycleCommandExecutor.Create.cs](../src/Godswar.Server/Infrastructure/Characters/PostgresCharacterLifecycleCommandExecutor.Create.cs)
- [PostgresGameStore.Characters.Persistence.cs](../src/Godswar.Server/State/PostgresGameStore.Characters.Persistence.cs)

The check intentionally prevents a premature realm-two character. Before a
second realm is enabled, a new forward migration must accompany realm-aware
character lifecycle, loading, account-slot/name policy, placement, and
client selection. Child character tables do not copy `RealmId`; globally
unique `character_id` remains their owner key.

## Typed runtime identities

[WorldInstanceIdentifiers.cs](../src/Godswar.Server/Domain/World/Instances/WorldInstanceIdentifiers.cs)
defines:

| Type | Contract |
| --- | --- |
| `RealmId` | Positive integer; `RealmId.Tempest.Value == 1` |
| `ServerNodeId` | Opaque validated ASCII node/process identifier, maximum 64 characters; local default `local-node` |
| `WorldInstanceId` | Non-empty GUID identity for one simulation |
| `MapId` | Nonnegative `short` map/content identity with a checked legacy-byte bridge |

These identities cannot be substituted for one another. In particular,
secure networking's current `TargetServerId = 100` remains a protocol
audience/routing value, not Tempest's durable realm ID.

[WorldInstanceDescriptor.cs](../src/Godswar.Server/Domain/World/Instances/WorldInstanceDescriptor.cs)
adds:

- `InstanceKind.OpenWorld`, `Battlefield`, and `Dungeon`;
- lifecycle `Creating -> Active -> Draining -> Closed`;
- direct `Creating -> Closed` cancellation;
- monotonic lifecycle revision and nondecreasing transition time;
- immutable realm, instance, map, kind, creation time, and capacity;
- player capacity bounded from `1` through `100,000`.

Closed instances cannot be reactivated under the same identity.

## Bounded local placement

[WorldInstancePlacementContracts.cs](../src/Godswar.Server/Application/WorldInstances/WorldInstancePlacementContracts.cs)
separates application placement intent from the storage/coordination
implementation. Its operations are cancellation-aware `ValueTask` contracts
for registering, transitioning, assigning, transferring, releasing,
removing, and locating instances/characters.

[LocalWorldInstancePlacementRegistry.cs](../src/Godswar.Server/Application/WorldInstances/LocalWorldInstancePlacementRegistry.cs)
is the one-process implementation. It is bounded and lock-serialized:

- configured instance capacity must be between `1` and `65,536`;
- configured character-assignment capacity must be between `1` and
  `1,000,000`;
- each `WorldInstanceId` registers once for the local registry lifetime;
- one open-world instance may own a `(RealmId, MapId)` pair;
- battlefield/dungeon instances may share a `MapId` while retaining distinct
  `WorldInstanceId` values;
- one character may be assigned to only one active instance;
- assignment and transfer validate lifecycle, source, registry capacity, and
  per-instance capacity;
- lifecycle writes use expected revisions;
- a draining instance cannot close until every assignment is explicitly
  released or transferred;
- removal accepts only closed instances and records the terminal identity in
  a bounded retired-ID ledger; and
- a full retirement ledger fails closed instead of forgetting reusable IDs.

The registry contains no database, socket, ECS, or Redis I/O and is not a
distributed ownership authority.

## Legacy `MapInstance` bridge

[MapInstance.Identity.cs](../src/Godswar.Server/Game/MapInstance.Identity.cs)
adds `RealmId`, `WorldInstanceId`, typed `ContentMapId`, and `InstanceKind`
to each `MapInstance`.

The existing byte constructor remains compatible: it creates a Tempest
open-world identity and converts its byte through `MapId.FromLegacy`.
The typed constructor rejects content map IDs above `255` while the original
client protocol and existing runtime still require a byte.

This slice deliberately does **not** claim duplicated live dungeon routing:

- `GameSessionRegistry` still stores actual mutable map runtimes in
  `ConcurrentDictionary<byte, MapInstance>`;
- existing movement, portals, monster ownership, AOI, and broadcasts still
  route through the legacy byte `MapId`;
- `LocalWorldInstancePlacementRegistry` is tested as an application boundary
  but is not yet composed into `GameSessionRegistry` or `Program`;
- creating two typed `MapInstance` objects with one content map proves
  identity separation only, not two playable live instances.

That integration is B18B.

## Verification

| Gate | Result |
| --- | --- |
| Release solution build | Passed; 0 warnings, 0 errors |
| Managed protocol/architecture checks | 268 passed |
| B03 mandatory PostgreSQL gate | 43 required checks passed; no skips |
| B03 migration scenarios | 5/5 passed |
| B03 cleanup | Passed; disposable databases and temporary container files cleaned |
| Live Compose PostgreSQL | Healthy |
| Live Compose server | Healthy |

The five B03 scenarios were:

1. empty bootstrap to all 36 migrations;
2. character-lifecycle prefix-030 preflight;
3. Tempest-realm prefix-034 authority migration, including fail-closed
   identity/non-Tempest fixtures;
4. restored prefix-008 upgrade; and
5. current-schema idempotence.

Focused coverage is provided by:

- [WorldInstancePlacementChecks.cs](../tests/Godswar.Server.ProtocolChecks/WorldInstancePlacementChecks.cs)
- [WorldInstanceMapIdentityChecks.cs](../tests/Godswar.Server.ProtocolChecks/WorldInstanceMapIdentityChecks.cs)
- [PostgresRealmMigrationChecks.cs](../tests/Godswar.Server.ProtocolChecks/PostgresRealmMigrationChecks.cs)
- [PostgresRealmMigrationIntegrationChecks.cs](../tests/Godswar.Server.ProtocolChecks/PostgresRealmMigrationIntegrationChecks.cs)

## Explicit non-claims

B18A does not implement:

- a second server process, gateway/worker backhaul, remote placement, or
  cross-process transfer;
- Redis packages, configuration, containers, adapters, leases, or routes;
- cross-realm login, character selection, Pindus settlement, battlefield
  schedules, dungeon admission, matchmaking, or reward handling;
- instance-aware `GameSessionRegistry` routing or single-owner map
  mailboxes;
- client support for map IDs greater than `255`.

B17 is approved for future Redis coordination by ADR 0004, but Redis remains
undeployed until the first runnable two-process boundary.

## Rollback and next milestone

The realm migration is additive and must remain applied during an
application rollback. The migration runner rejects a database whose applied
history is ahead of the binary catalog, so the plain pre-B18A image cannot
start against schema 035. Roll back with a compatibility build that retains
the immutable 035 catalog entry, or restore a matched pre-035 backup only
through the documented destructive-recovery procedure. Do not remove the
check constraint until the realm-aware lifecycle replacement is ready.

The next milestone is **B18B: instance-aware routing and the single-owner
mailbox boundary**. It must integrate the local placement contract into
`GameSessionRegistry`, route by `WorldInstanceId`, retain the legacy
client-map bridge, serialize mutable map/ECS commands through one bounded
owner, and keep socket/database work off that owner loop. Only after a
runnable second process exists should B17 add Redis-backed coordination.
