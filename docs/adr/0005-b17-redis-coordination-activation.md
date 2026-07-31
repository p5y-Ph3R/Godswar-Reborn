# ADR 0005: Activate B17 Redis coordination in bounded stages

- Status: Completed and verified behind an opt-in provider; production deployment gated
- Date: 2026-07-31
- Decision owner: Godswar server maintainers
- Roadmap ticket: B17
- Depends on: B15, ADR 0004, and completed B18C2
- Supersedes: ADR 0003 only for the B17 defer decision

## Context

ADR 0003 correctly deferred Redis while the server had one authoritative
process. ADR 0004 later confirmed multiple authoritative workers, scheduled
battlefields, repeated dungeon instances, and a future cross-realm Pindus
requirement. B18C2 now provides the semantic gateway and exact authenticated
worker route needed before shared coordination is useful.

PostgreSQL already owns the monotonic player-ownership generation and every
valuable durable mutation. Redis is therefore a coordination dependency, not
a second player database.

The alpha target is one Tempest realm with no more than 300 concurrent
players. That target does not prove the current implementation supports 300
players and does not require a multi-host Redis deployment by itself.

## Decision

B17 implements Redis behind intent-specific asynchronous contracts for:

- consume-once login/game tickets and semantic-gateway admissions;
- worker readiness, boot identity, drain state, and exact route leases;
- player presence and routing leases carrying the PostgreSQL-issued owner
  UUID and generation; and
- later reconnect or scheduled-instance coordination only after those
  protocols are accepted.

The checked-in provider default remains `Local`. Redis activation is explicit
through `Coordination.Provider=Redis` or
`GODSWAR_COORDINATION_PROVIDER=Redis`.

Application, networking, and ECS code depend on coordination contracts.
`StackExchange.Redis`, key construction, Lua, connection management, and
driver exceptions stay under `Infrastructure/Redis`. No ECS/map tick may
perform or await Redis I/O.

## Authority and consistency

PostgreSQL remains the only authoritative owner of:

- accounts, credentials, characters, deletion state, and home realm;
- inventory, equipment, mounts, pets, currency, progression, rewards,
  entitlements, and audits;
- command inbox/outbox results; and
- the monotonic player-owner UUID/generation fence.

A Redis player lease must contain the exact already-issued PostgreSQL fence.
Every valuable PostgreSQL transaction still locks and validates that durable
fence for the entire mutation. Redis loss, eviction, restore, or operator
error cannot reset it.

There is no PostgreSQL/Redis distributed transaction. A durable operation
commits PostgreSQL and its outbox first. Redis state is a lease, cache, or
rebuildable projection. Failure to create the projection cannot make a
valuable operation appear successful through Redis alone.

## Keyspace and atomicity

Keys use the typed, versioned form:

```text
godswar:<environment>:v1:<family>:<opaque-hash>
```

Implemented families are `server`, `route`, `player`, `ticket`,
`ticket-grant`, `ticket-generations`, `outstanding-tickets`,
`login-account`, `login-name`, `login-connection`, `admission`,
`gateway-counters`, and `gateway-expiry`. Registry/counter keys use fixed
identifiers; identity-bearing suffixes are bounded SHA-256-derived tokens.
Raw usernames, IP addresses, tokens, account IDs, character IDs, and
world-instance IDs are not embedded in keys.

Compare/install, compare/renew, compare/release, route publication,
consume-once, and generation replacement use one atomic Redis operation or
Lua script. Redis locks and Redlock are not durable correctness boundaries.
Every authoritative ticket, admission, worker, route, and player-lease
expiry is derived from Redis `TIME` inside the same atomic script that
creates or renews it. Process wall clocks do not decide shared expiry or
capacity. Each process uses monotonic elapsed time to decide whether its
locally observed proof is still usable.

Authenticated worker-backhaul reservation/replay windows and the bounded
replacement retry use receipt-relative monotonic lifetimes. They remain
pre-commit guards: Redis admission commit must succeed before any client
payload reaches a worker. The configured admission lifetime safety margin
shortens, and never extends, that local reservation window.

The current scripts touch multiple dynamically selected keys. The supported
deployment is therefore one Redis primary keyspace. Redis Cluster hash-slot
sharding is not supported by this B17 protocol. Replicas may not be promoted
by the current B17 rollout: asynchronous failover can resurrect a consumed
ticket or a superseded lease. Public HA requires a separately approved
zero-data-loss failover policy or a failover epoch that invalidates all
pre-promotion tickets and coordination leases. `noeviction` is mandatory.

## Initial safety and timing budgets

These are B17 activation limits, not measured production SLOs:

