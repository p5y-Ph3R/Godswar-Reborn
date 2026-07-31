# B18B local instance routing and single-owner mailboxes

Status: implemented on 2026-07-31; final integrated verification is recorded
in the repository commit that carries this report

## Outcome

B18B composes the B18A identity and placement foundation into the live
one-process game runtime:

- `WorldInstanceId`, rather than the legacy byte `MapId`, is the primary
  identity for a mutable map runtime.
- Two dungeon or battlefield runtimes may use the same map definition while
  retaining separate player, NPC, monster, and broadcast state.
- Existing client traffic still resolves a byte map to Tempest's one default
  open-world instance.
- Each local `WorldInstanceRuntime` owns one bounded single-owner mailbox.
- Session membership, NPC publication/canonical checks, and authoritative
  monster mutations are routed through the selected instance owner.
- Network sends and durable database work remain outside the mailbox command.
- Broadcast and monster-tick fanout use a configured concurrency bound.

This is a local modular-monolith milestone. It proves the routing and
ownership boundary needed by a later gateway/worker split; it does not
pretend that a second process or Redis exists.

## Runtime directory and identity

[LocalWorldInstanceRuntimeDirectory.cs](../src/Godswar.Server/Game/WorldInstances/LocalWorldInstanceRuntimeDirectory.cs)
is the process-local owner of live runtimes. It composes:

- B18A's bounded
  [LocalWorldInstancePlacementRegistry.cs](../src/Godswar.Server/Application/WorldInstances/LocalWorldInstancePlacementRegistry.cs);
- one
  [WorldInstanceRuntime.cs](../src/Godswar.Server/Game/WorldInstances/WorldInstanceRuntime.cs)
  per active identity; and
- one `BoundedSingleOwnerMailbox<MapInstance>` per runtime.

The primary index is `WorldInstanceId`. A separate byte-map index exists
only for Tempest's default `OpenWorld` compatibility projection.
`CreateInstancedAsync` permits repeated `Dungeon` and `Battlefield`
instances for the same `MapId`; it rejects `OpenWorld` because default open
worlds are created through the explicit compatibility operation.

Runtime creation registers and activates placement before publishing the
runtime. A failed activation is retired instead of leaving a partially
published owner. `BeginDrain` blocks new placement while leaving the mailbox
open for resident cleanup. `Close` verifies both placement and map
population, drains the owner to a finite deadline, and only then publishes
`Closed`; a forced or pre-stopped owner is quarantined instead. Removing a
closed runtime disposes its mailbox.

[MapInstance.Identity.cs](../src/Godswar.Server/Game/MapInstance.Identity.cs)
binds the active immutable descriptor into the map. The legacy byte remains
available only because the original client protocol cannot yet represent a
larger content-map identity.

## Session routing and transfer

[GameSessionContext.cs](../src/Godswar.Server/Game/GameSessionContext.cs)
now carries both `RealmId` and `WorldInstanceId`.

The partial registry files separate the new concerns:

- [GameSessionRegistry.WorldInstances.cs](../src/Godswar.Server/Game/GameSessionRegistry.WorldInstances.cs)
  owns directory composition, lookup, configuration snapshots, owner
  invocation, and bounded disposal.
- [GameSessionRegistry.WorldMembership.cs](../src/Godswar.Server/Game/GameSessionRegistry.WorldMembership.cs)
  assigns, transfers, releases, and rolls back character placement together
  with map membership.
- [GameSessionRegistry.WorldRouting.cs](../src/Godswar.Server/Game/GameSessionRegistry.WorldRouting.cs)
  resolves exact instance sessions, population, broadcasts, and monster
  initialization.
- [GameSessionRegistry.MapTransfers.cs](../src/Godswar.Server/Game/GameSessionRegistry.MapTransfers.cs)
  stages hidden transfers and rolls back placement, character position, and
  map membership if publication fails.

