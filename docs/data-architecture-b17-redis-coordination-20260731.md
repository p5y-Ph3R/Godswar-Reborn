# B17 disposable Redis coordination

Status: completed and verified behind an explicit opt-in provider; production
deployment and remote scale-out remain gated

## Outcome

B17 adds one bounded, asynchronous cross-process coordination layer without
moving durable player value out of PostgreSQL:

```text
unchanged client
  -> semantic gateway
       -> Redis: login generation + single-use admission
       -> exact mTLS worker route
  -> authoritative worker
       -> PostgreSQL: durable owner UUID/generation
       -> Redis: worker route + PG-fenced player presence lease
       -> ECS/gameplay
       -> PostgreSQL: every valuable mutation
```

The checked-in provider default is `Local`. Redis is selected only through
`Coordination.Provider=Redis` or
`GODSWAR_COORDINATION_PROVIDER=Redis`. Local mode constructs no Redis client
and retains the bounded in-process ticket/gateway authorities plus existing
process-local world and session ownership.

This is an implementation and local/CI-operability milestone. It is not a
claim that a managed Redis service is deployed, highly available, sized for
300 players, regionally redundant, or ready for cross-realm Pindus.

## Authority and dependency boundary

PostgreSQL remains authoritative for accounts, characters, inventory,
equipment, currency, progression, rewards, pets, mounts, command
inbox/outbox, audits, checkpoints, and the monotonic player-owner fence.
Redis owns only disposable coordination state.

`GameClientHandler.EnsureCheckpointOwnershipAsync` establishes authority in
this order:

1. acquire the PostgreSQL checkpoint owner UUID/generation;
2. refresh the durable character snapshot;
3. install a Redis player lease carrying that exact durable fence;
4. bind the process-local session ownership.

Cleanup releases the disposable player lease before the durable checkpoint
owner. A Redis lease never grants permission to bypass
`PostgresPlayerOwnershipGuard`; every valuable PostgreSQL transaction still
locks and validates the durable fence for its full mutation.

There is no PostgreSQL/Redis dual write or distributed transaction.
Redis disappearance cannot roll back a durable generation, duplicate an
item, or become evidence that a player is unowned.

## Implemented contracts and adapters

| Boundary | Implementation |
| --- | --- |
| Runtime options | `CoordinationRuntimeOptions`, `ServerOptions.Coordination.cs` |
| Process composition | `ServerCoordinationComposition` |
| Ticket contract | `Application/Sessions/IGameTicketStore.cs` |
| Ticket adapters | `InMemoryGameTicketStore*`, `Infrastructure/Redis/RedisGameTicketStore*` |
| Semantic contract | `Application/Gateway/ISemanticGatewayCoordination.cs` |
| Semantic adapters | `InMemorySemanticGatewayCoordination`, `RedisSemanticGatewayCoordination*` |
| Worker/player contract | `Application/Coordination/WorkerCoordinationContracts.cs` |
| Worker adapters | `InMemoryWorkerCoordination*`, `RedisWorkerCoordination*` |
| Worker lifecycle | `WorkerCoordinationRuntime*` |
| Player lifecycle | `WorkerCoordinationRuntime.PlayerLease.cs`, `GameClientHandler.CharacterCheckpoints.cs` |
| Redis execution | `RedisCoordinationExecutor`, `RedisCoordinationException`, `RedisCoordinationMetrics` |
| Keys/scripts | `RedisCoordinationKeyBuilder`, `RedisCoordinationScripts`, `RedisGameTicketScripts`, `RedisSemanticGatewayScripts*` |
| Readiness/operations | `ServerOperationalState`, `ServerReadinessMonitor`, `OperationalStateMetrics`, `CriticalTaskSupervisor` |

Networking, game, ECS, and application code do not reference
`StackExchange.Redis`. The driver and Lua scripts stay under
`Infrastructure/Redis`. All external coordination contracts are async,
cancellable, and carry an absolute bounded deadline. No map/ECS tick waits
on Redis I/O.

## Coordination semantics

### Secure tickets

`RedisGameTicketStore` implements:

- atomic login-generation replacement;
- bounded issue with a cryptographically random opaque ticket;
- ticket-digest-only storage;
- exact account, grant, audience, target, protocol, permission, and expiry
  validation;
- one successful consumer under concurrency;
- grant activation/revocation; and
- bounded generation/outstanding-ticket registries.

A logical ticket lasts 60 seconds by default. A consumed, expired, revoked,
wrong-scope, or unknown ticket fails closed. Redis loss requires a fresh
authenticated login; it does not fall back to an in-process ticket store.
Issuance, activation, consumption, and expiry use Redis `TIME` inside their
atomic scripts, so disagreement between gateway clocks cannot extend or
prematurely expire shared ticket authority.

