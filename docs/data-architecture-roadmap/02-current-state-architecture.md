# 2. Current-state architecture

## 2.1 Solution, projects, and process boundaries

| Status | Project or area | Current responsibility | Repository evidence |
| --- | --- | --- | --- |
| Existing | `GodswarServer.sln` | Main solution containing the server, protocol checks, secure smoke tooling, and Phase 5A tooling | `GodswarServer.sln`; `src`, `tests`, `tools` |
| Existing | `src/Godswar.Server/Godswar.Server.csproj` | .NET 10 executable modular monolith; Npgsql 10.0.2 is its only database package | `Godswar.Server.csproj` |
| Existing | `src/Godswar.Server/Program.cs` | Manual composition root; creates one store, registry, login/game listeners, optional TLS/UDP runtime, and gameplay loops | `Program.cs` |
| Existing | `src/Godswar.Server/Ecs` | Custom entity registry, component pools, system scheduler, command buffer, and event buffer | `EcsWorld`, `EntityRegistry`, `ComponentPool<T>`, `EcsSystemScheduler`, `EcsCommandBuffer`, `EcsEventBuffer` |
| Existing | `src/Godswar.Server/World` | ECS components, systems, map projections, monster runtime, player movement/combat boundaries | `World/Components`, `World/Systems`, `World/Boundaries`, `World/Maps` |
| Existing | `src/Godswar.Server/Game` | Packet dispatch, session/map registry, gameplay orchestration, persistence calls, replication | `LoginClientHandler`, `GameClientHandler`, `GameSessionRegistry`, `MapInstance` |
| Existing | `src/Godswar.Server/Networking` | Raw TCP, TLS mux, UDP binding/protection, admission, queues, timeouts, metrics | `ClientSession`, `TcpEndpointServer`, `Networking/Secure` |
| Existing | `src/Godswar.Server/State` | Broad storage contract, PostgreSQL/JSON implementations, SQL migrations, content seeds, DTOs | `IGameStore`, `PostgresGameStore`, `JsonGameStore`, `DatabaseMigrations` |
| Partially implemented, separate solution | `client/network-shim/Godswar.NetShim.sln` | The x86 `Net.dll` compatibility artifact/solution exists outside `GodswarServer.sln`; secure end-to-end adoption remains opt-in/partial | `client/network-shim`; `docs/network-infrastructure-goal.md` |
| Existing | `tests/Godswar.Server.ProtocolChecks` | Custom executable test/check harness rather than a standard unit-test framework | `Program.cs`, 227 C# check files, no external test package |
| Existing | `tools/Godswar.Server.Phase5A` | Bounded in-process replay/load/soak baseline with no external target | `docs/network-infrastructure-phase5a-replay-load-observability.md` |
| Existing for local development | Docker | One server container and PostgreSQL 17; secure Compose overlay replaces the raw profile | `Dockerfile`, `docker-compose.yml`, `docker-compose.secure.yml` |
| Missing | Distributed server processes | No separate login, gateway, zone, persistence-worker, or coordinator deployment exists | `Program.cs`, Compose files |
| Missing | Redis and MongoDB | No client package, configuration, adapter, migration, or test exists | Project files and repository search |

## 2.2 ECS organization and authority

`EcsWorld` uses opaque `EntityId` values with generation checks and typed `ComponentPool<T>` instances. Systems are scheduled through `EcsSystemScheduler`; structural changes and results can be buffered. This is a sound custom ECS core, not a database object model.

`EcsMonsterMapRuntime` is an **Existing** authoritative monster implementation with deterministic state, fixed-step simulation, patrol/aggro/combat/lifecycle systems, and network replication outside the system. `MapInstance` consumes `IMonsterMapRuntime`.

`MapEcsShadow` is a historical name. In the default player `Ecs` runtime it is authoritative for map player/NPC membership; in `Legacy` mode it is a parity shadow. Player recovery, status composition, accepted movement, combat intent, damage, and mount movement policies also have ECS systems and boundary adapters. The registry still applies results to the mutable `GameCharacter` while holding synchronization locks and produces packets in legacy order.

There is **no** generic ECS serializer, whole-world database snapshot, automatic dirty-component tracker, event store, or persisted component version registry. Persistence is explicit in handler/registry workflows. This is desirable in principle; the missing piece is a clean application boundary.

