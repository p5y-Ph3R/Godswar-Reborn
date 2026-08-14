# Remote Player Equipment Inspection and Ceiling Register

Status: implemented and packet-tested on 2026-07-18. This document covers the
`PlayerInspectEquipment` response (`10022` / `0x2726`) used when one player opens
another player's equipment window. Monster visibility is outside this scope.

The [ceiling register and required regression matrix](player-inspection-equipment-ceiling-register.md)
continue in a companion document.

## Why the inspected equipment was incomplete

The database and outgoing item bodies already contained both rings, all five
append-attribute IDs, holy-suit code `710`, and four holy stones for character 1
(`KREI_ARLOTT_KING`, account 7). The viewer was character 7 (`JAZZZ2`, account
347). The failures were in the server's projection of packet `10022`:

1. It wrote each item into a record matching its source slot and left the trailing
   source-slot mask at zero. The client expects non-empty records packed from the
   start, then uses the mask to associate those records with equipment slots.
2. It reused fabricated record-index identities for every inspected player. Those
   identities collide in the client's item cache and can leave stale or missing
   detail metadata.
3. It deliberately projected detailed-inspection quality and grade down to Q10 and
   G12, even though these two fields are independent full bytes in this packet.

The server now packs records, writes the source-slot mask, preserves the detailed
Q20/G25 values, and generates identities that are stable for the character/slot
and change when item metadata changes. No game-client file change is required for
this inspection fix; the local patched client already parses the fields described
below.

## Captured `10022` layout

Packet length is exactly 1,524 bytes:

| Packet offset | Size | Meaning |
| --- | ---: | --- |
| `0` | 2 | packet length, `1524` |
| `2` | 2 | opcode, `10022` / `0x2726` |
| `4` | 4 | inspected player's current world object ID |
| `8` | 1,512 | capacity for 21 packed item records, 72 bytes each |
| `1520` | 4 | source equipment-slot bitmask, bits `0..20` |

Only non-empty items are emitted as records, in ascending source-slot order. Empty
capacity follows the packed records. The set bits in the final mask reconstruct
the original slots. For example:

- Working full capture: slots `0..10` and `15..20`, mask `0x001F87FF`.
- Working sparse capture: slots `2,3,6,10,20`, five packed records, mask
  `0x0010044C`.
- Current two-ring regression fixture: slots `0,8,9`, mask `0x00000301`.

Each 72-byte item record has this layout:

| Record offset | Size | Meaning |
| --- | ---: | --- |
| `+0` | 4 | item template/property ID |
| `+4..+20` | 5 x 4 | five append-attribute IDs |
| `+24` | 1 | quality |
| `+25` | 1 | grade |
| `+26` | 1 | bound flag |
| `+27` | 1 | stack count |
| `+28` | 4 | item experience |
| `+32` | 2 | holy-suit code, such as `710` |
| `+34` | 2 | active holy-stone socket count |
| `+36..+42` | 4 x 2 | four holy-stone effect/level codes |
| `+44..+50` | 4 x 2 | four holy-stone display values |
| `+52..+63` | 12 | zero/reserved in retained captures |
| `+64` | 4 | stable item-state/cache identity |
| `+68` | 4 | stable physical/slot identity |

Captured empty records use `-1` for the item, attributes, and both identities;
quality `1`, grade `1`, bound `0`, stack `1`; and zero extension data.

The meanings of the two tail identities are inferred from captures. Working-server
responses preserve the pair for an unchanged physical item across sessions and
use distinct pairs for two rings with the same template ID. The current server
uses a deterministic state hash at `+64` and character/source-slot identity at
`+68`. The `character_items` table already has a persistent `bigint` row ID. If
that ID is adopted as a protocol identity later, propagate it through
`character_item_compact_entries` and `CompactItemEntry`, then replace this
deterministic approximation while preserving the stability/uniqueness tests.

## Working-capture evidence

Eight retained working inspection responses were examined. The most useful
examples are:

- `captures/working-multiplayer-20260514-193356.log`, lines `3719..3738`: request
  `10191`, followed by packed `10022` plus `10166`. Both item-3206 rings have
  distinct identity pairs. The head item includes five attributes, holy suit
  `301`, and two holy stones.
- `captures/working-visuals-20260519-163239.log`, lines `1940..1955`: the same
  sequence with an optional `10098` profile/context packet.
- `captures/capture-proxy-20260514-173331.log`, line `107787`: sparse `10022`
  with five packed records and mask `0x0010044C`.

The retained captures often show `10278` after `10022` and `10166`, but client
disassembly establishes that `10278` updates the locally carried pet's current
energy; it is not an inspection-completion marker. The timing was incidental to
the original server's roughly six-second energy refresh. Inspection therefore
sends only its equipment/status bundle. Packet `10098` appeared in only one of
eight retained sequences and is not required for rendering the inspected
equipment. The server should not prepend its currently mostly-empty
`PlayerInspectProfile` builder unless that packet is decoded first.

All retained original-server inspect items are at or below Q10/G12. Therefore the
capture corpus proves the packet shape and full-byte fields, but it is not evidence
of original-server Q20/G25 behavior. Q20/G25 is a local patched-client extension
and must be retained in the local regression matrix.

## Remote world full-quality/grade extension

Remote avatar rank effects are calculated from opcode `10021` (`0x2725`), not from
the detailed inspection packet. The native packet stores each compact item's
quality and grade in one byte at offset `81+i`: quality is the low nibble and grade
is the high nibble. Projecting account 7's Q20/G25 equipment to the captured
native fallback of Q13/G12 produces armor score `7753` and AR9, while the full equipment
produces score `25350` and AR14.

The local protocol keeps the native fields as a legacy fallback and extends the
packet from 260 to 300 bytes:

| Offset | Size | Meaning |
| --- | ---: | --- |
| `260` | 4 | Little-endian marker `0x31585747` (ASCII `GWX1`) |
| `264` | 18 | Full-byte quality values in the same compact order as IDs at offset `124` |
| `282` | 18 | Full-byte grade values in the same compact order |

`tools/PatchRemoteWorldEquipmentExtension.ps1` patches the tracked local
`Origin.exe` decoder at VA `0x004731A5` (file offset `0x731A5`). The replacement
code is in reserved executable `.rdata` slack at VA `0x009C3270` (file offset
`0x5C3270`). It reads the appended values only when the declared packet length is
at least 300 and
the `GWX1` marker matches. Native 260-byte packets and other servers continue
through the original nibble decoder.

The patched values then flow through the client's existing single-item and
aggregate score routines. The current local executable accepts Q20/G25 there;
the extension itself can carry `0..255` in each field. Raising the practical
ceiling still requires longer client template arrays and updated score-routine
bounds, but no new world wire layout through Q255/G255.

Continue with the [ceiling register and required regression matrix](player-inspection-equipment-ceiling-register.md).