### Semantic gateway

`RedisSemanticGatewayCoordination` preserves the B18C2 lifecycle:

```text
start login -> activate exact generation
            -> reserve exact route/admission
            -> commit -> refresh/resolve -> release
                         or rollback/expiry
```

Replacement, activation, reservation, commit, refresh, rollback, release,
and expiry accounting are atomic scripts. Limits and TTLs remain those
validated by `SemanticGatewayAuthorityLimits`. Route admission is rechecked
against the current exact worker lease rather than inferred by map ID.
Admission refresh renews both the lease and its capacity-counter/expiry-index
state, and the expiry sweeper is supervised as a critical gateway task.

### Workers, routes, and player presence

`WorkerCoordinationRuntime` generates one boot ID per process incarnation,
registers exact static routes, renews readiness, marks drain state, and
releases best-effort at shutdown. If Redis restarts empty, a still-running
process re-registers with the same boot ID; only a restarted process
generates a new boot ID.

Worker registration rejects a different live boot ID for the same node.
Route lookup validates the exact realm/map/world-instance/node tuple and the
current worker lease. A player lease carries:

- account and character identity;
- opaque lease token;
- exact worker node and boot ID;
- exact world route and presence state;
- PostgreSQL owner UUID/generation; and
- bounded issue/proven-until times.

Renew, release, and replacement compare the complete expected authority.
Install and renewal validate the player, exact route, and exact worker
incarnation in one Lua transition, so a drain, expiry, or boot change cannot
race between route lookup and lease mutation. Periodic renewal selects the
latest route and presence only after acquiring the per-player operation
gate, preventing an older heartbeat from reverting a portal transition.
Redis `TIME` supplies every shared worker, route, player, login, and
admission expiry. Local readiness uses monotonic elapsed time from the last
successful proof; process wall-clock offsets are telemetry only.
Loss/conflict/expiry invokes the session ownership-lost callback and stops
that session instead of permitting uncertain valuable commands.

The authenticated worker-backhaul pre-commit guard also uses a
receipt-relative monotonic lifetime, shortened by the configured admission
lifetime safety margin. Replay cleanup and the bounded replacement retry are
monotonic as well. Redis admission commit is still required before the
gateway forwards the first client byte, so this local guard is never promoted
to shared authority.

For an mTLS gateway session, the initial character route must equal the
admitted realm/map/world instance and target node. A later
server-authorized portal transition may move to another statically
configured route owned by that same worker. An unowned or cross-worker
destination remains fail-closed until a live worker-handoff protocol exists.

## Keys, privacy, and deployment shape

All keys use:

```text
godswar:<environment>:v1:<family>:<bounded-suffix>
```

| Family | Purpose |
| --- | --- |
| `server` | worker boot/readiness/drain lease |
| `route` | exact world-instance route lease |
| `player` | PG-fenced player presence lease |
| `ticket` | hashed consume-once ticket |
| `ticket-grant` | hashed secure grant state |
| `ticket-generations` | bounded ticket-generation registry |
| `outstanding-tickets` | bounded outstanding-ticket registry |
| `login-account` | active semantic login by hashed account ID |
| `login-name` | semantic login lookup by hashed canonical name |
| `login-connection` | login connection cleanup index |
| `admission` | semantic route admission |
| `gateway-counters` | bounded gateway capacity counters |
| `gateway-expiry` | bounded expiry index |

Most identity-bearing suffixes are 128-bit truncated, domain-separated
SHA-256 values encoded as uppercase hex. A ticket key uses the first 128 bits
of the already-SHA-256 ticket digest. Raw usernames, IPs, ticket material,
account/character IDs, node IDs, and world-instance IDs are not key names.
The two fixed gateway state keys contain no player identity.

The Lua protocols touch multiple dynamically chosen keys. B17 therefore
supports one Redis primary keyspace. Redis Cluster hash-slot sharding is not
supported. The current rollout must not automatically promote an
asynchronous replica because it could resurrect a consumed ticket or a
superseded lease. Public HA requires an approved zero-data-loss policy or a
failover epoch that invalidates pre-promotion coordination state.
`noeviction` is required.

## Bounds, deadlines, and failure policy

Initial defaults are:

| Bound | Default |
| --- | ---: |
| Process coordination capacity | 4,096 |
| Concurrent Redis operations | 128 |
| Queue admission | 25 ms |
| Logical operation deadline | 250 ms |
| Connect timeout | 1,000 ms |
| Circuit | 5 failures; open 5 seconds |
| Worker heartbeat / TTL | 5 / 20 seconds |
| Player renewal / TTL | 10 / 30 seconds |

