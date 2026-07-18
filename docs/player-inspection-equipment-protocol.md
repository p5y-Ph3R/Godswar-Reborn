# Remote Player Equipment Inspection and Ceiling Register

Status: implemented and packet-tested on 2026-07-18. This document covers the
`PlayerInspectEquipment` response (`10022` / `0x2726`) used when one player opens
another player's equipment window. Monster visibility is outside this scope.

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

The normal captured completion sequence is `10022`, `10166`, then `10278`.
Packet `10098` appeared in only one of eight retained sequences and is not required
for rendering the inspected equipment. The server should not prepend its currently
mostly-empty `PlayerInspectProfile` builder unless that packet is decoded first.

All retained original-server inspect items are at or below Q10/G12. Therefore the
capture corpus proves the packet shape and full-byte fields, but it is not evidence
of original-server Q20/G25 behavior. Q20/G25 is a local patched-client extension
and must be retained in the local regression matrix.

## Ceiling register

Detailed inspection and remote world appearance are different protocols. They must
not share a ceiling constant.

| Surface | Current effective ceiling | Where to change it | Important limitation |
| --- | --- | --- | --- |
| Detailed `10022` quality/grade | Q20/G25 from current DB/templates; serialized as full bytes | `PacketBuilder.PlayerInspectEquipment`, `WriteInspectItemRecord`, `WriteKitBagItemRecord`; DB/template and client sites below | Wire fields can hold `0..255`, but downstream data and client arrays are the practical ceiling |
| Remote world equipment appearance | Explicit captured projection Q10/G12 | `CapturedWorldVisualQualityCap`, `CapturedWorldVisualGradeCap`, and `PackWorldItemVisual` in `PacketBuilder.cs` | One byte stores `(grade << 4) | quality`; each half is only four bits. Values above 15 require a protocol and client redesign |
| Append attributes | Five IDs; local data through L25 | Item record offsets `+4..+20`, DB attribute templates, client `ItemAppendAttribute.xml` and patches below | Attribute levels are not separate fields in captured 72-byte records. The client combines IDs, grade, and its XML. More than five attributes needs protocol/client work |
| Holy suit | Current semantic type 7, level 10; code `710` | `CompactItemEntry.HolySuitType/HolySuitLevel`, `WriteItemExtension`, embedded schema, migrations `003` and `015`, tier/requirement data, and client holy-suit data | Wire code is a signed 16-bit value. New semantic tiers/levels need client lookup/effect discovery as well as larger server tables |
| Holy stones | Four sockets, effects at levels 1..10 | `NativeClientHolyStoneSocketCount`, `CompactItemEntry.MaxSockets`, `HolyStoneItemMutator.MaxSockets`, `WriteHolyStoneValueRows`, `HolyStoneEffectCode`, and value tables | Captured/native record has exactly four effect/value pairs. DB columns 5/6 are dormant; six sockets need the reference client patches and a revised packet model |

### Server and database surfaces

When raising quality, grade, attribute, holy-suit, or stone ceilings, audit all of
these together:

- `src/Godswar.Server/Packets/PacketBuilder.cs`
  - detailed inspection packing, identities, and full-byte serialization;
  - `PackWorldItemVisual` and its separate nibble projection;
  - `WriteItemExtension`, socket count, effect-code level clamp, and stone-value
    lookup arrays;
  - other full-byte item paths (`WriteEnterItemRecord`, kitbag, and item snapshots).
- `src/Godswar.Server/State/CompactItemEntry.cs`
  - parsed compact-field types, holy-suit semantic clamps, and four-socket output.
- `src/Godswar.Server/State/HolyStoneItemMutator.cs`
  - four-socket mutation loops and limits.
- `src/Godswar.Server/State/PostgresGameStore.cs`
  - the embedded `character_item_compact_entries` schema clamps quality to the
    template `BaseFraction` length and grade to both `AppFraction` length and the
    literal `25`;
  - generated loadout, validation, rank/stat, holy-suit, and stone views.
