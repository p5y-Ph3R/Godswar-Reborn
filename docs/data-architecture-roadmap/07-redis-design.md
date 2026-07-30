# 7. Redis design

## 7.1 Decision and introduction gate

Redis is **deferred** by
[ADR 0003](../adr/0003-defer-redis-coordination.md). B16 completed the
decision gate on 2026-07-31; B17 was evaluated and closed as conditional,
not activated. No Redis implementation or deployment is claimed.

The evidence is concrete:

- `Program.cs` composes login, game, `GameSessionRegistry`, the shared
  `InMemoryGameTicketStore`, and optional UDP runtime in one process;
- Compose defines one server and PostgreSQL, with no separate login, gateway,
  zone, placement, or coordinator service;
- account sessions, ticket state, admission, presence, and routing are
  process-local and bounded;
- the current limits are 512 active connections, 1,024 outstanding-ticket
  capacity, and a 60-second ticket TTL;
- no measurement shows ticket saturation or unacceptable coordination
  latency; and
- B15 made PostgreSQL the monotonic player-fence authority for every valuable
  transaction.

The repository has no approved second-process date, cross-instance reconnect
contract, player/login capacity target, Redis SLO, provider, regional
topology, or cost budget. Adding Redis now would create a network dependency
without solving a current requirement.

Redis may be reconsidered only after a superseding ADR confirms a real
cross-process/TTL use case **and measurements or approved SLOs show that
PostgreSQL plus local memory is inadequate**, for example:

- login and game endpoints run in different processes, share enough ticket traffic to justify a TTL store, and a measured PG ticket path is insufficient;
- multiple game/zone instances need player-to-server routing or duplicate-login fencing;
- reconnect can land on a different instance;
- cross-instance presence, invitations, matchmaking, or coarse abuse limits are required;
- a measured read projection cannot be served economically from PostgreSQL/in-process memory.

Before that gate, the current `InMemoryGameTicketStore`, registry presence,
and bounded limiter tables remain simpler and safer. Do not add Redis merely
to replace ordinary dictionaries in one process.

An approved B17 must first replace synchronous `IGameTicketStore` network
operations with asynchronous, deadline-bearing application contracts.
Synchronous Redis I/O in network handlers or ECS loops is forbidden. The
activation record must also define peak demand, p95/p99 latency, timeout,
availability, maximum staleness, recovery, eviction, memory, and cost
budgets.

## 7.2 Key and value conventions after the gate

Use a single builder library; never concatenate raw usernames, IP strings, or attacker-controlled text. Suggested prefix:

```text
godswar:<environment>:v1:<purpose>:<opaque-id>
```

Angle brackets above denote placeholders; they are not literal Redis Cluster hash tags. If hash tags are later used, they must be chosen narrowly per aggregate rather than pinning an entire environment to one slot. Hash IDs when exposing them to shared operational tooling. Every key family must declare owner, TTL, maximum cardinality, value version, and outage behavior.

| Use case | Key/type/value | TTL | Owner and lifecycle | Invalidation/reconstruction | Outage behavior | Maximum staleness |
| --- | --- | --- | --- | --- | --- | --- |
| One-time secure game ticket | `ticket:<hash>` string or hash containing version, account, audience, server, generation, expiry; never raw ticket | Current 60 s | Authentication/session service creates; game bind atomically consumes with `GETDEL` or Lua | Expiry; client re-authenticates. Reconstructed only by issuing a new ticket | Deny new cross-process game binds; established sessions continue | None after consumption |
| Active player ownership | `owner:character:<id>` hash: server ID, session generation, **PG-issued** fencing token, lease expiry | 30 s, refresh about 10 s | PG atomically allocates/stores the monotonic fence; session service places that fence in a compare-and-renew Redis lease | Expiry permits PG allocation of a higher fence; every PG write verifies it | Stop new ownership; retain established session only while its lease/fence is proven valid, otherwise drain | Under 10 s target; correctness comes from PG fence |
| Account duplicate-login generation | `owner:account:<id>` hash/string carrying a durable/session-authority generation | 30-60 s | Session service; durable high-water mark follows the approved ownership design | Replace via atomic generation update that cannot reset after Redis loss | Fail closed for new duplicate-prone logins | Under refresh interval |
| Presence | `presence:character:<id>` hash: server/map/status/version | 30-60 s | Owning server refreshes; logout deletes | Rebuild from active ownership/server heartbeats | Hide presence or mark unknown; never modify PG account value | 30 s |
| Player-to-server/map routing | `route:character:<id>` hash: server/zone/PG owner generation | Lease-aligned | Placement/session service | Derive from ownership and active server registry | Reject new handoff; existing local play continues where safe | One lease interval |
| Server registry/readiness | `server:<id>` hash with endpoint capability, build/content versions, drain state | 15-30 s | Each server heartbeat | Re-register on restart | Placement excludes unknown servers | 15 s |
| Reconnect window | `reconnect:<opaque-token-hash>` hash with account, target, generation, expiry; no keys/secrets | 60-120 s | Session service creates on unexpected disconnect; consumes once | Expiry means full login | Fall back to full TLS login | None after consume |
| Coarse distributed rate limit | prefix/account cost buckets in hashes or strings | Seconds/minutes | Edge/session service | Natural TTL; reset is acceptable | Use stricter local limits; never open admission | One bucket interval |
| Valuable-command idempotency prefilter | `seen:<scope>:<operation>` short result marker | 5-30 min | Application adapter writes only after PG commit/outbox | PG inbox is authoritative; repopulate on demand | Query PG inbox directly | Cache TTL only |
| Character summary cache | `character-summary:<id>:v<version>` string/message-pack/JSON | 1-5 min | Query adapter fills cache-aside after PG read | Outbox invalidates; version mismatch misses | Read PG | Product-specific, normally under 1 min |
| Configuration cache | `content:<revision>:<type>:<id>` | Hours, immutable by revision | Content query service | New revision uses new keys; old expires | Read PG/packaged content | Revision-pinned, not time based |
| Leaderboard projection | sorted set plus metadata revision | Hours with rebuild marker; or retained projection | Projection worker consumes PG outbox | Rebuild from PG ledger/snapshot; swap complete version | Hide/stale-board indicator; gameplay unaffected | Product decision |
| Party/guild invitations | hash/set containing opaque IDs and state | 2-10 min | Future social service | TTL/delete on accept; authoritative membership commits in PG | Invitations unavailable; membership unaffected | TTL |
| Matchmaking queue | sorted set/stream plus lease | Seconds/minutes | Future matchmaking service | Players requeue after outage | Queue temporarily unavailable | A few seconds |
| Distributed lease/short lock | compare-and-renew token value | Operation-specific, short | Owning application service | Expiry + fencing; no correctness without PG constraint/version | Abort operation on uncertainty | None |

Redis persistence is not required for these disposable states; configure replication/backup only for faster operational recovery, not as the source of truth. For ownership/routing Redis, use `noeviction` and explicit capacity planning. Caches can use a separate eviction-capable instance if necessary so cache pressure cannot evict ownership keys.

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