| Control | Initial value | Policy |
| --- | ---: | --- |
| Queue admission | 25 ms | Reject boundedly; never wait on a map tick |
| Logical Redis operation | 250 ms | Hard application deadline |
| Initial connection | 1,000 ms | Startup/readiness fails closed |
| Concurrent operations/process | 128 | Timed-out driver work keeps its permit until it ends |
| Circuit breaker | 5 failures / 5 s open | Reject new uncertain coordination while open |
| Worker heartbeat / TTL | 5 s / 20 s | TTL must remain greater than twice heartbeat |
| Player renewal / TTL | 10 s / 30 s | TTL must remain greater than twice renewal |
| Ticket lifetime | 60 s | Consume once; no stale fallback |
| Configured coordination capacity | 4,096 | A process safety bound, not a Redis memory guarantee |
| Local Redis memory | 256 MiB, `noeviction` | Disposable local/CI dependency only |

Before public multi-host activation, a same-region staging test must
demonstrate Redis operation p95 at or below 10 ms and p99 at or below 50 ms
under the expected login, renewal, and route mix. The 250 ms deadline remains
a safety cutoff, not an acceptable steady-state latency.

No managed-provider availability SLA, regional failover guarantee, production
memory tier, or monthly cost is approved by this ADR. Those values require a
provider quote and staging measurements. Public multi-host activation remains
blocked until they are recorded.

## Failure policy

| Failure | Required behavior |
| --- | --- |
| Redis unavailable, slow, or circuit open | Reject new cross-process login, transfer, reconnect, placement, and lease acquisition |
| Ticket/admission result uncertain | Do not issue a second identity or report success; require a fresh authenticated flow after expiry |
| Worker registration missing | Exclude the worker from new placement; do not infer readiness |
| Route missing or stale | Reject the handoff; never choose an arbitrary same-map worker |
| Player lease cannot renew | Stop new valuable commands for that session, drain or disconnect, and reacquire through PostgreSQL fencing |
| Redis restarts empty | Treat all routes/presence as unknown; re-register workers and reacquire player leases with valid/new PG fences |
| Two workers claim one player | PostgreSQL fence decides; drain the stale owner and alert |
| Cache/projection unavailable | Read PostgreSQL or hide the projection; do not invent player state |

Established local play may continue during a short outage only while the
process still has an unexpired proven lease and its PostgreSQL fence remains
valid. Missing Redis state never means an owner is free.

## Security boundary

Production Redis must:

- use a private endpoint and TLS;
- use an application ACL limited to `godswar:<environment>:v1:*` and an
  explicit reviewed command allowlist, including only the required Lua,
  hash, sorted-set, TTL, health, connection-bootstrap, and Redis-clock
  commands;
- receive credentials from an approved secret manager or read-only secret
  mechanism, never source control;
- disable administrative access, automatic `CONFIG`/`CLUSTER` probes, and
  the single-primary tie-breaker key for the application client;
- keep any operational or fault-injection identity separate from the
  application identity;
- use `noeviction` for coordination keys; and
- keep metrics, connection strings, tickets, lease tokens, and player
  identities out of logs and metric labels.

The checked-in Compose dependency is loopback-only, ephemeral, ACL-protected,
non-persistent, and explicitly permits plaintext only for
`LocalDevelopment`. It is not a production topology.

## Rollout

1. Keep `Local` as the default; the async contracts and Redis adapters are
   opt-in.
2. Require the disposable Redis integration gate in CI.
3. Prove two-process expiry, restart, slow/unavailable, duplicate, stale
   route, and PostgreSQL-fence cases before remote staging.
4. Run staging shadow comparisons without allowing Redis and local providers
   to become simultaneous authorities.
5. Drain to one authoritative worker, activate Redis for new sessions, then
   admit traffic gradually.
6. Enable remote placement or reconnect only in separate accepted slices.

## Rollback

Rollback is coordinated, never an automatic per-request fallback:

1. stop new admissions and transfers;
2. drain to one known authoritative worker;
3. verify PostgreSQL ownership fences and durable queues;
4. set the coordination provider to `Local` and restart the gateway/worker
   set as one authority;
5. require fresh login for discarded tickets/reconnect windows; and
6. retain PostgreSQL data, migrations, audits, and owner generations.

Do not switch individual live sessions silently between Redis and local
coordination. Do not delete Redis keys as the first rollback step.

## Consequences and nonclaims

B17 gains a reversible cross-process coordination seam without moving player
value out of PostgreSQL. Deadlines, bounded concurrency, circuit breaking,
typed keys, and explicit outage behavior make Redis failure observable and
fail-safe.

It also adds a network dependency and operational burden. This ADR does not
claim:

- a deployed or highly available managed Redis service;
- a production availability, capacity, recovery, or cost guarantee;
- automatic worker failover or live world-instance migration;
- cross-realm Pindus settlement;
- secure-UDP gateway routing; or
- 300-player server capacity.

MongoDB remains unjustified for this coordination workload.