Relevant component classifications include:

- Durable projections: `PlayerIdentityComponent`, class/camp fields, `PlayerTransformComponent`, `PlayerVitalsComponent`, `PlayerProgressionComponent`, `PlayerWalletComponent`, zodiac state, and committed equipment appearance.
- Runtime-only: movement intents and sequence state, cooldowns, MP reservations, aggro, monster lifecycle timers, deterministic random state, online timers, recovery clocks, status-source timers, AOI membership, queues, sockets, and connection state.
- Derived: `PlayerCalculatedStatsComponent`, equipment scores/ranks, composed status snapshots, maximum/stat projections calculated from durable equipment/content.
- Replicated only: `PlayerEcsSnapshotAdapter` output, monster/NPC/player packet snapshots, transport sequence/ack state.
- Configuration/content: NPC, map, monster, item, skill, talent, forge, pet, and world-boss definitions hydrated from catalogs/generated seeds.

## 2.3 Persistence and current save/load behavior

`Program.cs` selects `PostgresGameStore` only when `storage.provider` equals `postgres`; every other value falls back to `JsonGameStore`. `appsettings.json` defaults to JSON, while `appsettings.docker.json` and Compose select PostgreSQL.

`PostgresGameStore` owns one `NpgsqlDataSource` and uses handwritten parameterized SQL. Its partial files separate some features physically, but all implement the single `IGameStore`. Important mutations such as character creation, inventory moves, forging, gear enhancement, holy stones, pet operations, talents, and zodiac use explicit transactions and, where needed, `FOR UPDATE`. `SaveCharacterVitalsAsync` uses a monotonic `vitals_revision` plus a process-local per-character semaphore. Character position uses a session-local `CharacterPositionPersistenceCoordinator` and a capacity-one coalescing channel.

`PostgresSchemaMigrationRunner` is a strong **Existing** foundation:

- a PostgreSQL advisory lock serializes runners;
- migration history stores immutable SHA-256 checksums;
- recorded history must be an exact ordered prefix;
- an ahead-of-binary, reordered, gapped, checksum-drifted, or partial legacy schema fails closed;
- each forward migration and its history row commit in one transaction.

However, migrations and content seeding run in application startup. `docker-compose.yml` also mounts `database/postgres` into PostgreSQL's init directory even though `docs/database-migrations.md` declares those files historical bootstrap sources and the embedded runner the sole runtime path. These two bootstrap stories must be reconciled.

The embedded fresh-database path is not currently self-contained. `LegacySchemaBootstrap.001.sql` through `.005.sql` do not create `packet_opcodes` or `packet_transactions`, while registered forward migrations including `20260728_009_skill_cast_interrupt_opcode`, `_013_owned_pet_bootstrap_opcode`, and `_022_pet_level_progression` reference those relations. Those tables currently come from historical filesystem SQL. Therefore a genuinely empty database using only `PostgresSchemaMigrationRunner` can fail before completing the registered plan. Repair the reviewed bootstrap foundation without changing checksums of already-applied forward migrations, then remove the Compose init mount only after both empty and restored paths pass.

`JsonGameStore` uses a path-scoped `SemaphoreSlim`, reads the complete `GameDatabase`, and rewrites the complete file through a temporary file. It has no cross-process lock, durable flush guarantee, versioned migration chain, checksum, or production recovery process. Pet operations, online status, and captured world synchronization already differ from PostgreSQL. It must be treated as a disposable development fixture, not an alternative production authority.

Content authority is also currently split. `EnsureSeedDataAsync` upserts many generated catalogs into PostgreSQL, while `MapTraversalCatalog.Default.CreateDefault()` reads generated `MapTemplateSeeds` directly at runtime and forge/material/stat/pet policies also execute from code catalogs. Other world-sync paths read PostgreSQL and captured packets. A rolling server can therefore calculate from package values while another reader exposes PG values. Each content family needs one pinned release owner.

The legacy `server` table (`id`, name/identifier, IP, server limit) and `character_base.server_id` exist, and character creation currently uses `server_id = 1`; no runtime placement/routing implementation consumes that as a distributed ownership record. It is legacy configuration, not evidence of multi-server support. Likewise, `server_data_migrations` is a legacy one-off repair/import marker distinct from the checksum-enforced `schema_migrations` history and must be inventoried and retired or clearly namespaced.

