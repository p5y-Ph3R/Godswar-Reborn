# ADR 0004: Realm, node, and world-instance topology

- Status: Accepted
- Date: 2026-07-31
- Decision owner: Godswar server maintainers
- Roadmap tickets: B17, B18A, B18B, B18C1, and B18C2
- Supersedes: the target-topology and B17-activation conclusions of
  [ADR 0003](0003-defer-redis-coordination.md)
- B17 implementation status is superseded by
  [ADR 0005](0005-b17-redis-coordination-activation.md)

## Context

ADR 0003 correctly recorded the repository state on 2026-07-31: one
`Godswar.Server` process hosts every map, tickets and sessions are local,
PostgreSQL is the only external data service, and no Redis runtime exists.
Based on the information available then, it deferred Redis and closed B17 as
conditional.

The product topology is now confirmed:

- **Tempest** is the first logical realm, not the name of an operating-system
  process.
- Additional independently hosted realms are planned.
- Open-world maps may be spread across multiple worker processes without
  changing their realm.
- Pindus is planned as a cross-realm battlefield. Ni Mini Valley and
  Lelantine are also scheduled battlefield content.
- Battlefield instances are short-lived. The initial product assumption is
  up to two openings per day and a maximum 45-minute active window; schedules
  remain configuration/content rather than hard-coded topology.
- Medusa Island, Atlantis, Wonderland, and Bay Under Attack are on-demand
  dungeon-instance content. A worker process may host many isolated
  instances; an instance does not require its own process.

These requirements established a real cross-process routing and coordination
use case. At this ADR's acceptance they did not mean Redis was implemented,
and they still do not mean every map must move to a separate process.

## Identity model

The following identities have different meanings and must not be
interchanged:

| Identity | Meaning | Lifetime |
| --- | --- | --- |
| `RealmId` | Positive integer logical realm; `Tempest = 1` | Long-lived; independent of process restarts |
| `ServerNodeId` | Validated opaque node string; local default `local-node` | One configured node/incarnation; never a player identity |
| `WorldInstanceId` | GUID-backed authoritative simulation identity | From instance creation through final closure |
| `MapId` | Nonnegative short content ID with a checked legacy-byte bridge | Content-version lifetime |

The existing PostgreSQL `public.server` catalog is the logical realm catalog
for this additive slice: row `id = 1` is Tempest and
`character_base.server_id` already references it. All nine characters in the
inspected development database use `server_id = 1`. B18A represents that as
`RealmId.Tempest = 1`; it does not create a duplicate realm table, rename the
legacy table, add realm fields to child tables, or change character
serialization.

Migration `20260731_035_tempest_realm_authority` makes that existing
single-realm contract explicit. Its preflight requires the exact Tempest
`id`, name, identifier, and validated character foreign key; it rejects
pre-existing non-Tempest characters. It then backfills only null
`server_id` values to `1`, sets default `1` and `NOT NULL`, adds an index,
and adds the temporary check `server_id = 1`.

That check deliberately prevents accidental realm-two characters while both
create paths still assume Tempest. A later forward migration may remove it
only after `RealmId` is carried through lifecycle/load commands and realm
scoping for names and account slots is decided. Globally unique
`character_id` remains the child-table identity, so child tables do not copy
the realm ID.

Secure tickets' current `TargetServerId = 100` is a protocol
audience/routing value. It is **not** Tempest's durable `RealmId = 1` and
must not be reused as one.

`MapId` is not sufficient routing identity. Several dungeon parties or
battlefield matches can use the same `MapId` concurrently while having
different `WorldInstanceId` values and isolated ECS state.

World instances have an explicit kind:

- `OpenWorld`
- `Battlefield`
- `Dungeon`

Their lifecycle is:

```text
Creating -> Active -> Draining -> Closed
    `---------------------------> Closed (cancelled creation)
```

Only the owning worker may mutate an active instance. `Draining` rejects new
admissions while allowing a bounded transfer, result-settlement, and shutdown
window. `Closed` cannot be reactivated under the same `WorldInstanceId`.

## Target topology

```text
Original client
      |
      v
