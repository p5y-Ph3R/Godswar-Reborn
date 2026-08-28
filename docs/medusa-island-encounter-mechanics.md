# Medusa Island encounter mechanics

The encounter-effect layer is process-local. Exact bound ECS monster attacks
commit player HP and record all authored effects in the instance owner.
Exact-life server gates consume recorded stun, freeze, and
Shackle for legacy and secure-realtime movement, common basic attacks, initial
and pending-completion skill casts, and item activation. The effects are also
projected as complete player status packet 10167 snapshots through exact
target and same-instance observer routes. Applying a control interrupts an
already pending cast with an intentional duplicate-status FIFO. Self delivery
is impact, damage, the initial committed application 10167 (plus an initial
self-only 10166 when applicable), then one atomic corrective current 10167 plus
self-only 10166 immediately followed by exactly one 10171. Observer delivery
omits 10166: impact, damage, initial 10167, then an atomic corrective current
10167 plus exactly one 10171. The initial snapshot does not wait behind
cast-start publication; refresh, expiry, or run termination can make the
corrective snapshot different or empty. A committed Bleed instead installs a
server-only periodic effect after its direct hit; it never emits native status
18 or skill 2041. Legacy bound Medusa attacks still fail closed. The typed
outgoing-damage preview is consumed by the owner-bound player-to-monster
transaction.

## Authored effects

- Stun (`2002/330`) blocks all actions for 2 seconds.
- Freeze (`2018/402`) blocks all actions for 3 seconds.
- Euryale Shackle (`2017/401`) blocks all actions for 3 seconds.
- Bleed (`2041/18`) lasts 15 seconds. Status 18 supplies `Effect=27`
  (`DecHP`), `Values=200`, and `Interval=2`. It is therefore authored as
  direct health loss, not physical or magical damage: 200 at +2, +4, +6, +8,
  +10, +12, and +14 seconds. There is no immediate or expiration tick.
- A Gorgon Pikeman committed hit grants 10x outgoing physical damage for 30
  seconds. A Gorgon Axeman committed hit grants 10x outgoing magical damage
  for 30 seconds. These typed effects can coexist but never cross channels.

Within the pure mechanics runtime, every accepted authored hit produces its
defined effect. The live ECS integration records all six effects. Stock
`StatusOdds` remains audit evidence and is never
interpreted as probability. Reapplying a recorded effect replaces its
source/window rather than stacking it. Effects use exclusive expiration
boundaries.

The world pump drains due Bleed work before elemental movement, monster
movement, attacks, and quiet-world skipping using the same captured time. Each
tick applies 200 direct health loss once and is completed before another tick
for that target. A nonlethal tick emits one vitals packet per exact recipient;
a lethal tick emits one atomic vitals/death pair. Observers are admitted before
self. Stale, dead, or transferred targets consume the effect without HP, while
retiring the source does not cancel an already committed Bleed. A failed
post-HP publication or persistence step is retried without repeating HP or an
already-owned packet.

## Client projection

The server owns the 10x calculation and refresh window. Native statuses 330,
402, 401, 236, and 235 are projected with exclusive-expiry durations and
refresh-safe application identities. Complete snapshots retain every
authoritative baseline candidate, then select at most 20 client icons and at
most 10 beneficial icons in deterministic priority order; presentation limits
never remove gameplay state or alter aggregate calculations. Stock
`AffectMap=209,223` for statuses 235 and 236 uses the secondary client scene
ID, where content map 200 resolves to scene 209 and content map 204 resolves
to scene 223. The runtime records the matched client scene explicitly and
still treats every effect as server-authoritative.

## Authority boundary

State is isolated per admitted character. Roster object ID plus spawn
generation authorizes a monster source, and retiring a source fences later
hits without cancelling already committed effects. Foreign characters,
unknown/stale/retired sources, non-mechanic monsters, backward UTC timestamps,
and unrepresentable effect windows reject before the runtime clock, effects,
or pending ticks can change. Snapshots and tick batches are immutable.

When attached to an instance owner, the run clock and mechanics clock are an
invariant pair. Valid committed hits, defeats, time observations, and whole-run
abandonment advance both; invalid identities advance neither. The unresolved
exact 40-minute boundary advances both clocks but applies no new effect.

Outgoing damage amplification has both snapshot and timestamp-aware pure
previews. The authoritative combat path uses the timestamp-aware form, so a
stored amplifier is ignored at its exclusive 30-second expiration even when
no clock consumer has evicted it. Neither preview expires state or emits or
consumes a bleed tick.

The bound player-to-monster transaction holds the Medusa owner and monster
runtime gates together. It validates admitted character, explicit instance
difficulty, roster role, object generation, health revision, and run time;
then applies the matching 10x amplifier followed by the boss's final 10%
wrong-channel multiplier before committing HP. Lethal damage is preflighted
and claimed by the run before the gates are released. Due Bleed work is drained
by the world pump before later encounter actions; an operation that reaches the
owner while such work is still pending fails closed before HP changes.
Channelless raw, rebound, and periodic monster damage also fail closed
on an owned Medusa runtime. Rebound and Gaia reflection retain the exact
source world-instance revision and monster-runtime identity; a stale same-map
transfer cannot redirect either secondary to another runtime. Player Burn is
rejected before its status ledger changes because its periodic ticks do not
yet have a typed Medusa handoff. The inverse ECS path now commits exact
monster-to-player HP and records every authored owner effect. Exact current
membership, ownership, life, and owner availability are required before the
server action gates permit a command; ordinary unbound maps retain their prior
behavior. The server never publishes these effects through map-only or stale
routes: exact complete 10167 snapshots require the current world, ownership,
object, and life. Control application publishes a corrective current snapshot
immediately before one exact pending-cast interruption, after the separate
initial committed application snapshot described above. Bleed is installed in
server state but intentionally omitted from native status projection.
