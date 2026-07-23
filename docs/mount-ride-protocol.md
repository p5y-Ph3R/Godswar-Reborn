# Mount and Ride protocol

This note records the stock-client facts used by the replacement server. It
separates captured behavior from inferred compatibility so later mount-family
work does not have to rediscover the same offsets.

## Native equipment layout

`ItemBagsExUI.xml` uses these logical source slots:

| Slot | Item kind | Stock level-40 example |
| ---: | --- | ---: |
| 15 | `mounthead` | 14500 |
| 16 | `mountarmor` | 14600 |
| 17 | `mountsoul` | 14700 |
| 18 | `mountornament` | 14800 |
| 19 | `mountamulet` | 14900 |
| 20 | `mount` | 14220 |

World spawn opcode `10021` and inspection opcode `10022` use mask bits 15
through 20 for these slots. The ordinary equipment range remains 0 through
12; slots 13 and 14 are the stock tool/create and pet positions.

## Riding skill

Skill 4904 (`Riding`) is defined with 50 MP, six seconds of intonation, a
six-second cooldown, and mount requirement 20. Book item 5802 normally grants
it at level 40. The replacement server temporarily grants skill 4904 to every
class until the original quest/book award path is implemented; equipping and
using a mount still enforces the mount's authored player level.

A captured successful activation is:

1. Client sends opcode `10040` (40 bytes), skill 4904.
2. Server echoes `10040` with `u32 @20` changed from 0 to 10.
3. About 6.2 seconds later, server sends a 612-byte bundle in this exact order:
   complete opcode `10167` status snapshot (340 bytes), opcode `10166`
   PlayerStatusUpdate (236 bytes), opcode `10046` cast completion (24 bytes),
   then opcode `10135` mana update (12 bytes).

Opcode `10167` offset 324 describes the composed movement multiplier in the
status snapshot. The client does not use that field alone for local movement.
Opcode `10166` float offset 56 updates the local `GameData` locomotion
multiplier: `1.0` while walking and, for example, `1.24` on a 24% mount.
Every later local `10166` refresh (forging, progression, talent, equipment,
inventory, NPC enhancement, and self-detail refreshes) must reuse the active
status aggregate. Sending the packet's `1.0` walking default while Ride is
active immediately cancels the client-side speed change even though the mount
model remains visible.

Using skill 4904 again while effect 33 is already active is an immediate
dismount toggle. The installed client does not send another opcode `10040` for
this reuse; it sends the 20-byte opcode `10320` player-state request with action
`6` at offset 8 (`1400502800000000060000000000000000000000`). The replacement
server responds with opcode `10167` with the Ride status removed and effect 33
cleared, followed by opcode `10166` with its movement multiplier reset to
`1.0`. This is the symmetric reset required by the activation evidence; the
retained working-server corpus does not contain a post-activation dismount
exchange. The replacement does not echo opcode `10040` or send opcode `10046`
or `10135`, so dismount has no intonation, cooldown, or MP cost. The mount
remains equipped and can be used by the next activation.

The equipped mount selects the permanent status and therefore the rendered
ride model. Existing viewers receive the updated opcode `10167`. Late viewers
need the same active status embedded in opcode `10021`: `u16 count @178`, then
consecutive `u32` status IDs from offset 180. Movement multiplier is the float
at opcode `10167` offset 324. The status ID alone does not enter the mounted
render state: StatusData effect 33 is the `u32` Riding flag at offset 328 and
must be `1` while mounted, then return to `0` on dismount. In the working
capture corpus, all 330 snapshots containing a `Ride.ini` status set this flag;
all 939 non-Ride snapshots clear it.

The six-second wait is scheduled without blocking the session packet loop.
Completion is an atomic server-side commit over character identity, life
generation, equipped mount, level, HP, MP, and the Ride status. Death invalidates
a pending cast and removes an active Ride status; mount removal/replacement is
rejected while activation is pending.

## Ride-model mapping

| Item | Level | Speed bonus | Ride status | Evidence |
| ---: | ---: | ---: | ---: | --- |
| 14220 | 40 | 20% | 1100 | captured |
| 14221 | 50 | 21% | 1101 | captured |
| 14222 | 60 | 22% | 1102 | inferred from the client sequence |
| 14223 | 70 | 23% | 1103 | captured |
| 14224 | 80 | 24% | 1104 | captured |
| 14225 | 90 | 25% | 1105 | captured |
| 14226 | 100 | 26% | 1105 | captured |
| 14227 | 110 | 27% | 1105 | inferred client-model clamp |
| 14228 | 120 | 28% | 1105 | inferred client-model clamp |
| 14229 | 120 | 50% | 1105 | inferred client-model clamp |

The server now maps every one of the 349 grantable client mount templates to a
`Ride.ini` status. This includes the five legacy six-model families, the 30
shipped modern ten-item families, the locally authored Erebus Lion family, and
the shipped timed/special entries. The malformed client entry `14429` remains
catalogued but cannot be generated or ridden.

For a modern ten-item family, IDs `base+0..base+8` are the authored level
40..120 progression and `base+9` is the separate 50%-speed special item. The
visual model sequence is shorter than the item progression for several native
families, so their last normal tiers intentionally reuse the last compatible
Ride status. Modern families with paired regular/upgraded models switch to the
upgraded status at item offset five. Atlantic Leatherback uses statuses
1201..1209, with its special item sharing 1209; the timed Leatherback uses
1210. These mappings are asserted against the client catalog so a mount cannot
be exposed by `/item mount add` without a ride definition.

Erebus Lion items `16200..16209` all map to custom status `1390` in an unused
native-range gap. Its patched `Status.ini` entry selects `Ride.ini` section
`117`; local locomotion speed is synchronized independently through opcode
`10166`, so the custom visual status does not need to impersonate a stock
mount. The custom JCS is derived
from shipped `Ride_Lion_002.jcs` (source SHA-256
`80186f8ef998296e6a37c21783dbce4746b1b0284e0a8a71027da9bac402364a`).
An `ErebusScaleRoot` parent around both top-level model hierarchies uniformly
scales X/Y/Z by `1.40`; the generated asset SHA-256 is
`1e5ae1dc596ae69a659b55dd52839fe842946353536b4258ea75f73657fb84ac`.
This makes the lion 40% larger in every dimension while preserving its native
proportions, skin weights, embedded animation data, and the stock
`_Lion_ride_stand_00` / `_Lion_ride_run_01` rider actions. Only the cloned TGA
texture is recoloured. Reinstall or verify the client patch with
`python tools/InstallErebusLionMount.py --check`.

Use `/item mount list [page|family]` to inspect the complete client catalog and
`/item mount add <id>` or `/item mount add <family> <tier>` to create one bound
Q1/G1 test mount. See `docs/developer-mount-command.md` for the aliases and
special/timed caveats.

## Authoritative mount-gear rules

The server validates `player level >= mount level >= mount-gear level`. Mount
gear cannot be equipped without a mount, and the mount cannot be removed until
all five mount-gear slots are empty. A riding mount also cannot be removed or
replaced. The native player-spawn body has only 18 equipment records, so a
fully populated 19-item layout prioritizes mount slot 20 over the non-visual
mount-amulet overflow record. Mount and mount-gear authored stats contribute to character stats,
but are excluded from the ordinary armor-rank/aura score.

Stock error strings 0700 through 0704 corroborate the dependency and level
rules. Exact Horse Feeder operation packets and local drag/drop captures for
mount gear are still unresolved; do not infer the feeder's upgrade/bind wire
format from the generic equipment packets.