`RedisCoordinationExecutor` owns one process-wide
`ConnectionMultiplexer`. A semaphore bounds concurrent logical operations;
queue admission is finite; operation deadlines are explicit; and a small
circuit rejects new uncertain work during repeated failure. A timed-out
driver operation retains its permit until its underlying task finishes, so
timeouts cannot create unbounded hidden concurrency.

Redis provider startup fails closed when the connection string is missing,
the initial connection fails, PostgreSQL durable ownership is not selected,
or Production is configured without Redis TLS. During runtime:

- missing/slow Redis removes coordination readiness;
- worker coordination is a supervised critical task;
- new login/admission/route/lease work is rejected;
- semantic-gateway expiry cleanup failure stops the host rather than
  silently abandoning capacity accounting;
- an unproven player lease disconnects the affected session;
- routes are never guessed and owners are never inferred from a missing key;
  and
- PostgreSQL player value and owner generations remain intact.

Rollback is coordinated: stop admission, drain to one known authoritative
gateway/worker, verify PostgreSQL fences and durable queues, switch the new
process set to `Local`, and require fresh login. Per-request fallback or
mixed Local/Redis authorities for one realm are forbidden.

## Configuration and local operations

The disposable local/CI dependency is:

- `docker-compose.redis-coordination.yml`;
- `ops/redis/redis-coordination.local.conf`;
- `.env.redis-coordination.example`;
- `tools/NewB17RedisLocalConfiguration.ps1`;
- `tools/InvokeB17RedisCiGate.ps1`;
- `tools/InvokeB17RedisCiGate.Docker.ps1`;
- `tools/InvokeB17RedisCiGate.Acl.ps1`; and
- `.github/workflows/phase5a-network-gate.yml`.

The generator creates ignored random ACL material, never prints the password
or connection string, restricts generated file access to the current user,
and refuses accidental overwrite. The container is loopback-only,
read-only, ACL-protected, non-persistent, `noeviction`, memory/PID/CPU
bounded, and has no restart policy or durable volume.

The application identity is restricted to the exact environment key prefix
and an explicit command allowlist. It can execute the reviewed Lua workflows,
Redis `TIME`, and scoped `GET`/`SET` operations for realm-content admission,
but it cannot run administrative, unapproved bulk-string, key-discovery,
publish, or destructive script commands. The single-primary
client configuration disables automatic `CONFIG` and `CLUSTER` probes and
the tie-breaker key rather than widening that ACL. The CI harness creates a
different disposable administrative identity only for configuration
inspection, bounded fault injection, denial-log inspection, and cleanup.

Plaintext is permitted only for this loopback LocalDevelopment dependency.
Production requires a private TLS endpoint, approved secret injection,
least-privilege ACL, one primary keyspace, provider capacity/latency
evidence, and an explicit rollout approval.

See:

- [ADR 0005](adr/0005-b17-redis-coordination-activation.md);
- [B17 outage/recovery/rollback runbooks](operations/b17-redis-coordination-runbooks.md);
- [AWS alpha sizing](../AWS_ALPHA_EC2_SIZING.md); and
- [historical B16 defer evidence](data-architecture-b16-b17-redis-decision-20260731.md).

## Observability

The meter `Godswar.Server.Infrastructure.RedisCoordination` exports:

| Instrument | Finite labels |
| --- | --- |
| `godswar.coordination.operations` | `coordination.family`, `coordination.outcome` |
| `godswar.coordination.duration` | `coordination.family`, `coordination.outcome` |
| `godswar.coordination.logical_results` | `coordination.family`, `coordination.result` |

Families are exactly `health`, `worker`, `route`, `player`, `ticket`, and
`admission`. The operation and duration instruments describe bounded Redis
execution, so a Lua script that executes successfully is `success` even when
its application result rejects a conflict. Their outcomes are exactly
`success`, `timeout`, `unavailable`, `overloaded`, `circuit_open`, and
`cancelled`.

The separate logical-result counter prevents transport success from hiding a
worker, route, or player coordination decision. Its result values are exactly
`applied`, `current`, `not_found`, and `conflict`; transport failures are not
recorded again in this counter. Ticket and semantic-admission lifecycle
counts remain in their bounded authority snapshots rather than being
misreported as worker lease results.

