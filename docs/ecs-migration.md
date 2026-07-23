# ECS server migration

## Legacy and ECS monster runtimes

`MonsterMapRuntime` is a deterministic object-oriented state machine. It is
functionally similar to the supplied `MonsterSystem` example:

| Supplied example | Current runtime |
| --- | --- |
| `Update(monster, deltaTime)` | `MonsterMapRuntime.Advance(now, targets)` |
| Idle / Walking | `NextMovementAt`, `IsMoving`, `StartMovement` |
| Following / Attacking | `AggroCharacterId`, `MonsterCombatPhase` |
| Returning | `BeginReturnHome`, `AdvanceReturnHome` |
| Dead / Reviving | `IsAlive`, `IsSpawned`, `DespawnAt`, `RespawnAt` |
| Visible players | `MapInstance` AOI viewer state |
| Network broadcast | `GameSessionRegistry` replication |

The supplied example is not ECS. It passes one rich `MonsterModel` to a system
and mixes simulation with network broadcasting. The legacy runtime likewise
owns all monster state in one private `MonsterRuntimeState`.

It also contains several behaviors that must not be copied into the live
runtime:

- wall-clock reads and one shared unseeded `Random` make replay nondeterministic;
- `MoveTowards` can normalize a zero-length vector;
- the return sync branch resets `WalkSyncTimer` instead of
  `ReturnSyncTimer`;
- `SendSpawnMonster` and the return branch construct spawn packets without
  publishing them;
- scan/revive timers mix millisecond deltas with second-like thresholds;
- combat contains hard-coded or unused damage and incomplete send paths; and
- AI, combat, visibility, packet construction, and broadcasting are coupled
  into one class.

`EcsMonsterMapRuntime` is the compatible ECS implementation. It hydrates typed
components into `EcsWorld`, runs them through the deterministic scheduler, and
returns the same immutable runtime DTOs. `MapInstance` consumes
`IMonsterMapRuntime`, so AOI, broadcasts, packet DTOs, and packet order remain
shared rather than being duplicated by the ECS runtime.

The existing behavior must not be replaced by the example verbatim. The
current runtime already provides important guarantees:

- deterministic per-monster random state;
- fixed 12 Hz simulation;
- exact 0.38-unit movement steps and destination clamping;
- passive retaliation rather than unconditional proximity aggro;
- spawn-generation and health-revision ordering;
- invulnerable return-to-home followed by a full-health replacement;
- network replication outside the simulation.

## Target runtime

Each map is a single-writer world shard:

