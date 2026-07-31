# ADR 0003: Defer Redis coordination

- Status: Superseded in part by
  [ADR 0004](0004-realm-and-world-instance-topology.md) and
  [ADR 0005](0005-b17-redis-coordination-activation.md)
- Date: 2026-07-31
- Decision owner: Godswar server maintainers
- Roadmap tickets: B16 and conditional B17

> Historical note: this ADR accurately records the repository evidence and
> B16 decision at the time it was accepted. ADR 0004 later confirmed a
> multi-realm, cross-process target and reopened B17. ADR 0005 then approved
> and implemented an opt-in Redis coordination provider. Redis is still not
> deployed as production infrastructure, and PostgreSQL remains the durable
> owner of player value.

## Context

B16 is the decision gate that prevents an operational database from being
added merely because it may become useful later. B17 is conditional on B16
approving a measured multi-process coordination need.

The inspected repository and running development topology provide this
evidence:

- `src/Godswar.Server/Program.cs` constructs one `GameSessionRegistry`, one
  shared `InMemoryGameTicketStore`, the login and game listeners, and the
  optional authenticated-UDP runtime in one process. It requires exactly one
  coherent login/game listener pair.
- `docker-compose.yml` defines one server and one PostgreSQL service. There is
  no separate login, gateway, zone, placement, or coordinator process.
- `GameSessionRegistry.AccountSessions.cs` keeps account-session replacement
  and checkpoint-acquisition gates in process-local dictionaries and locks.
- `IGameTicketStore` and `InMemoryGameTicketStore` provide bounded, single-use
  tickets in memory. The configured ticket TTL is 60 seconds, ticket capacity
  is 1,024, and the configured process connection limit is 512.
- `OperationalStateMetrics` exposes ticket capacity and outstanding-ticket
  counts. No checked-in measurement demonstrates ticket saturation, an
  unacceptable ticket latency, or a need for shared ticket state.
- Reconnect currently means a fresh login/game bind and character reload.
  There is no cross-instance resume, placement, or player-to-server routing
  contract.
- B15 made PostgreSQL's `character_base.checkpoint_owner_id` and monotonic
  `checkpoint_owner_generation` the authoritative player fence. Valuable
  PostgreSQL transactions lock and validate that exact fence.
- The immediate development profile remains one process, while the secure
  transport is opt-in and the checked-in Compose profile is the loopback raw
  compatibility profile.

The repository has no approved target for concurrent players, peak login
rate, second-process date, reconnect SLO, Redis latency or availability SLO,
maximum coordination staleness, managed provider, regional topology, or
budget. Adding Redis now would create installation, patching, monitoring,
capacity, outage, and incident-response work without solving a current
runtime requirement.

There is also a contract mismatch that must not be hidden:
`IGameTicketStore.BeginLogin`, `Issue`, `Consume`, and `RevokeGeneration` are
synchronous. A network-backed implementation must first use asynchronous,
deadline-bearing operations. Blocking a connection handler on synchronous
Redis I/O would be an unsafe implementation shortcut.

## Decision

Redis is **deferred**.

B16 is complete with an explicit defer decision. B17 was evaluated and is
closed as **conditional, not activated**. This does not claim that a Redis
adapter, Redis deployment, cross-instance routing, or reconnect service was
implemented.

The current production data recommendation remains PostgreSQL only:

- PostgreSQL is the sole durable player-value authority.
- The existing in-process ticket, session, presence, admission, and UDP state
  remain disposable runtime state for the current one-process topology.
- No Redis client package, container, configuration, keyspace, health check,
  or runtime dependency is introduced by B16/B17.
- Redis must not be added simply to replace a bounded dictionary in one
  process.

## Activation gate

B17 may be reopened only through a superseding ADR after all applicable
conditions below are supplied:

1. An approved topology requires at least two login/game/zone processes, or a
   reconnect can intentionally arrive at a different process.
2. The owner of placement and player routing is defined, including drain,
   transfer, split-brain, and rollback behavior.
3. Peak sessions, login rate, ticket issue/consume rate, region count, and
   reconnect-window demand are quantified.
