# ADR 0004: Realm, node, and world-instance topology

- Status: Accepted
- Date: 2026-07-31
- Decision owner: Godswar server maintainers
- Roadmap tickets: B17, B18A, and B18B
- Supersedes: the target-topology and B17-activation conclusions of
  [ADR 0003](0003-defer-redis-coordination.md)

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

These requirements establish a real cross-process routing and coordination
use case. They do not mean Redis is already implemented or that every map
must immediately move to a separate process.

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

The first implementation remains local-first: the gateway, placement
registry, and all instances may still run in one process. The same
application contracts must later admit a remote worker without changing ECS
or durable economy semantics.

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
5. Do **not** add or deploy Redis merely for the current one-process runtime.
   B17 implementation begins with the first runnable two-process
   gateway/worker or worker/worker slice.
6. Keep the server a modular monolith in code and split deployable processes
   only at explicit composition boundaries.

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

## B17 activation and rollout

B17 is now **approved for future implementation, not implemented or
deployed**. Before its runtime slice is enabled, the team must still record:

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
   **Implemented.**
3. A runnable gateway/worker split in local development.
4. B17 Redis adapter, two-process tests, observability, and failure policy.
5. Controlled instance transfer, scheduled battlefield, then cross-realm
   settlement slices.

Rollback drains remote workers to one process and selects the local
placement/coordination implementation. PostgreSQL identities, ownership
generations, and durable results are retained.

## Consequences

The model supports many maps today, repeated dungeon instances, scheduled
battlefields, and future multiple realms without equating a map with a
server. It keeps current gameplay runnable while giving process separation a
stable destination.

The cost is additional identity, placement, lifecycle, transfer, and
operations work before scale-out is safe. Redis becomes an approved future
dependency, but it is deliberately absent until a second process exercises
the coordination boundary.