```text
WorldShard
|- EntityRegistry (opaque index + generation IDs)
|- ComponentPool<T>
|- SpatialIndex
|- SystemScheduler
|- WorldCommandBuffer
`- WorldEventBuffer
```

Socket tasks enqueue commands. Only the map loop mutates ECS components.
Systems emit immutable events; a protocol adapter translates those events to
client packets after the simulation tick.

Database connections, client sockets, packet buffers, and `IGameStore` are
resources/adapters, not ECS components.

## Per-map player and NPC runtime

`MapInstance` uses `MapEcsShadow` (the historical migration name) as the
authoritative gameplay-entity store when `PlayerRuntimeMode` is `Ecs`. The
session dictionary remains a transport lookup because sockets and mutable
session contexts are intentionally not ECS components. In `Legacy` rollback
mode, the same store runs as a parity shadow. The ECS store:

- maps each live session and world object ID to a generation-checked entity;
- creates a complete replacement entity before swapping an updated player;
- mirrors the `WorldReady` transition as map-presence data;
- destroys the current entity on map leave, making old handles stale;
- hydrates the finalized per-map NPC definitions after validation and returns
  the canonical ECS projection used by NPC visibility and packet production;
- preserves entity handles when repeated NPC definitions are identical; and
- exposes immutable snapshots and live-versus-shadow parity diagnostics.

Player packet projection still uses the transport context associated with an
ECS member; database stores and sockets remain boundary adapters. Status
effects use a neutral placeholder in the map-membership projection because
their authoritative ECS lifecycle is owned separately by the player policy
runtime.

## Reversible player-policy cutover

`GameSessionRegistry` accepts a separate `PlayerRuntimeMode` selector. The
code, JSON, Docker, and environment defaults are `Ecs`; `Legacy` remains an
explicit restart-level rollback.

In `Ecs` mode, typed ECS systems are authoritative for:

- the monotonic recovery clock and six-second recovery decision;
- dead/full-vitals suppression and delayed-poll single-pulse behavior;
- runtime-status expiry and complete status-snapshot composition; and
- mount movement aggregation, including the local 10166 multiplier;
- accepted player movement projection and movement revisions;
- basic, single-target skill, and area-skill combat intent validation,
  cooldowns, MP reservations/refunds, and damage intents; and
- monster-to-player damage application, duplicate/stale-event rejection,
  vitals revisions, and lethal decisions.

The registry remains the boundary adapter. It copies current character values
into ECS, applies emitted decisions while holding the existing character
synchronization lock, sends packets in the existing order, and performs the
same best-effort vitals save. No socket, packet buffer, store, or mutable
`GameCharacter` is retained in a component or system.

Session removal destroys the policy adapter. Rejoining with the same socket
therefore starts a fresh recovery interval. A new life revision also resets
recovery to six seconds, while a preserved status dictionary is rehydrated
into a fresh status ECS host.

Progression-boost and Zodiac persistence remain legacy-authoritative in this
stage. ECS observes an online-duration event only after the corresponding
store transaction succeeds. A failed save leaves both the durable watermark
and ECS diagnostic watermark unchanged; reconnects seed from their new online
time, so offline gaps are never consumed. Moving those writes into ECS
requires a durable outbox or equivalent transaction boundary and is a later
cutover, not an in-memory approximation.

## Player movement and combat boundaries

Inbound walk packets are decoded by the protocol adapter and projected through
typed movement identity, transform, and intent components. An accepted ECS
decision is then applied in the existing order: character mutation and map
registry update, NPC/monster AOI reconciliation, throttled persistence, and
world broadcast. Rejected intents have no side effects. The first inbound
movement word remains opaque because no capture proves that it contains the
player object ID; the live adapter does not invent a source-ID check.

Outgoing player combat uses ECS for basic attacks and hostile single/area
skills. Systems validate identity, life and spawn generations, cooldowns, MP,
and target state, then emit immutable damage intents. The boundary adapter
applies those intents through the existing monster mutation API using health
revision guards. Packet order, AOI delivery, rewards, and committed
progression remain compatible boundary work. Stun is intentionally retained
as the existing control-only path until the ECS combat contract owns
non-damage control intents.

Incoming monster damage uses the shared player identity and vitals
components. Holy Ward and defense are resolved before the ECS boundary; ECS
then rejects duplicate/stale attacks, clamps health, advances vitals/life
revisions, and emits the lethal or nonlethal result. The adapter preserves the
existing impact, damage, death, mount-clear, aggro, and persistence order.

The ECS movement system does not add an unproven speed or teleport
anti-cheat rule. Mount speed is synchronized through the native local
`10166` locomotion multiplier; every later local status refresh composes the
active Ride multiplier instead of resetting it to `1.0`.

## Reversible monster cutover

The live default is the parity-gated ECS runtime. Select the implementation
with the JSON key:

```json
{
  "game": {
    "monsters": {
      "runtime": "Ecs"
    }
  }
}
```

Accepted values are `Legacy` and `Ecs` (case-insensitive). The environment
override is `GODSWAR_MONSTER_RUNTIME=Ecs`; use
`GODSWAR_MONSTER_RUNTIME=Legacy` for an explicit rollback. Invalid values stop
startup instead of silently choosing an implementation.

Changing the value requires a server restart. Existing maps then construct
only the selected runtime; there is no dual execution and therefore no
duplicate network broadcast. Rollback is the same one-value change back to
`Legacy`.

Player recovery and status composition use the ECS runtime by default through:

```json
{
  "game": {
    "players": {
      "runtime": "Ecs"
    }
  }
}
```

Use `GODSWAR_PLAYER_RUNTIME=Legacy` for the reversible recovery/status
rollback. The same selector reverses map player/NPC membership, movement,
player combat, incoming monster damage, the six-second recovery clock, status
expiration, full status composition, Ride multiplier, and lifecycle resets.
Sockets, packet translation, and persistence remain boundary adapters.
Progression-boost and Zodiac database transactions remain durable authorities;
their successfully committed online intervals are mirrored as ECS events so a
failed save or offline gap cannot advance a runtime watermark.

## Initial components

Core:

- `WorldObjectIdentity`
- `MapMembership`
- `Transform`
- `MovementIntent`
- `SpawnGeneration`
- `NetworkAppearance`

Monster:

- `MonsterDefinition`
- `SpawnPoint`
- `Vitals`
- `Patrol`
- `AggroPolicy` (`Passive`, `Defensive`, or `Aggressive`)
- `AggroTarget`
- `MonsterAiState`
- `AttackCooldown`
- `Stun`
- `Lifecycle`
- `HealthRevision`
- `WorldBoss`

Player:

- `PlayerIdentity`
- `PlayerClass`
- `Camp`
- `Vitals`
- `CalculatedStats`
- `StatusEffects`
- `Cooldowns`
- `MountState`
- `Progression`
- `OnlineDuration`
- `InventoryRevision`

NPC:

- `NpcIdentity`
- `NpcFunction`
- `NpcDialog`

## Fixed tick order

1. Apply queued commands.
2. Expire timed status effects and stuns.
3. Update spatial cells.
4. Acquire retaliation/proximity targets according to `AggroPolicy`.
5. Decide patrol, chase, attack, return, and lifecycle transitions.
6. Move entities.
7. Resolve scheduled attacks.
8. Resolve deaths, corpse expiry, return retirement, and respawns.
9. Calculate AOI changes.
10. Publish an immutable ordered event batch.
11. Translate events into protocol packets.
12. Queue durable persistence work.

## Cutover status

Completed and defaulted to ECS behind restart-level rollback selectors:

1. Generation-safe entity registry, sparse component pools, deterministic
   scheduler, command buffer, and immutable event buffer.
2. Monster patrol, retaliation, chase, attack scheduling, stun, leash return,
   death, corpse expiry, and full-health replacement.
3. Per-map player membership and canonical NPC hydration/projection.
4. Player status expiry/composition, recovery, Ride aggregation, movement,
   outgoing combat, and incoming monster damage.
5. Parity and live-adapter tests for lifecycle resets, stale generations,
   health revisions, duplicate attacks, disconnect races, failed cross-map
   transfers, live NPC revisions/object-ID collisions, persistence failures,
   and Legacy rollback.

Deliberately retained at boundaries:

1. Socket ownership, packet decoding/encoding, AOI delivery, and protocol
   ordering.
2. Inventory, forging, Gear Mentor, and progression database transactions.
3. Zodiac and online-boost durable clocks, mirrored into ECS only after a
   successful commit.
4. The Legacy monster/player implementations until sustained live parity makes
   their removal safe.

## Non-negotiable parity gates

- 12 ticks per second.
- 0.38 movement step and 8-unit patrol radius.
- Passive retaliation unless a definition explicitly selects another policy.
- 3-unit attack range and 21-tick cooldown.
- 32-unit combat leash.
- Returning monsters reject damage and stun.
- Movement-end event precedes returned-monster retirement.
- Return creates a full-health replacement with a newer spawn generation.
- 5-second corpse despawn and 10-second normal respawn.
- 12-hour world-boss respawn.
- Old-generation packets and inverse health revisions are suppressed.
- Existing AOI and packet order is unchanged.
- Online-only boost and zodiac clocks remain online-only.
- Forging and Gear Mentor remain atomic server-side transactions.
- Every protocol and PostgreSQL integration check remains green.

## Database migration rules

- Take a physical/logical backup and invariant manifest before cleanup.
- Apply migrations under a PostgreSQL advisory lock.
- Record version, name, SHA-256 checksum, and application time.
- Use one transaction per migration and reject checksum drift.
- Never replay historical test-character fixture scripts in production.
- One source owns schema; catalog seed data is a separate concern.
- Reconcile orphan data before adding foreign keys.
- Dual-read/compare before removing compatibility views or columns.
- Packet research captures use a separate retention policy.