`JoinMap` retains the original behavior by resolving or creating Tempest's
default open-world instance. The internal `JoinWorldInstance` boundary
admits a character to a specified local active instance. An explicit
same-map transfer therefore changes `WorldInstanceId` without conflating the
two simulations.

The legacy routing rule is deliberately narrow:

1. if a routing session is already associated with the requested byte map,
   its exact `WorldInstanceId` is used;
2. otherwise the operation resolves only Tempest's default open-world
   projection; and
3. it never broadcasts to every dungeon or battlefield that happens to use
   the same byte map.

Player removal releases both runtime membership and the local placement
assignment. A later clean join can reuse the character in another active
instance. Portal transfers expressed only as byte maps target default
open-world instances; a future admission service must choose dungeon and
battlefield instance identities explicitly.

World-entry monster bootstrap follows a registered session's exact instance.
The original byte-map overload remains the explicit compatibility path for
initial/default Tempest entry; two same-content instances never share a
monster runtime.

## Single-owner mailbox

[BoundedSingleOwnerMailbox.cs](../src/Godswar.Server/Application/WorldInstances/BoundedSingleOwnerMailbox.cs)
and its
[contracts](../src/Godswar.Server/Application/WorldInstances/BoundedSingleOwnerMailbox.Contracts.cs)
provide a process-local FIFO owner boundary with:

- a hard capacity covering active plus queued work;
- explicit `Accepted`, `Overloaded`, `Draining`, and `Stopped` admission
  outcomes;
- one synchronous command executing at a time;
- typed caller completion and command-fault isolation;
- reentrant inline invocation for the current owner, without a
  cross-mailbox synchronous wait;
- rejection of `Task`-returning commands so asynchronous I/O cannot
  accidentally execute inside the owner command;
- bounded graceful drain and fail-safe forced shutdown; and
- finite accounting for depth, high-water depth, accepted/rejected work,
  command/worker faults, and abandoned work.

The mailbox owns mutable `MapInstance` access for membership snapshots and
changes, NPC catalog revision changes/checks, monster initialization,
ticks, damage, stun, aggro, and related snapshots. Socket sends happen only
after the owner returns immutable/snapshotted work. PostgreSQL commands and
checkpoint workers retain their existing application boundaries and never
run inside this mailbox.

This does not yet replace every synchronization primitive in the game.
Per-player durable mutation/fencing, status timers, asynchronous monster
viewer leases, and transport ingress retain their existing bounded
coordination. B18B establishes the world-instance owner boundary; later
incremental work may move additional safe synchronous map operations behind
it without putting I/O on the owner.

## Fanout and monster processing

[GameSessionRegistry.MonsterWorld.cs](../src/Godswar.Server/Game/GameSessionRegistry.MonsterWorld.cs)
ticks every local runtime independently through its owner. The owner returns
tick updates and a recipient snapshot. Player damage processing and packet
delivery then happen outside the owner.

Monster egress is arranged round-robin across instance recipient lists and
processed with bounded parallelism. General instance broadcast likewise
snapshots ready sessions through the owner and performs bounded asynchronous
socket sends afterward. A slow or disconnected client therefore does not
hold the map owner while network I/O completes.

Every generic fanout and monster attack send revalidates the captured
`WorldInstanceId` and `WorldRevision` immediately before transport egress. A
recipient that transferred after the owner snapshot—even one that returned
to the same instance with a newer revision—is skipped rather than receiving
stale source-instance packets.

NPC catalog snapshots in
[NpcCatalog.cs](../src/Godswar.Server/Game/NpcCatalog.cs) now carry
`WorldInstanceId`. Publication gates are instance-scoped, subscribers are
validated against the exact route, and callback delivery happens after the
owner command returns.

## Configuration

[WorldInstanceRuntimeOptions.cs](../src/Godswar.Server/WorldInstanceRuntimeOptions.cs)
adds validated process-local limits:

