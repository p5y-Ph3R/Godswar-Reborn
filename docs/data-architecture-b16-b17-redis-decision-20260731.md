# B16 Redis decision and B17 conditional evaluation

Status: B16 completed; B17 evaluated and closed as conditional-not-activated
on 2026-07-31.

## Outcome

[ADR 0003](adr/0003-defer-redis-coordination.md) accepts the evidence-backed
decision to defer Redis. B17 was assessed in the same batch because its only
valid entry condition is an approved B16 decision. That condition was not
met, so B17 did not add a Redis client, adapter, container, configuration,
keyspace, health dependency, or deployment.

This is a completed decision gate, not a claim that cross-instance
coordination exists.

## Current evidence

| Evidence | Repository location | Consequence |
| --- | --- | --- |
| One modular-monolith process constructs login, game, registry, ticket, and optional UDP runtime | `src/Godswar.Server/Program.cs` | Current session/ticket consumers share memory |
| Exactly one coherent login/game listener pair is required | `Program.cs` | No independent login/game deployment exists |
| Compose has only one server and PostgreSQL | `docker-compose.yml` | No Redis, gateway, zone, placement, or coordinator service exists |
| Account-session replacement and acquisition gates are process-local | `Game/GameSessionRegistry.AccountSessions.cs` | They do not provide cross-process routing |
| Secure tickets are bounded in memory | `Networking/Secure/IGameTicketStore.cs`; `InMemoryGameTicketStore.cs` | 60-second TTL and 1,024 capacity already cover the configured one-process boundary |
| Process admission is bounded | `Networking/NetworkRuntimeOptions.cs`; `appsettings.json` | Current maximum is 512 active connections |
| Metrics expose outstanding tickets and capacity | `Operations/OperationalStateMetrics.cs` | No checked-in saturation or latency evidence justifies another store |
| Reconnect is a fresh local login/game bind | `docs/data-architecture-roadmap/02-current-state-architecture.md` | No different-process resume contract exists |
| PostgreSQL owns the durable player fence | `Application/Characters/PlayerOwnershipFence.cs`; `Infrastructure/Characters/PostgresPlayerOwnershipGuard.cs` | Redis is unnecessary for valuable-write correctness |

The live development inspection also found only the healthy
`godswar-server` and `godswar-postgres` containers. That observation supports
the checked-in topology but is not treated as a production-capacity claim.

## Missing decision inputs

The following are unknown, so no production SLO or capacity guarantee is
invented:

- target concurrent players per process and world;
- peak login and ticket issue/consume rate;
- date and ownership model for a second server process;
- whether reconnect must land on another process and for how long;
- target Redis p95/p99 latency, timeout, availability, and maximum staleness;
- regional topology, provider, memory/throughput tier, and monthly budget;
- recovery objective for lost disposable coordination state;
- whether a dedicated non-evicting coordination deployment is affordable.

The present values—60-second ticket TTL, 1,024 ticket capacity, and 512
active connections—are bounded development defaults, not production sizing.

## Why B17 is not activated

Redis would solve a future cross-process TTL and routing problem, not a
current one. Adding it now would introduce another network hop and failure
mode while leaving the server in the same one-process topology.

It would also tempt an unsafe implementation. `IGameTicketStore` is
synchronous, while Redis is network I/O. A future B17 must first introduce
asynchronous, deadline-bearing ticket and coordination contracts. It must not
block network handlers or ECS loops through synchronous Redis calls.

PostgreSQL already supplies the monotonic owner fence that protects durable
player value. Redis cannot improve that guarantee and must never replace it.

## Exact reopening trigger

B17 can reopen only when a superseding ADR records:

1. a real multi-process ticket, placement, routing, or reconnect requirement;
2. quantified session/login/reconnect demand;
3. a measured local-memory/PostgreSQL baseline and an approved target it
   cannot meet, or an inherently cross-process TTL requirement;
4. the routing/placement owner and transfer/drain behavior;
5. latency, outage, staleness, recovery, eviction, and cost budgets; and
6. a provider/deployment design with a safe single-process rollback.

## Minimum future implementation

The first approved B17 slice is limited to:

- 60-second atomic consume-once secure tickets;
- 30-second player owner/route leases renewed around every 10 seconds,
  carrying the exact PostgreSQL-issued owner UUID and generation;
- 15-30-second server readiness registrations;
- lease-aligned presence projections; and
- optional 60-120-second reconnect tokens only after reconnect semantics are
  accepted.

It requires an async coordination contract, a bounded key builder, atomic
scripts/functions, strict timeouts, bounded concurrency, circuit breaking,
low-cardinality metrics, and two-process integration tests. Redis
restart/slow/eviction tests must prove:

- no fence reset;
- no stale-owner valuable commit;
- no endpoint takeover;
- new uncertain cross-instance operations fail closed;
- local ECS ticks do not block; and
- draining to the existing single-process/PostgreSQL path remains possible.

## Authority and failure behavior

PostgreSQL remains authoritative for accounts, character lifecycle,
inventory, equipment, mounts, pets, currency, progression, rewards,
entitlements, owner-generation high-water marks, inbox, outbox, audit, and
the completion of every valuable command.

Redis would contain only disposable coordination projections. On Redis
outage, caches bypass to PostgreSQL; future cross-instance bind, acquire,
transfer, or reconnect operations fail closed; missing lease state never
means ownership is free. Established local sessions may continue only while
their PostgreSQL fence and approved lease policy remain valid.

## Changes and verification

This batch changes architecture documentation and repository checks only:

- accepts ADR 0003;
- records B16 as completed with Redis deferred;
- records B17 as evaluated and conditional-not-activated;
- updates the roadmap entry point and sections 7, 17, and 18; and
- registers `DeferredRedisArchitectureChecks`, which rejects a Redis client
  in the server project, a Redis Compose runtime, replacement of the bounded
  in-process ticket composition, or a Redis infrastructure directory unless
  ADR 0003 is deliberately superseded.

No production source, package reference, migration, database, container,
network setting, or live player data changes. Rollback is the documentation
and repository-check revert; there is no runtime state to recover.

Verification on 2026-07-31:

- Release solution build: passed with 0 warnings and 0 errors;
- managed protocol checks: 264 passed, 0 failed, 48 PostgreSQL-gated checks
  skipped without test connection strings;
- deferred-Redis architecture ratchet: passed;
- 66 local Markdown links across the changed decision documents: passed;
- changed source files remain below 20 KB; and
- both existing development containers remained healthy without a rebuild.

The disposable PostgreSQL migration suite was not repeated because this batch
changes no production code, SQL, migration, persistence behavior, or database
state. Its B15 result remains the preceding durable-authority gate.