The worker readiness source also exposes finite process snapshots for
configured capacity/concurrency, in-flight operations, locally observed
routes/player leases, accepted/conflicting/timed-out/unavailable operations,
overload/circuit rejects, and last success. These snapshots are not global
Redis key-cardinality or provider-capacity metrics. No account, character,
username, endpoint, node, ticket, lease, or Redis error text is a metric
label.

## Verification evidence

The B17-specific evidence completed on 2026-07-31 includes:

- mandatory disposable Redis gate: **26/26 checks across 5 scenarios
  passed** in 32.432 seconds, including its Release build at **0 warnings
  and 0 errors** and verified container/secret cleanup;
- the gate disabled the default Redis user, authenticated application and
  disposable-admin identities separately, allowed the application only in
  the four test key patterns, and observed no unexpected ACL denial during
  the ticket, worker/route/player, or semantic-gateway workflows;
- application probes proved that out-of-scope keys, `FLUSHDB`, `CONFIG`,
  `CLIENT PAUSE`, `KEYS`, `SET`, `SCRIPT FLUSH`, `ACL`, and `PUBLISH` are
  denied. `CLIENT PAUSE`, configuration inspection, denial-log inspection,
  restart, and cleanup remained isolated administrative test actions;
- the restart scenario first seeded disposable application state, then
  proved that the non-persistent restart lost it, rejected unauthenticated
  access, required fresh application authentication, and recovered all
  coordination workflows. It does **not** claim live-ticket continuity
  across a state-losing restart;
- the paused dependency failed closed, and the post-flush recovery scenario
  rebuilt worker/route/player state using the same live-process boot ID and
  exact PostgreSQL fence/lease token;
- a real Redis 7.4.10 loopback ticket authority check: **1/1 passed**,
  followed by verified empty-database cleanup;
- a two-executor Redis semantic-gateway authority check: **1/1 passed**,
  including one-winner reservation, cross-gateway commit/resolve,
  replacement invalidation, drain/boot fencing, refresh, release, and
  opposite **+365/-365 day** process clocks. Redis expiry preserved live
  capacity, returned distinct refreshed admission/login expiries, reclaimed
  genuinely expired state, and made capacity reusable;
- worker/player runtime checks prove conservative monotonic readiness and
  player leases under a +365-day wall-clock offset, fail closed at their TTL
  boundary, and invoke ownership loss exactly once when a definitively
  missing lease cannot be restored;
- authenticated backhaul checks prove receipt-relative monotonic admission
  and replay expiry under +365/-365-day timestamps, including the minimum
  one-second admission lifetime. The real-socket replacement retry remains
  bounded and no longer compares Redis time with a process wall clock;
- existing B18C2 semantic checks: **2/2 passed**, with a Release protocol
  check build at **0 warnings and 0 errors**;
- generated local ACL/configuration validation and Compose interpolation:
  passed;
- the checked-in Compose dependency reached healthy and returned `PONG`;
- generator overwrite refusal and explicit credential rotation were
  exercised without printing secret material.

The repository test catalog contains the B17 architecture ratchet, bounded
worker/player runtime lifecycle checks, async semantic-gateway parity checks,
and live Redis ticket forgery/scope/replacement/concurrency/revocation/
expiry/capacity coverage. The live check requires
`GODSWAR_TEST_REDIS_CONNECTION_STRING`; absence is a local skip, not an
acceptable CI pass.

Final repository-wide closeout also passed:

- Release solution build: **0 warnings and 0 errors**;
- complete managed protocol catalog: **286 passed, 0 failed**;
- mandatory disposable PostgreSQL gate: **43 checks across 5 migration
  scenarios passed** in 439.071 seconds, with cleanup complete and no
  disposable `godswar_b03_*` databases left behind; and
- mandatory disposable Redis gate: **26 checks across 5 scenarios passed**
  in 32.432 seconds, with credentials and containers cleaned up.

The existing `godswar-server` and `godswar-postgres` containers remained
healthy and were not replaced, restarted, or reconfigured by the B17 gate.

## Remaining production gates

B17 does not yet prove:

- a managed Redis provider, TLS certificate/secret rotation workflow, HA
  topology, regional failover, provider RTO/RPO, or monthly cost;
- Redis p95/p99 under the expected alpha login/renewal/admission mix;
- a remote multi-host gateway/worker deployment or failure-isolation drill;
- reconnect or live world-instance transfer between workers;
- automatic instance replacement or live map migration;
- cross-realm Pindus coordination or settlement;
- a global Redis cardinality bound enforced by this process; or
- 300-player capacity.

Public multi-process activation remains blocked until the ADR's staging
latency, outage, provider, security, and rollback gates pass. MongoDB remains
unjustified for this workload.