- SQL definitions that must remain aligned with the embedded schema:
  - `database/postgres/003_item_quality.sql`
  - `database/postgres/004_equipment_scores.sql`
  - `database/postgres/005_item_attributes.sql`
  - `database/postgres/015_holy_suit.sql`
  - `database/postgres/016_character_items.sql`
  - `database/postgres/021_cap_packet_grade_at_12.sql` (the historical filename
    remains, but its current view definition uses the local G25 ceiling)
  - `database/postgres/027_character_equipment_scores_append_attributes.sql`
  - `database/postgres/030_item_grade_levels_25.sql`
  - `database/postgres/041_character_stat_summary.sql`
  - `database/postgres/044_holy_stone_weapon_sockets.sql`
  - `database/postgres/045_holy_stone_socket_cap_4.sql`
- Generated server data:
  - `src/Godswar.Server/State/ItemTemplateSeed.Generated.cs`
  - `src/Godswar.Server/State/ItemAttributeTemplateSeed.Generated.cs`
  - `tools/GenerateItemTemplates.ps1`
  - `tools/GenerateItemAttributeTemplates.ps1`
- Every affected item template needs enough entries in `BaseFraction` (quality),
  `AppFraction` (grade), `MainAttribute`, and its base-stat arrays. The current
  Q20/G25 patch scripts include `PatchLevel135GearCaps.ps1` and the per-slot weapon,
  ring, chest/amulet, boots/girdle, helmet, sleeve, and leggings variants.
- Rank/aura calculations and thresholds must be extended with the item data:
  `equipment_rank_rules`, `character_equipment_scores`,
  `character_rank_summary`, `ArmEffFraction`/`ArmEff`, and body-effect assets.

### Patched game-client surfaces

The game-client repository was clean at baseline commit `8418134` during this
audit. No client edit is required for the present inspection fix. The following
existing local changes are the checklist for any future ceiling increase:

- `Origin.exe` file offsets `0xA70AA` and `0xA70B3`: single-item score path now
  accepts Q20/G25.
- `Origin.exe` file offsets `0xA7505` and `0xA750E`: aggregate armor score path now
  accepts through Q20/G25 (rejects Q21/G26).
- Generic 72-byte item parser at VA `0x00441EA0`: copies quality, grade, five
  attributes, holy suit, socket count, and four stone pairs. No Q10/G12 clamp was
  found in this parser.
- Remote-world visual decoder around VA `0x004731A5`: masks the low quality nibble
  and shifts the high grade nibble. This is the client side of the 15/15 hard
  protocol limit.
- Append-attribute XML loader patch at VA `0x0043F275`: accepts `L1..L25`.
- Append-attribute vector/clamp patches at VA `0x00580370` and `0x00580381`.
- Both `en_us` and `zh_cn` copies of:
  - `Localization/<locale>/Settings/Sys/ItemBaseAttribute.xml`
  - `Localization/<locale>/Settings/Sys/ItemColor.xml`
  - `Localization/<locale>/Settings/Sys/EquipForge.xml`
  - `Localization/<locale>/Settings/Sys/ItemAppendAttribute.xml`
  - `Localization/<locale>/UI/Base/font.lua`
  - relevant `Text/EquipDescription.dat` and equipment-name data.

The six-socket scripts `tools/PatchSixSocketItemRecord.ps1` and
`tools/PatchSixSocketLayoutCap.ps1` are reference-only. Their known parser/display
sites must not be applied accidentally while the active server remains on the
native four-socket model.

## Required regression matrix

Before raising a ceiling or changing item identity behavior, verify all of these:

1. Q10/G12 control item and Q20/G25 extended item.
2. Ring 1 only, ring 2 only, and two rings with identical template IDs.
3. Five append attributes, holy-suit code `710`, and four holy stones.
4. Correct packed order and exact source-slot mask.
5. Stable identities on repeated inspection and relog.
6. Distinct identities across players and across both ring slots.
7. Item-state identity changes after an upgrade/replacement while slot identity
   remains stable.
8. Empty-record sentinels after the final packed item.
9. Separate world-spawn checks for both ring IDs, mask `0x000007FF` for core slots
   `0..10`, and the intentionally projected world visual byte.
10. Two-account UI retest after clearing the old session/cache state by relogging.