4. A reproducible benchmark shows PostgreSQL plus process-local memory cannot
   satisfy the approved latency, throughput, or operational SLO, or the
   cross-process TTL semantics themselves require a shared coordinator.
5. Redis availability, latency, timeout, maximum-staleness, recovery, memory,
   eviction, and cost budgets are approved.
6. The hosting choice and failure boundary are known. A Redis outage must not
   become loss of authoritative player value.

An approved second process is a strong candidate use case, but it is not
permission to skip the capacity, ownership, failure, and cost decisions.

## Minimum future B17 scope

If the activation gate is later satisfied, the initial implementation stays
narrow:

| Coordination family | Initial policy |
| --- | --- |
| One-time secure game ticket | 60-second TTL; hashed opaque ticket; atomic consume; account, protocol, audience, target server, permissions, and expiry scoped |
| Player owner and route lease | 30-second TTL, renewed around every 10 seconds; exact PostgreSQL-issued owner UUID and generation; compare-and-renew token |
| Server readiness registry | 15-30-second TTL; endpoint capability, build/content revision, and drain state |
| Presence | Lease-aligned disposable projection; unknown during outage rather than invented offline/online truth |
| Reconnect token | Only after product semantics are approved; candidate 60-120-second consume-once TTL |

The first implementation must:

- change the ticket/session coordination contracts to asynchronous operations
  with cancellation, strict deadlines, bounded concurrency, and circuit
  breaking;
- keep the Redis driver under `Infrastructure`, behind intent-specific
  application contracts;
- use a single bounded key builder and never place raw usernames, IP
  addresses, ticket secrets, session keys, or attacker-controlled strings in
  keys or metric labels;
- use atomic compare-and-renew/release logic for leases and atomic
  consume-once logic for tickets;
- use a non-evicting, capacity-planned coordination deployment; eviction-safe
  caches must not share a failure budget with ownership keys;
- keep Redis calls off ECS/map ticks;
- expose bounded latency, timeout, rejection, lease-renewal, and connection
  metrics plus readiness behavior;
- prove behavior with at least two server processes and Redis
  restart/slow/eviction simulations.

Redis loss or restart never resets the player fence. Missing lease state is
not evidence that no player owner exists. New cross-instance binds,
acquisitions, transfers, or reconnects fail closed when coordination is
uncertain. A stale process cannot commit valuable data because PostgreSQL
still locks and validates the exact durable fence.

## Data that remains PostgreSQL-authoritative

Redis must never be the only owner of:

- credentials, accounts, character ownership, lifecycle, or entitlements;
- inventory, equipment, mounts, pets, currency, ledgers, progression,
  talents, skills, Zodiac state, rewards, or purchases;
- the monotonic player-owner generation or the only record that a valuable
  command completed;
- inbox, outbox, audit, reconciliation, or permanent world outcomes.

Redis coordination records are disposable projections. They do not
participate in an unsafe PostgreSQL/Redis dual-write transaction.

## Consequences

The current server remains simpler, runnable, and free of an unused
availability dependency. B15 provides the durable scale-out safety boundary,
but routing and rapid stale-session eviction remain future work.

The trade-off is deliberate: the server cannot claim cross-instance tickets,
presence, routing, or reconnect today. Those capabilities remain unavailable
until a real topology and SLO justify reopening B17.

## Verification and rollback

B16/B17 verification uses documentation, repository-evidence validation, and
the `DeferredRedisArchitectureChecks` protocol-check ratchet:

- the ADR and roadmap status agree;
- the server project has no Redis client package reference;
- Compose has no Redis service or image;
- `Program.cs` retains the configured bounded `InMemoryGameTicketStore`
  composition and no Redis infrastructure directory exists;
- all cited current symbols and limits exist;
- B17 is labelled conditional-not-activated rather than implemented.

The ratchet's failure text requires ADR 0003 to be deliberately superseded
before Redis is added. There is no runtime or schema rollback. Reverting
these documentation and test changes restores the prior undecided roadmap
wording. Any future Redis activation requires its own tested, reversible
implementation and deployment plan.