The account row/object mapping has legacy ambiguity: `GameAccount.CreatedUtc` is populated from `accounts.last_login_time`, so it is not a trustworthy creation timestamp and changes with login behavior. Legacy `uuid`, `email`, IP/MAC-like, and `status` columns are not consistently exposed or enforced by the current account model. The account contract migration must inventory them, define privacy/retention semantics, add a real immutable `created_at` if needed, and name `last_login_at` honestly.

The character-login path loads base character, calculated stats, skills, talents, and pets in separate calls. It does not currently guarantee one database snapshot across the full bootstrap. Character creation is one transaction, but PostgreSQL permits multiple `character_base` rows per account and there is no character-slot/cardinality or create-operation idempotency constraint. `GetFirstCharacterAsync`/preview uses `ORDER BY id LIMIT 1`; a lost create acknowledgement can therefore produce another character while the client presents only the first. The supported characters-per-account rule requires clarification before adding a slot constraint. Character deletion is a hard cascading delete without tombstone/restore window and its handler does not use the returned boolean to determine success.

Normal runtime saves are mixed:

- Valuable inventory/crafting/pet mutations generally commit synchronously before the response packet.
- Movement is coalesced and checkpointed approximately every two seconds.
- Vitals writes are also awaited directly by multiple combat, recovery, mount/movement, and monster-attack handlers. This is stronger than a purely periodic checkpoint but couples database latency to handler/background-loop progress. EXP-boost online time and zodiac use periodic/best-effort paths.
- `PostgresGameStore.SaveCharacterPositionAsync` currently returns no applied/conflict/not-found result and does not surface a zero-row update. A map transition can therefore treat a missing/wrong account-character row as a successful destination save. The target conditional checkpoint must require exactly one affected row with the expected revision/fence before transfer continues.
- Disconnect attempts final saves and marks the account offline, but a process crash skips that path.
- `accounts.login_status`, `GameSessionRegistry` ownership dictionaries, and local tickets can disagree after a crash.

## 2.4 Current networking, command dispatch, and replication

Raw legacy traffic uses `TcpEndpointServer` and `ClientSession`. The legacy protocol is length-prefixed, little-endian, and protected by the original rolling cipher rather than TLS. `ClientSession`, `BoundedByteQueue`, and `BoundedReliableEgress` enforce bounded ingress/egress, partial-frame handling, timeouts, and backpressure.

`LoginClientHandler` and the partial `GameClientHandler` decode opcodes and dispatch large switches/method calls. The secure login path uses `AccountAuthenticationService`, a bounded `PasswordKdfScheduler`, TLS, generation-aware one-time game tickets, and a secure game principal. The raw login path calls `IGameStore.LoginOrCreateAccountAsync`; the raw game login can associate by username. That compatibility path must not be exposed as the target authentication architecture.

The optional secure UDP path uses:

- short-lived TCP-issued binding offers/tickets;
- stateless address cookies before meaningful allocation;
- traffic-key derivation and authenticated encryption;
- sequence rules and bounded replay windows;
- endpoint validation and authenticated NAT rebinding;
- per-global/prefix/session rate limiting;
- a 1,200-byte datagram limit;
- transport epoch, input ID, world generation, simulation tick, and snapshot sequence reconciliation;
- TLS fallback when UDP is unavailable.

At present, only authoritative real-time movement is deliberately moved to UDP. Inventory, currency, character lifecycle, chat/control, and other reliable operations remain TCP/TLS. TCP and UDP have no shared ordering.

The current real-time payload still carries an absolute client position sample rather than low-level directional/input intent. `AuthoritativePlayerMovementSystem` validates and sequences the accepted sample, but this remains **Partially implemented authority** and must not be described as the client sending only intent until server-side locomotion can derive position from input and collision/world rules.

`GameClientHandler.RunAsync` serializes legacy packet handling through an instance/per-session handler gate. It does not serialize every command for the same durable character across duplicate or replaced sessions. `GameSessionRegistry`, `MapInstance`, and ECS adapters update world state and broadcast packets. The intended map model is single-owner/single-writer, but sockets, handlers, mutable characters, registries, and several background loops still coordinate through locks and concurrent dictionaries rather than one character/map command mailbox.