stable login/game gateway
      |
      +--> Tempest realm open-world workers
      |       `--> open-world and local map instances
      |
      +--> future realm workers
      |
      `--> cross-realm instance workers
              +--> scheduled Pindus battlefield instances
              +--> other scheduled battlefield instances
              `--> on-demand party dungeon instances
```

The stable gateway owns client-facing transport continuity, authentication
association, and routing. It does not own combat, inventory, rewards, or
world simulation. A worker owns the fixed-step ECS state for each assigned
`WorldInstanceId`.

The completed process-separation increments remain local-first:

```text
original client -> B18C1 opaque raw-TCP relay -> one combined worker
                or
                -> B18C2 loopback semantic gateway
                -> mTLS private exact-routed authoritative worker
```

B18C1 is not the stable semantic gateway pictured above. It has no
authentication, session authority, packet interpretation, placement, or
`WorldInstanceId` route decision. The combined worker still owns networking
sessions, game handlers, placement, every map/instance and B18B mailbox, and
all PostgreSQL/JSON access.

B18C2 now supplies the semantic gateway/session-authority boundary without
changing ECS or durable economy semantics. It is not yet the remote,
high-availability production gateway in the target diagram: its unchanged
client edge is deliberately loopback-only, its coordination is in memory,
and it has no UDP gateway path or live cross-worker transfer.

## Durable and temporary ownership

PostgreSQL remains the sole authoritative owner of:

- global accounts and credentials;
- a character's home realm and durable identity;
- inventory, equipment, currency, progression, pets, mounts, skills,
  entitlements, and economy audit;
- monotonic player-owner generations and idempotent command results;
- committed battlefield/dungeon admission, lockout, result, and reward
  records when those features are implemented.

Redis is approved only for disposable cross-process coordination:

- node readiness and drain registration;
- instance placement and route lookup;
- short-lived login/game/transfer tickets;
- online presence and reconnect routing;
- TTL ownership leases carrying the exact PostgreSQL-issued owner generation;
- scheduled-instance admission coordination where a local owner is
  insufficient.

Redis never stores the only copy of player value, a completed reward, a
match result, or the monotonic ownership fence. There is no PostgreSQL/Redis
dual-write transaction: PostgreSQL commits durable state plus outbox, while
Redis records are leases, routes, or rebuildable projections.

## Cross-realm battlefield boundary

A future cross-realm Pindus flow must use the character's home realm as the
durable authority:

1. The home realm creates an idempotent admission and a versioned battlefield
   loadout projection.
2. A short-lived, audience-scoped transfer ticket admits the player to one
   Pindus `WorldInstanceId`.
3. The battlefield worker owns only the temporary combat representation.
4. It emits a signed/authenticated, idempotent result command.
5. The home realm applies rewards and progression exactly once in
   PostgreSQL.
6. Timeout, reconnect, worker crash, or duplicate result delivery cannot
   duplicate rewards or strand the durable character.

Cross-realm direct writes into another realm's inventory or wallet are
forbidden.

## Decision

1. Adopt the realm/node/world-instance identity model above.
2. Treat Tempest as the default first realm while preserving current
   one-process behavior and existing characters.
3. Make B18A the local identity and placement foundation before splitting
   processes.
4. Reopen B17 and approve Redis as the target coordination store for the
   confirmed multi-process topology.
5. B18C2 now satisfies the semantic gateway/worker prerequisite for B17.
   Begin B17 next only after the Redis operational budgets are recorded;
   replace disposable coordination, never PostgreSQL player-value authority.
6. Keep the server a modular monolith in code and split deployable processes
   only at explicit composition boundaries.
7. Treat B18C1 as a reversible local/raw-development topology proof, not as
   the production gateway or a distributed authority.
8. Treat B18C2 as a verified local-first semantic boundary, not as proof of
   remote production placement, high availability, secure UDP routing, live
   transfer, or capacity.

