# 7. Redis design

## 7.1 Decision and introduction gate

[ADR 0003](../adr/0003-defer-redis-coordination.md) is the historical
single-process defer. ADR 0004 confirmed the later multi-process topology,
and [ADR 0005](../adr/0005-b17-redis-coordination-activation.md) now governs
the implemented B17 boundary.

B17 is **completed and verified behind an explicit opt-in provider, but not
deployed as production infrastructure**. It provides async, deadline-bearing Redis
adapters for consume-once tickets, semantic login/admission, worker routes,
presence, and player leases carrying the PostgreSQL-issued ownership fence.
`Local` remains the default and constructs no Redis client. PostgreSQL still
owns all durable player value and the monotonic ownership generation.

The implementation includes finite concurrency/deadlines, circuit breaking,
opaque versioned keys, atomic scripts, readiness, metrics, a disposable
local/CI profile, and coordinated outage/rollback runbooks. Shared ticket,
admission, worker, route, and player-lease expiries come from Redis `TIME`;
local proof validity uses monotonic elapsed time rather than comparing
process wall clocks. The Lua scripts touch multiple dynamically selected
keys, so B17 supports one Redis primary
keyspace. Redis Cluster hash-slot sharding is not supported, and the current
protocol must not automatically promote an asynchronous replica: promotion
could resurrect a consumed ticket or superseded lease. Public HA requires an
approved zero-loss policy or a failover epoch that invalidates all
pre-promotion coordination state.

Before public activation, staging must still record peak demand, p95/p99
latency, availability, recovery, memory, provider/region, cost, remote
failure isolation, and rollback evidence. Directly connected map groups must
remain co-located until cross-worker transfer exists.

Synchronous Redis I/O in network handlers or ECS/map loops is forbidden.
PostgreSQL remains the only durable player-value and monotonic-fence
authority.

## 7.2 Key and value conventions after the gate

`RedisCoordinationKeyBuilder` owns the implemented prefix:

```text
godswar:<environment>:v1:<purpose>:<opaque-id>
```

Angle brackets denote placeholders, not Redis Cluster hash tags. Raw
usernames, IPs, tokens, account/character IDs, node IDs, and world-instance
IDs are not key names.

| Use case | Key/type/value | TTL | Owner and lifecycle | Invalidation/reconstruction | Outage behavior | Maximum staleness |
| --- | --- | --- | --- | --- | --- | --- |
| Secure ticket authority | `ticket`, `ticket-grant`, `ticket-generations`, `outstanding-tickets` | 60 s logical ticket; bounded cleanup retention | TLS authentication issues; game bind consumes once; Lua replaces/revokes atomically | Lost state requires fresh authenticated login | Deny new bind; no local fallback | None after consumption |
| Semantic login/admission | `login-account`, `login-name`, `login-connection`, `admission`, `gateway-counters`, `gateway-expiry` | Validated gateway limits/TTLs | Semantic gateway starts/activates one generation and reserves/commits an exact route | Re-authenticate and re-reserve | Reject uncertain admission | None for single-use transitions |
| Worker and route | `server`, `route` | 20 s default worker TTL; 5 s heartbeat | Worker process registers one boot incarnation and exact routes | Same live process re-registers the same boot ID; a restarted process uses a new ID | Exclude unknown/draining worker | One heartbeat/lease proof |
| PG-fenced player presence | `player` | 30 s default; renew 10 s | Worker installs the committed PG UUID/generation plus presence and exact route | Reacquire through PG fence; missing key is not free ownership | Stop uncertain valuable work and disconnect/drain | One proven lease |

Future caches, leaderboards, invitations, matchmaking, reconnect windows,
and distributed rate limits remain illustrative only. They require their own
owner, TTL, bound, reconstruction, outage, and acceptance record before use.

Redis persistence is not required for these disposable states. A replica or
backup may assist operational recovery only after the ticket/lease
invalidation policy is designed and approved; it is never the source of
truth. For ownership/routing Redis, use `noeviction` and explicit capacity
planning. Caches can use a separate eviction-capable instance if necessary
so cache pressure cannot evict ownership keys.

## 7.3 Durable fencing authority

PostgreSQL is the monotonic fencing authority. Acquiring or transferring a player atomically locks and increments one durable character-ownership row (or uses a proven equivalent CAS) and stores `owner_generation`. Redis then carries a TTL lease containing that exact PG-issued generation.

Every valuable transaction first locks that same ownership row for the full transaction and validates the expected owner/generation **before** mutating wallet, items, pets, progression, or child rows. Ownership transfer takes the conflicting lock and cannot advance the generation while a value transaction is in flight. A standalone check followed by unrelated child-row updates is not sufficient. An expired, evicted, restarted, or partitioned Redis instance can never reset the durable high-water mark.

This is not an unsafe permanent dual write:

1. PG locks the authoritative ownership row, allocates the new fence, and records the ownership transition/audit.
2. The Redis lease is a disposable coordination projection of that committed fence.
3. The new server becomes ready for valuable commands only after the lease is installed and verified.
4. If Redis installation fails, the PG fence remains advanced and no prior owner can pass the required ownership-row lock/validation; the attempted owner releases/drains and retries acquisition safely.

At low scale, PostgreSQL may also store/consume the entire short-lived ownership/ticket record, avoiding Redis.

## 7.4 Atomic Redis operations

Lua scripts or Redis Functions are appropriate for:

- consume-once ticket validation with expiry/audience/generation checks;
- install/acquire/renew/release a lease only when the opaque lease token and PG-issued fence match;
- token-bucket rate limiting;
- compare-version cache replacement;
- invitation accept-if-still-pending.

Do not use Redis transactions, Redlock, or a distributed mutex as the sole correctness boundary for inventory, currency, trade, auction, purchase, or character ownership. PostgreSQL constraints, aggregate versions, and fencing remain decisive.

## 7.5 Data that must never exist only in Redis

- credentials, account ownership, VIP/entitlements;
- characters and deletion state;
- inventory, equipment, mounts, mount gear, pets;
- currency balances or ledgers;
- EXP, levels, skills, talents, zodiac, durable boost time;
- quest/achievement/guild/friend/mail state;
- trades, auction listings/purchases, crafting outcomes;
- purchases/refunds;
- permanent world-boss/world state;
- security/economy/GM audit;
- the only record that a valuable command completed.

## 7.6 Failure behavior

- **Redis unavailable:** bypass caches and read PG. Keep established single-owner local sessions if safe. Deny new cross-instance ticket consumption, ownership acquisition, transfer, matchmaking, or reconnect that cannot be fenced. Durable local commands may continue only while the server's ownership lease/fence is known valid.
- **Redis slow:** use strict sub-tick-independent deadlines and circuit breaking. Never let Redis block ECS loops. Prefer stale/PG fallback for caches and reject coordination operations.
- **Redis restart/keys evicted:** rebuild presence/routes from live servers and projections from PG/outbox. Treat missing ownership as an incident requiring PG fence reacquisition, not proof that nobody owns the player. A new Redis lease carries the already durable or newly incremented PG generation.
- **Lease expires unexpectedly:** stop accepting valuable commands for that character, mark the session reconnecting/draining, allocate/verify a new PG fence and lease or disconnect. A stale server's PG updates fail their fence check.
- **Two servers believe they own a session:** the higher fencing token wins; the lower token cannot update PG. Alert, drain the loser, reconcile its pending nonvaluable checkpoints, and preserve audit evidence.
