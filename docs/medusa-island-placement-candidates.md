# Medusa Island client-block-table placement candidates

This is an authored reconstruction, not recovered original-server spawn data.
It assigns all 73 approved roster identities to deterministic positions on the
stock map-200/map-204 geometry used by the live instance bootstrap.

## Client evidence

The two difficulty scenes are byte-identical:

| Evidence | Map 200 | Map 204 | Result |
| --- | --- | --- | --- |
| HMP | `Map/Medusa_Island.hmp` | `Map/Medusa_Island2.hmp` | SHA-256 `2519287645950257306D055B70571B40EA7143A0A051EC77CE027A105EC9B598` |
| Minimap | `MinMap/Medusa_Island.gwo` | `MinMap/Medusa_Island2.gwo` | SHA-256 `C4207D34498E564DEAF465432A625C23F1B19BA5C5C4A9D0017671F10AC38D41` |

The HMP header reports 128-by-128 terrain cells of 4 world units, giving a
centered 512-by-512 envelope. The minimap is a 512-by-512 DXT3 DDS. The
installed client's `0x0059CD20`/`0x0059CBC0` projection truncates the
coordinates, applies quadrant scales, then rotates them by 45 degrees:

```text
x = truncate(worldX)
z = truncate(worldZ)
scaledX = x * (0.58 when x > 0 and z < 0; otherwise 0.57)
scaledZ = z * (0.54 when z >= 0; otherwise 0.57)
pixelX = truncate(256 + (scaledX + scaledZ) / sqrt(2))
pixelY = truncate(256 - (-scaledX + scaledZ) / sqrt(2))
```

### Client HMP block table

All 63 installed HMP files contain exactly one 4,194,304-byte plane immediately
before the recurring `04 00 00 00 00 00 7C C3` marker. Every byte in each
plane is either 0 or 1. This structural test succeeds on 63/63 maps, including
the `Parnitha_1` root-record edge case.

For both Medusa assets the plane is `[356684,4550988)`, is 2048 by 2048
row-major cells, and has SHA-256
`A13395AB9CF89AB3C2B3AF3DFA2DE607574404F9BEA192B29644482AA962419F`.
It contains 1,239,620 zero cells and 2,954,684 one cells. The validated world
projection is:

```text
blockX = floor((worldX + 256) * 4)
blockY = floor((256 - worldZ) * 4)
```

Across 31 other scenes, all 381 parseable `Address.ini` coordinates sample
value 0 with this transform. Seven alternate axis/flip/transpose transforms
miss between 52 and 95 of those known address points. Of 44 `SpanMap` trigger
coordinates, 27 sample 0; the other 17 are commonly authored on blocked map
boundaries. Static obstacle cores frequently sample 1, and the Medusa plane's
zero-cell connectivity follows the visible island gaps.

The installed `Origin.exe` consumer is now recovered and pinned in
[`medusa-island-client-terrain-audit.md`](medusa-island-client-terrain-audit.md).
`CTerrain::LoadBlockData` reads this exact 4 MiB plane, and the active
`CTerrain::is_block(float,float)` movement query returns false for byte 0 and
true for nonzero bytes. Three actor-movement call sites enforce that result.
Consequently the code uses `ClientBlockTableUnblocked` for these points.

That evidence proves the client's block-table result, not arbitrary
static-mesh clearance, actor acceptance, or a valid teleport trigger. Some
zero regions can coincide with water/outside artwork, so intended component
membership and local clearance remain mandatory.

## Component and clearance audit

Four-neighbour connectivity identifies three relevant zero-cell components:

| Gameplay component | Zero cells | Static landmarks | Assigned enemies |
| --- | ---: | --- | ---: |
| First | 1,023,348 | gate 2 and ring 3 | 52 |
| Second | 132,860 | ring 2 and ring 1 | 15 |
| Final | 81,872 | across the blocked ring-1 gap | 6 |

All 73 authored enemy points sample value 0 in the client-consumed table and
belong to the component named by their roster island. Every point is at least
4 world units from the nearest value-1 cell; the measured minimum is 4.000
world units. The 4-unit floor
covers the largest 3.5-unit `Range` field in the Medusa client monster file
plus a 0.5-unit authorship margin. It is a conservative placement rule, not a
claim that `Range` is the client's collision radius.