ADR 0003 remains the historical evidence for why no Redis package or service
was added during B16. This ADR supersedes only its assumption that there was
no approved multi-process target and its resulting closure of B17.

## B18A scope and acceptance boundary

B18A introduces:

- typed `RealmId`, `ServerNodeId`, `WorldInstanceId`, and `MapId` values;
- `RealmId.Tempest = 1` mapped to the existing legacy `server` row, without a
  new realm table or character serialization change;
- migration `20260731_035_tempest_realm_authority`, which preserves the
  legacy FK and enforces the current Tempest-only character invariant;
- `InstanceKind` and the instance lifecycle state machine;
- a local placement/router contract and in-memory implementation;
- exactly one active owner for every local `WorldInstanceId`;
- a Tempest default that does not reinterpret existing numeric map IDs or
  character IDs.

B18A does not claim:

- a Redis adapter or Redis deployment;
- a second process, remote transfer, reconnect across processes, or
  cross-realm gameplay;
- implemented Pindus scheduling, dungeon matchmaking, reward settlement, or
  second-realm character selection/lifecycle support;
- one process per map or per dungeon.

Acceptance requires existing Tempest login, portal, movement, combat, and
map behavior to remain compatible; multiple instances of one map definition
must have distinct placement identities and single assignments; and
lifecycle transitions must reject invalid reactivation, closing with live
assignments, or admission while draining. Actual instance-aware
`GameSessionRegistry` routing and isolated mutable ECS ownership were
deliberately deferred to B18B and are recorded below.

## B18B implementation boundary

B18B now composes the local placement model into the live one-process
runtime:

- `GameSessionContext` carries `RealmId` and `WorldInstanceId`;
- `LocalWorldInstanceRuntimeDirectory` uses `WorldInstanceId` as its primary
  runtime key and retains a separate Tempest default-open-world byte-map
  projection;
- repeated dungeon and battlefield runtimes may share one content `MapId`
  without sharing map state, population, NPCs, monsters, or broadcasts;
- each `WorldInstanceRuntime` owns a bounded FIFO
  `BoundedSingleOwnerMailbox<MapInstance>`;
- membership, NPC catalog, and authoritative monster mutations enter through
  that owner boundary; and
- socket fanout and durable database work stay outside owner commands.

The legacy byte-map API resolves a routed session's exact instance when one
exists and otherwise resolves only Tempest's default open world. It never
means "all instances that use this map definition." Legacy portal targets
likewise select a default open-world instance; explicit dungeon or
battlefield admission must supply a `WorldInstanceId`.

B18B remains process-local. It does not implement a gateway/worker backhaul,
Redis, remote placement or transfer, cross-process reconnect, client-facing
dungeon admission, scheduled battlefield orchestration, or cross-realm
settlement. Existing per-player durable fencing and feature-level
coordination also remain distinct from the map mailbox. See the
[B18B implementation evidence](../data-architecture-b18b-instance-routing-mailboxes-20260731.md).

## B18C1 implementation boundary

B18C1 adds the separate opt-in
`Godswar.Server --relay-gateway <configPath>` process mode. Its login and
game listeners copy opaque bytes to exactly one configured private or
loopback combined worker. One global connection cap, fixed pooled buffers,
connect/idle/write deadlines, TCP half-close, tracked finite drain, and
finite-label in-memory snapshot/`.NET Meter` signals bound the relay. Relay
mode does not compose a management endpoint or metrics exporter.

The worker now has a configurable `ServerNodeId` and a raw advertised game
`PublicPort`, so its private game listener can redirect the original client
back to the relay. These values do not give the relay placement or session
authority.

B18C1 does not terminate TLS/authentication, interpret packets, route by
`WorldInstanceId`, share tickets or sessions, relay secure UDP, preserve
source IP, coordinate workers, transfer/reconnect across workers, schedule
battlefields/dungeons, settle cross-realm results, or use Redis. Secure UDP,
if experimented with, remains direct-to-worker and is not B18C1 acceptance.
See the
[B18C1 implementation evidence](../data-architecture-b18c1-local-relay-gateway-20260731.md).

