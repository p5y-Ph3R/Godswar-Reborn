# 1. Executive summary

The server is an **Existing** .NET 10 modular monolith. `src/Godswar.Server/Program.cs` manually composes login, game, ECS/world, networking, security, and storage objects in one process. The custom ECS kernel under `src/Godswar.Server/Ecs` is real and generation-safe. Monster simulation uses a per-map `EcsMonsterMapRuntime`; player ECS authority is further along for movement, combat, recovery, status, and map membership, but durable progression and inventory remain boundary operations coordinated through mutable `GameCharacter`, `GameSessionRegistry`, and direct store calls.

Persistence is **Existing but inconsistent by runtime profile**:

- Docker selects `PostgresGameStore`; it uses Npgsql, explicit SQL, row locks, transactions, normalized `character_items`, and a strong forward-only migration runner.
- `appsettings.json` still defaults to `JsonGameStore`. That store rewrites one whole `data/state.json` file, is process-local, and has material parity gaps, including pets and captured world state.
- `IGameStore` combines authentication, account presence, characters, inventory, equipment, progression, crafting, pets, skills, talents, NPCs, monsters, and packet-capture reads. Network handlers and the session registry call it directly.
- There is no durable command inbox, transactional outbox, general operation idempotency key, player-ownership fence, currency ledger, or background durable retry queue.

Networking is **Partially implemented** as two mutually exclusive profiles:

- The checked-in default is proprietary raw TCP on login/game ports. `ClientSession`, `RawTcpLegacyTransport`, `TcpEndpointServer`, and the packet handlers implement the legacy framing and bounded queues.
- The secure profile implements TLS-protected reliable traffic plus authenticated and encrypted UDP movement through `TlsMuxLegacyTransport`, `SecureUdpRuntime`, `SecureUdpSessionAuthority`, replay windows, NAT-rebinding validation, rate limits, transport epochs, simulation ticks, and TLS fallback. It is enabled only by the secure configuration/Compose overlay; `appsettings.json` keeps both secure networking and UDP disabled.
- Tickets, duplicate-login ownership, presence, UDP binding, and routing are process-local. Zone transfer is local to the same process.

The largest architectural risk is **ambiguous and unenforced authority across mutable session/ECS copies and durable PostgreSQL rows**. The combination of direct `_store` calls from packet handlers, process-local locks/ownership, separate character-load queries, and missing idempotency/outbox boundaries can duplicate or lose valuable operations when TCP retries, UDP/TCP ordering, reconnects, failures, or a future second server process are introduced. A separate P0 security risk is that the legacy raw login path calls `LoginOrCreateAccountAsync`, while the hardened password verifier and one-time ticket path is tied to the secure profile.

## Database recommendation

**Recommendation: PostgreSQL plus Redis for the confirmed target topology,
introduced in phases.** PostgreSQL only remains correct for the current
one-process runtime, including the completed B18A/B local instance-routing
foundation.

PostgreSQL should become the sole authoritative durable store. The existing relational schema, JSONB catalog fields, explicit Npgsql transactions, row locks, and migration checksum policy already cover the repository's demonstrated durable workload. The initial work should remove JSON-store ambiguity, establish application/persistence boundaries, add idempotency/outbox support, and make one server process safe before adding another operational dependency.

Redis is **approved for the future multi-process topology, but is not yet
implemented or deployed**. [ADR 0004](../adr/0004-realm-and-world-instance-topology.md)
confirms Tempest as the first logical realm, future realms, cross-realm
Pindus, scheduled battlefields, and on-demand dungeon instances. Those
requirements need cross-process tickets, placement, routing, presence, and
leases once a second gateway/worker process becomes runnable. Until then, the
existing bounded in-memory structures remain the correct disposable runtime
state.

**MongoDB should not be introduced during the initial migration.** No concrete independent document workload was found. Flexible item, NPC, map, monster, skill, and audit payloads already fit PostgreSQL relational tables plus JSONB. Packet-capture research data needs retention and archival policy, not a third operational database. MongoDB should be reconsidered only if a measured content-authoring or player-generated-document workload fails the criteria in section 8.

The migration should be incremental and expand/contract based:

1. Create a coherent, reproducible release from the currently applied migration set.
2. Document ownership and invariants, then split the broad store behind feature use cases without changing behavior.
3. Make PostgreSQL tests mandatory and PostgreSQL the only production authority.
4. Add aggregate versions, command inboxes, outboxes, audits, and reconciliation for valuable state.
5. Integrate the TCP/UDP command envelope and ownership fence.
6. Use the completed local realm/node/world-instance identities and bounded
   owners to build the first two-process gateway/worker slice, then add Redis
   only when that slice exercises shared coordination.
7. Remove the JSON fallback and legacy persistence paths after verified parity and a rollback window.

Benefits are a single owner for every field, commit-before-ack guarantees for player value, safe retries and reconnects, non-blocking ECS ticks, independently testable transport/application/storage layers, reversible migrations, and a clear path to multiple server instances without unsafe dual writes.