Local map transfer persists the destination first, then transfers registry/ECS membership and compensates on failure. World-ready state and secure world generation suppress stale traffic. There is no inter-process zone handoff, sticky routing protocol, or player ownership lease.

Reconnect is **Partially implemented** only as a fresh login/game bind and character reload. `GameSessionRegistry.ReplaceAccountSession` replaces the prior session inside one process. No reconnect/resume state machine, reconnect window, transient gameplay-state preservation, or different-process resume exists; secure tickets and transport epochs protect association but do not provide session recovery.

## 2.5 Current data flow

```text
Untrusted original client
        |
        | raw legacy TCP (default)
        | OR Net.dll shim -> TLS reliable + authenticated UDP movement
        v
TcpEndpointServer / ClientSession          SecureUdpRuntime
        |                                      |
        +---------- bounded decoded packets ---+
                               |
                               v
LoginClientHandler / GameClientHandler opcode dispatch
                               |
             +-----------------+------------------+
             |                                    |
             v                                    v
GameSessionRegistry / MapInstance       direct IGameStore calls
             |                                    |
             v                                    v
MapEcsShadow / ECS adapters              PostgresGameStore
/ EcsMonsterMapRuntime                    OR JsonGameStore
             |
             v
PacketBuilder -> bounded session egress -> client
```

The principal coupling is the second branch: transport handlers both interpret untrusted packets and orchestrate transactions through the very broad `IGameStore`. ECS systems themselves do not hold database clients, but their boundary adapters and registry are not yet separated from persistence/application use cases.

## 2.6 Concurrency, jobs, errors, and observability

- `GameSessionRegistry` uses `ConcurrentDictionary` collections and locks for account sessions, maps, boosts, player revisions, and runtime adapters. These are process-local.
- Each map is intended to become a single-writer ECS shard, but not every player command enters through a bounded map mailbox yet.
- Position writes use a bounded coalescing channel. Reliable network queues and the password KDF scheduler are bounded.
- Background monster, recovery, EXP reconciliation, and zodiac loops are created in `Program.cs`. Endpoint faults trigger shutdown, but gameplay loop faults are not equivalently supervised; a failed critical loop can leave listeners alive.
- Some periodic loops await per-player network/database work serially, causing O(players) head-of-line drift.
- `_vitalsPersistenceLocks` is a `ConcurrentDictionary<int, SemaphoreSlim>` without removal and grows with distinct character IDs.
- Errors are mostly logged with `Console.WriteLine`; there is no structured logging abstraction, distributed trace, durable retry queue, or dead-letter workflow.
- Metrics classes exist for network, secure network, UDP, authentication, simulation loops, and operational state, but there is no production exporter or private metrics endpoint.

## 2.7 Existing tests and feature completeness

The custom protocol-check executable has broad deterministic ECS/network coverage, decoder mutation/fuzz cases, concurrency checks, PostgreSQL race checks, migration checks, replay, impairment, and a bounded local load tool. PostgreSQL integration checks skip when `GODSWAR_TEST_POSTGRES_CONNECTION_STRING` is absent, and the outer executable can still print PASS. The only checked-in CI workflow runs the Phase 5A network subset, not the complete PostgreSQL suite.

| Status | Feature |
| --- | --- |
| Existing | Accounts, characters, inventory/equipment/storage, wallet fields, progression, skills, talents, zodiac, boosts, world-boss faction control, forging/gear mentor/holy stones, mounts represented as items, monsters, maps, NPC/content catalogs |
| Partially implemented | Pets: catalog, ownership, hatch/list/presence/level work exists in the dirty working tree; merge and rebirth remain incomplete/planned |
| Partially implemented | Secure TLS + UDP movement: implemented and locally tested, but optional and disabled in checked-in defaults |
| Partially implemented | Quests: content/reference/capture hints exist; durable per-character quest progress was not found |
| Missing | Real guild aggregate; only legacy `consortia*` character fields and boost terminology were found |
| Missing | Party, trade, mail, auction house, friends/blocks, achievements, housing, purchases/entitlements, matchmaking, leaderboards, durable chat |
| Requires clarification | Expected processes, concurrent players, regions, RPO/RTO, and when horizontal scaling is actually required |