The minimum distance between any two of the 73 points is 7.810 world units,
above the policy floor of 7.75.

## Deterministic formations

- Four-member first-stage group: elite `(0,0)`, normal 1 `(-6,-5)`, normal 2
  `(6,-5)`, normal 3 `(0,8)`.
- Three-member second-stage group: elite `(0,0)`, normal 1 `(-6,-5)`, normal 2
  `(6,-5)`.

### First component: three lanes

The rows may stagger in Z to avoid blocked cells, but each lane progresses
north and left/centre/right ordering is preserved.

| Progress row | Left / Stun center | Centre / Freeze center | Right / Bleed center |
| ---: | --- | --- | --- |
| 1 | E1 `(119,-197)` | E5 `(153.5,-159)` | E9 `(189,-125.5)` |
| 2 | E2 `(73,-165.5)` | E6 `(119.5,-121.5)` | E10 `(166,-74.5)` |
| 3 | E3 `(36.5,-131.5)` | E7 `(83,-84.5)` | E11 `(129,-37)` |
| 4 | E4 `(-2.5,-92)` | E8 `(44,-44.5)` | E12 `(81,-0.5)` |

| Spawn | Position | Purpose |
| --- | --- | --- |
| E14 elite | `(-17.5,-49.5)` | Inside guard for Euryale |
| Euryale | `(-46,-65.75)` | Top-left first-component boss |
| E15 elite | `(49.5,18.25)` | Inside guard for Chrysaor |
| Chrysaor | `(65.75,48.5)` | Top-right first-component boss |

### Second component: five mixed groups

| Group | Center | Component anchor |
| --- | --- | --- |
| E13 | `(-100,90)` | East side of the ring-2 landing shelf |
| E16 | `(-135,90)` | West side of the ring-2 landing shelf |
| E17 | `(-149,111)` | Westward middle rise |
| E18 | `(-115,120)` | East branch toward ring 1 |
| E19 | `(-105,145)` | Ring-1 approach |

Each center expands with the three-member formation. E13 remains the literal
Elite Cyclops Swordsman group.

### Final component

| Spawn | Position | Cluster |
| --- | --- | --- |
| Stheno | `(-160,175)` | West/physical core |
| Pikeman 1 | `(-175,155)` | Lower-west physical amplifier |
| Pikeman 2 | `(-172,186)` | Upper-west physical amplifier |
| Medusa | `(-125,185)` | East/magical core |
| Axeman 1 | `(-114,175)` | Lower-east magical amplifier |
| Axeman 2 | `(-105,195)` | Upper-east magical amplifier |

## Entry and transfer hard points

Static-model centers are not used as player landings. All five candidates
below are value 0 in the client-consumed block table, have at least 4 units of
local blocked-cell clearance, and are on the stated component.

| Candidate | Position | Component | Client landmark | Status |
| --- | --- | --- | --- | --- |
| Instance entry | `(210,-220)` | First | gate 2 `(221.27,-224.58)` | Packet binding unknown |
| First transfer source | `(-33,51)` | First | ring 3 `(-34.26,53.53)` | Trigger/direction unknown |
| First transfer destination | `(-80,98)` | Second | ring 2 `(-77.43,95.33)` | Trigger/direction unknown |
| Second transfer source | `(-128,139)` | Second | ring 1 `(-130.75,140.66)` | Trigger/direction unknown |
| Final transfer destination | `(-146,144)` | Final | no matching ring recovered | Inferred across blocked gap |

The landmarks make the likely progression gate 2 -> ring 3/ring 2 -> ring 1
-> final component coherent. Their HMP records are render transforms with no
trigger radius, direction, condition, or destination binding. The client
scene-change handler instead consumes server-supplied X/Y/Z. CTerrain projects
Y=0, but the original arrival Y, facing, and safe player landing are unknown.
Completion likely needs no map exit because it is encounter-driven, but that
also remains a runtime policy choice.

## Activation gate

`TryResolveLiveCertified` still returns false for every spawn and difficulty;
the live monster bootstrap uses the narrower block-table-gated server spawn
resolver. Traversal remains separately uncertified. Full acceptance requires
static-mesh/actor acceptance on maps 200 and 204, player reachability, all five
trigger direction/destination bindings, arrival facing, safe aggro distances,
and `ClientPlacementAccepted` evidence. All five traversal anchors remain
trigger-uncertified.
