# 1. Executive summary

The authoritative worker is an **Existing** .NET 10 modular monolith.
`src/Godswar.Server/Program.cs` normally composes login, game, ECS/world,
networking, security, and storage objects in one process. B18C1 also provides
a separate opt-in `--relay-gateway <configPath>` process that copies opaque
raw login/game TCP to one private combined worker. B18C2 adds the separate
`--semantic-gateway <serverOptionsPath> <gatewayConfigPath>` composition:
the unchanged client connects only to a loopback legacy edge, authentication
and single-use admission stay at the gateway, and the authoritative worker
accepts an exact routed session over a mutually authenticated TLS backhaul.
The custom ECS kernel under
`src/Godswar.Server/Ecs` is real and generation-safe. Monster simulation uses
a per-map `EcsMonsterMapRuntime`; player ECS authority is further along for
movement, combat, recovery, status, and map membership, but durable
progression and inventory remain boundary operations coordinated through
mutable `GameCharacter`, `GameSessionRegistry`, and direct store calls.

Persistence is **PostgreSQL-only in every server runtime profile**:

- Focused Npgsql adapters and the retained `PostgresGameStore` compatibility facade use explicit SQL, row locks, transactions, normalized `character_items`, and the checksum-enforced migration runner.
- Historical JSON authority is quarantined under protocol-test compatibility fixtures and cannot be selected by production composition.
- Durable inbox/outbox, audit, operation identity, reconciliation, and player-ownership fencing exist for the implemented command families. Remaining broad facade calls are finite, instrumented compatibility debt pending B20H rather than a second authority.
- World, gameplay, item, and pet content load from immutable PostgreSQL publications pinned for the process lifetime; generated and capture data are publisher/research inputs only.

Networking is **Partially implemented** as two mutually exclusive profiles:

- The checked-in default is proprietary raw TCP on login/game ports. `ClientSession`, `RawTcpLegacyTransport`, `TcpEndpointServer`, and the packet handlers implement the legacy framing and bounded queues.
- The secure profile implements TLS-protected reliable traffic plus authenticated and encrypted UDP movement through `TlsMuxLegacyTransport`, `SecureUdpRuntime`, `SecureUdpSessionAuthority`, replay windows, NAT-rebinding validation, rate limits, transport epochs, simulation ticks, and TLS fallback. It is enabled only by the secure configuration/Compose overlay; `appsettings.json` keeps both secure networking and UDP disabled.
- The default keeps tickets, presence, and routing process-local. B17 can
  explicitly move tickets/admissions, worker routes, presence, and
  PG-fenced player leases to Redis; UDP binding and zone transfer remain
  local.
- B18C1's bounded raw TCP relay is a local-development topology proof. It
  does not terminate TLS/authentication, interpret packets, route by
  `WorldInstanceId`, share sessions/tickets, relay secure UDP, preserve
  source IP, or coordinate workers.
- B18C2's semantic gateway is completed and verified as a local-first
  unchanged-client boundary. It authenticates locally, binds a principal
  without relying on IP alone, and routes exact
  `RealmId`/`MapId`/`WorldInstanceId`/`ServerNodeId` admissions over TLS 1.3
  with mutual leaf pinning. A game admission is single-use for its complete
  login generation, so reconnect requires a fresh full login.

The largest remaining B20 risk is **premature compatibility deletion**. B20E-G establish one runtime authority, but the finite broad facade calls and rollback schema remain while B20H has only a local evidence gate. No real all-replica seven-day deployed zero-use window authorizes their deletion yet. New valuable command families must still adopt the existing idempotency, ownership-fence, and commit-before-ack boundaries explicitly.

## Database recommendation

**Recommendation: PostgreSQL plus Redis for the confirmed target topology,
introduced in phases.** PostgreSQL only remains correct for the current
local-first deployment, including the completed B18A/B instance-routing
foundation, B18C1 relay, and B18C2 in-memory semantic authority.

PostgreSQL is now the sole authoritative durable runtime store. The relational schema, JSONB catalog fields, explicit Npgsql transactions, row locks, immutable content publications, and migration checksum policy cover the repository's demonstrated durable workload. Redis remains optional disposable coordination rather than player-value authority.

Redis is **implemented as an opt-in cross-process coordination provider but
is not deployed as production infrastructure**. ADR 0004 confirms the future
topology; [ADR 0005](../adr/0005-b17-redis-coordination-activation.md)
limits Redis to atomic tickets/admissions, worker routes, presence, and
PG-fenced leases. `Local` remains the default. No managed HA, remote
production placement, cross-worker live transfer, provider SLA, or
production capacity is claimed.

**MongoDB should not be introduced during the initial migration.** No concrete independent document workload was found. Flexible item, NPC, map, monster, skill, and audit payloads already fit PostgreSQL relational tables plus JSONB. Packet-capture research data needs retention and archival policy, not a third operational database. MongoDB should be reconsidered only if a measured content-authoring or player-generated-document workload fails the criteria in section 8.

The migration remains incremental and expand/contract based. B01A through B19
and B20A-G are implemented as repository/local foundations. B20H must now run
the approved all-replica seven-day observation and recovery gates before a
separate forward-only change removes the remaining compatibility facade and
schema. Redis stage qualification remains independent and cannot own player
value.

Benefits are a single owner for every field, commit-before-ack guarantees for player value, safe retries and reconnects, non-blocking ECS ticks, independently testable transport/application/storage layers, reversible migrations, and a clear path to multiple server instances without unsafe dual writes.