| Setting | Default |
| --- | ---: |
| Maximum live runtimes | 256 |
| Maximum character assignments | 4,096 |
| Maximum retired instance IDs | 65,536 |
| Default open-world player capacity | 512 |
| Per-instance mailbox capacity | 1,024 |
| Owner invocation timeout | 1 second |
| Shutdown drain timeout | 5 seconds |
| Maximum fanout concurrency | 8 |

The same values are explicit in `appsettings.json` and
`appsettings.docker.json`, and every setting has a
`GODSWAR_WORLD_INSTANCE_*` environment override. Invalid bounds fail
configuration validation instead of silently creating unbounded work.

[Program.cs](../src/Godswar.Server/Program.cs) stops producers before
disposing the registry, which gives every created runtime a finite mailbox
shutdown opportunity.

## Verification coverage

Focused managed checks cover:

- [WorldInstanceRuntimeOptionsChecks.cs](../tests/Godswar.Server.ProtocolChecks/WorldInstanceRuntimeOptionsChecks.cs):
  defaults, bounds, JSON binding, environment overrides, and fail-closed
  invalid configuration;
- [BoundedSingleOwnerMailboxChecks.cs](../tests/Godswar.Server.ProtocolChecks/BoundedSingleOwnerMailboxChecks.cs):
  single execution, FIFO, capacity, fault isolation, drain, reentrancy,
  forced shutdown, accounting, and async-command rejection;
- [WorldInstanceRuntimeDirectoryChecks.cs](../tests/Godswar.Server.ProtocolChecks/WorldInstanceRuntimeDirectoryChecks.cs):
  concurrent default creation, stable legacy projection, repeated-instance
  isolation, capacity, lifecycle, and disposal; and
- [WorldInstanceSessionRoutingChecks.cs](../tests/Godswar.Server.ProtocolChecks/WorldInstanceSessionRoutingChecks.cs):
  two same-map dungeons, default-map isolation, exact broadcasts, explicit
  monster bootstrap, transfer, hydration visibility, removal/rejoin, and
  settled owner accounting; and
- [WorldInstanceEgressRevalidationChecks.cs](../tests/Godswar.Server.ProtocolChecks/WorldInstanceEgressRevalidationChecks.cs):
  deterministic blocked fanout with a same-map transfer round trip, proving
  that a stale captured revision cannot receive the source packet.

These checks are registered in the normal managed architecture/protocol
catalog. The full solution build, full managed catalog, and mandatory B03
PostgreSQL gate remain the release evidence; their exact final counts belong
to the carrying commit/test transcript rather than being guessed here.

## Explicit non-claims

B18B does **not** implement:

- Redis packages, configuration, containers, leases, routes, or presence;
- a second process, gateway/worker backhaul, remote placement, remote
  transfer, or cross-process reconnect;
- client-facing dungeon matchmaking/admission or battlefield scheduling;
- Pindus, Ni Mini Valley, or Lelantine match orchestration;
- Medusa Island, Atlantis, Wonderland, or Bay Under Attack admission and
  lifecycle rules;
- cross-realm character selection, loadout projection, result settlement,
  or rewards;
- durable instance admission/result records;
- map IDs above `255` in the original client; or
- a universal character mailbox or replacement of all existing
  per-feature concurrency controls.

Redis remains approved by ADR 0004 only when a runnable second process
exercises the coordination boundary. Local routing is not represented as
distributed routing, and mailbox counters are not yet exported as a
production metrics surface.

## Rollback and next milestone

B18B adds no database migration and no durable player-data representation.
An application rollback must still use a binary compatible with the already
applied B18A schema migration. The compatibility behavior is Tempest's
single default open-world instance per byte map.

The next scale-out proof should be a runnable local gateway/worker split
using these identities and ownership contracts. Instance admission,
capacity, drain, reconnect, explicit transfer tickets, and operational
metrics must be defined as part of that boundary; product-specific dungeon
matchmaking and battlefield schedules can then build on it. Only after the
second process exercises shared coordination should B17 introduce Redis and
two-process route/lease failure tests.