Completion requires both automated in-process coverage and the Docker-free
real two-process smoke. Both passed in the carrying tree, alongside the
275-check managed catalog and the 43-check, five-scenario PostgreSQL gate.

## B18C2 implementation boundary

B18C2 adds the opt-in
`Godswar.Server --semantic-gateway <serverOptionsPath> <gatewayConfigPath>`
process and a separate worker backhaul mode. The unchanged original client
speaks legacy raw TCP only to a loopback listener. The gateway verifies the
credential locally, creates a bounded login generation, and permits one
lifetime game admission for that generation. A reconnect requires another
complete login.

The gateway selects an exact
`RealmId`/`MapId`/`WorldInstanceId`/`ServerNodeId` route. It sends fixed,
authenticated admission metadata across a TLS 1.3 private hop with mutual
leaf pinning and ALPN, then tunnels the original encrypted game bytes
unchanged. The worker validates node, route, account, replay, expiry,
capacity, and drain policy before exposing a bound principal to the existing
game handler. ECS, map simulation, inventory, economy, and persistence stay
on the worker.

B18C2 is completed and verified: focused checks passed `5/5`, the full
managed catalog passed `280/280`, the Release build completed with zero
warnings and zero errors, the disposable PostgreSQL gate passed `43/43`
checks plus `5/5` migration scenarios and cleanup, and development
backhaul-certificate validation passed. See the
[B18C2 evidence](../data-architecture-b18c2-semantic-gateway-backhaul-20260731.md).

The semantic gateway is the full account-admission authority. mTLS
authenticates and encrypts the private hop, but it cannot defend a worker
against a compromised gateway that possesses an accepted pinned key.
Compromise therefore requires gateway isolation/rebuild, admission draining
or invalidation, and prompt revocation and rotation of that key and pin at
every worker.

B18C2 does not implement Redis, distributed discovery, shared presence,
secure-UDP gateway routing, live cross-worker transfer, remote production
placement, high availability, or a capacity guarantee. Static open-world
routes are selected at login; maps joined by direct portal movement must
remain on the same worker until a controlled transfer protocol exists.

## B17 activation and rollout

B17 is now **completed and verified behind an opt-in provider, but not
deployed as production infrastructure** under ADR 0005. Before public
scale-out, the team
must still record:

- node and instance capacity targets;
- placement, sticky-routing, transfer, drain, and split-brain rules;
- Redis latency, availability, timeout, memory, eviction, recovery, region,
  provider, and cost budgets;
- async deadline-bearing ticket/coordination contracts;
- two-process tests covering lease expiry, Redis loss/restart, stale routing,
  duplicate admission, reconnect, and PostgreSQL fencing.

The rollout order is:

1. B18A local identities and placement. **Completed.**
2. B18B owner mailboxes and transport-independent local instance routing.
   **Completed.**
3. B18C1 bounded opaque login/game TCP relay to one combined worker.
   **Completed and verified.**
4. B18C2 semantic gateway/session authority and backhaul, gateway connection
   identity, `WorldInstanceId` routing, and admission/source identity.
   **Completed and verified.**
5. B17 Redis adapter, shared-coordination tests, observability, budgets, and
   failure policy. **Implemented opt-in; production deployment gated.**
6. Controlled instance transfer, scheduled battlefield, then cross-realm
   settlement slices.

B18C2 rollback drains admissions, omits semantic-gateway/worker-backhaul
mode, and returns to B18C1 or the directly advertised single worker.
PostgreSQL identities, ownership generations, and durable results are
retained.

## Consequences

The model supports many maps today, repeated dungeon instances, scheduled
battlefields, and future multiple realms without equating a map with a
server. It keeps current gameplay runnable while giving process separation a
stable destination.

The cost is additional identity, placement, lifecycle, transfer, and
operations work before scale-out is safe. The semantic boundary and opt-in
B17 coordination are implemented locally. Public Redis activation still
requires provider, staging, remote-failure, and PostgreSQL-fence evidence.
