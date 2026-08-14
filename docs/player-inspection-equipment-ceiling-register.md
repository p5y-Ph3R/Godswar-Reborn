# Remote Player Equipment Ceiling Register

This is the ceiling-register companion to the
[remote player equipment inspection protocol](player-inspection-equipment-protocol.md).

## Ceiling register

Detailed inspection and remote world appearance are different protocols. They must
not share a ceiling constant.

| Surface | Current effective ceiling | Where to change it | Important limitation |
| --- | --- | --- | --- |
| Detailed `10022` quality/grade | Q20/G25 from current DB/templates; serialized as full bytes | `PacketBuilder.PlayerInspectEquipment`, `WriteInspectItemRecord`, `WriteKitBagItemRecord`; DB/template and client sites below | Wire fields can hold `0..255`, but downstream data and client arrays are the practical ceiling |
| Ordinary Sapphire forging | Q20/Boundless | `EquipmentForgeCalculator.MaximumQuality`; both locales' `EquipForge.xml`, `BijouForge.xml`, and `ItemBaseAttribute.xml`; `tools/PatchClientForgeBoundlessGrade25.ps1`; generated forge/item catalogs | Level-4 Sapphire remains Q8..Q12. Level-5 Sapphire covers current Q8..Q19; Q19 is the final attempt and produces Q20 |
| Ordinary Emerald forging | G25 | `EquipmentForgeCalculator.MaximumGrade`; both locales' `EquipForge.xml`, `BijouForge.xml`, and `ItemBaseAttribute.xml`; `tools/PatchClientForgeBoundlessGrade25.ps1`; generated forge/item catalogs | Level-4 Emerald remains G10..G17. Level-5 Emerald covers current G10..G24; every forgeable `AppFraction` must contain the G25 result |
| Weapon rank | WR10 at score 8000; every ordinary forgeable weapon supports it | `BaseFraction`, `AppFraction`, `ArmEffFraction`, `ArmEff`; `equipment_rank_rules`; `tools/PatchClientGlobalEquipmentRanks.ps1`; generated item templates | Q20/G25 with five append attributes scores 8050/WR10. Four attributes score 6780/WR9. Special GM Spear `1499` keeps its authored arrays |
| Armor rank | AR14 at score 25300; every ordinary forgeable `armor`/`cloth` carrier supports it | `BaseFraction`, `AppFraction`, `DefendFraction`, `DefendEff`; aggregate score views; `tools/PatchClientGlobalEquipmentRanks.ps1`; generated item templates | Complete no-shield set is 25350. A shield adds 650, keeping Warrior/Priest at 26000 below signed 16-bit. Custom GM Armor `2190` is preserved |
| Remote world equipment appearance | Native fallback Q13/G12; local `GWX1` extension Q20/G25 | Legacy caps and `PackWorldItemVisual`, plus `PlayerWorldFullVisual*` in `PacketBuilder.PlayerWorld.cs` and `PacketBuilder.ItemSerialization.cs`; `tools/PatchRemoteWorldEquipmentExtension.ps1`; client score caps/template arrays | Native byte remains `(grade << 4) | quality`. The appended full-byte fields carry through 255; the native fallback deliberately remains Q13/G12 |
| Append attributes | Five IDs; local data through L25 | Item record offsets `+4..+20`, DB attribute templates, client `ItemAppendAttribute.xml` and patches below | Attribute levels are not separate fields in captured 72-byte records. The client combines IDs, grade, and its XML. More than five attributes needs protocol/client work |
| Holy suit | Current semantic type 7, level 10; code `710` | `CompactItemEntry.HolySuitType/HolySuitLevel`, `WriteItemExtension`, embedded schema, migrations `003` and `015`, tier/requirement data, and client holy-suit data | Wire code is a signed 16-bit value. New semantic tiers/levels need client lookup/effect discovery as well as larger server tables |
| Holy stones | Four sockets, effects at levels 1..10 | `NativeClientHolyStoneSocketCount`, `CompactItemEntry.MaxSockets`, `HolyStoneItemMutator.MaxSockets`, `WriteHolyStoneValueRows`, `HolyStoneEffectCode`, and value tables | Captured/native record has exactly four effect/value pairs. DB columns 5/6 are dormant; six sockets need the reference client patches and a revised packet model |

### Server and database surfaces

When raising quality, grade, attribute, holy-suit, or stone ceilings, audit all of
these together:

- `src/Godswar.Server/Packets/PacketBuilder.EquipmentInspection.cs`
  - detailed inspection records and identities;
- `src/Godswar.Server/Packets/PacketBuilder.ItemSerialization.cs`
  - detailed inspection packing, identities, and full-byte serialization;
- `src/Godswar.Server/Packets/PacketBuilder.PlayerWorld.cs`
  - `PackWorldItemVisual`, its separate nibble fallback, and the `GWX1`
    full-byte world extension;
  - `WriteItemExtension`, socket count, effect-code level clamp, and stone-value
    lookup arrays;
  - other full-byte item paths (`WriteEnterItemRecord`, kitbag, and item snapshots).
- `src/Godswar.Server/State/CompactItemEntry.cs`
  - parsed compact-field types, holy-suit semantic clamps, and four-socket output.
- `src/Godswar.Server/State/HolyStoneItemMutator.cs`
  - four-socket mutation loops and limits.
- `src/Godswar.Server/State/DatabaseMigrations/LegacySchemaBootstrap.sql`
  - `character_item_compact_entries` clamps quality to the
    template `BaseFraction` length and grade to both `AppFraction` length and the
    literal `25`;
- `src/Godswar.Server/State/PostgresGameStore.*.cs`
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
  - `src/Godswar.Server/State/EquipmentForgeCatalog.Generated.cs`
  - `tools/GenerateItemTemplates.ps1`
  - `tools/GenerateItemAttributeTemplates.ps1`
  - `tools/GenerateEquipmentForgeCatalog.ps1`
- Ordinary-forge authority and validation:
  - `src/Godswar.Server/State/EquipmentForgeCalculator.cs` owns the Q20/G25
    ceilings, 25-crystal limit, material/range checks, probability clamp, and
    result construction;
  - `src/Godswar.Server/State/ForgingMaterialCatalog.cs` owns the three local
    Level-5 item definitions;
  - generated equipment/material rules must be regenerated after either
    locale's `EquipForge.xml` or `BijouForge.xml` changes.
- Every affected item template needs 20 entries in its core numeric quality
  vectors and `BaseFraction`, plus 25 entries in the grade-indexed
  `AppFraction`. `MainAttribute` is an allowed-attribute list, not a progression
  vector. `PatchClientForgeBoundlessGrade25.ps1` establishes those indexed
  vector lengths; `PatchClientGlobalEquipmentRanks.ps1` then normalizes the
  score tails and independent rank tables across every forgeable item tier.
- Rank/aura calculations and thresholds must be extended with the item data:
  `equipment_rank_rules`, `character_equipment_scores`,
  `character_rank_summary`, `ArmEffFraction`/`ArmEff`, and body-effect assets.
  Those `ArmEff*` tables and the corresponding `DefendFraction`/`DefendEff`
  tables are independent rank curves; preserve them byte-for-byte during an
  ordinary-forge ceiling change unless the rank curve itself is intentionally
  being redesigned. The global equipment-rank patch is that intentional
  redesign; regenerate item templates immediately after applying it.

### Class-neutral rank normalization

Profession IDs are Warrior `0`, Champion `1`, Priest `2`, and Mage `3`. Rank
ceilings are not profession constants: the client selects score and effect
curves from the equipped item template. The apparent Warrior WR7 cap was caused
by account 13's starter sword `1000`, whose old `ArmEffFraction` ended after the
`600`/rank-7 threshold, being compared with account 7's endgame Champion spear
`1435`. The server already evaluated the starter sword's current score `1280`
as WR8; the short client table caused the visible disagreement.

`tools/PatchClientGlobalEquipmentRanks.ps1` applies one guarded model to both
locales:

- Every ordinary forgeable weapon except GM Spear `1499` uses Q20
  `BaseFraction=1700`, G25 `AppFraction=1270`, and thresholds
  `40,100,180,240,300,460,600,1200,4000,8000,-1...`. Five append attributes
  produce `1700 + (5 * 1270) = 8050`/WR10; four produce `6780`/WR9.
- Warrior and Champion weapons use the physical `1`-family effects. Priest
  weapons use the `201` family and Mage weapons the `51` family, preserving
  their distinct aura assets while sharing rank thresholds.
- Every ordinary forgeable `armor` and `cloth` row uses
  `DefendFraction=330,475,750,950,1350,1720,2225,3860,5250,8000,12000,17000,22000,25300,-1`
  and `DefendEff=1,2,3,4,5,6,7,8,9,10,11,12,13,14,14`.
- Nonweapon rows retain their exact Q1..Q10 and G1..G12 prefixes. Extended
  tails follow the existing slot profile, ending at Q20=`3 * Q10` and
  G25=`4 * G12`.

| Nonweapon profile | Q10 | Q20 | G12 | G25 | Q20/G25 with five attributes |
| --- | ---: | ---: | ---: | ---: | ---: |
| Body (`armor`, `cloth`) | 300 | 900 | 150 | 600 | 3900 |
| Medium (`head`, `glove`, `girdle`, `shoes`) | 200 | 600 | 100 | 400 | 2600 |
| Light (`amulet`, `cuff`, `leggins`, `ring`) | 170 | 510 | 85 | 340 | 2210 |
| Shield | 50 | 150 | 25 | 100 | 650 |

One body, four medium pieces, three light pieces, and two rings total `25350`.
Warrior/Priest add the `650` shield profile for `26000`, safely below `32767`.
GM Spear `1499` and GM Armor `2190` are explicitly excluded, preserving their
authored score, allowed-attribute, and rank/effect arrays in each locale.

### Patched game-client surfaces

The game-client repository started from baseline commit `8418134`. Detailed
inspection itself still uses the generic full-byte parser, while correct remote
rank/aura rendering now also requires the guarded `GWX1` world-decoder patch.
The following local changes are the checklist for any future ceiling increase:

- `Origin.exe` file offsets `0xA70AA` and `0xA70B3`: single-item score path now
  accepts Q20/G25.
- `Origin.exe` file offsets `0xA7505` and `0xA750E`: aggregate armor score path now
  accepts through Q20/G25 (rejects Q21/G26).
- Generic 72-byte item parser at VA `0x00441EA0`: copies quality, grade, five
  attributes, holy suit, socket count, and four stone pairs. No Q10/G12 clamp was
  found in this parser.
- Remote-world visual decoder at VA `0x004731A5`: the native fallback still masks
  the quality/grade nibbles, while the local hook reads full-byte `GWX1` arrays
  from the extended packet. Reapply or revert it only through
  `tools/PatchRemoteWorldEquipmentExtension.ps1`.
- Append-attribute XML loader patch at VA `0x0043F275`: accepts `L1..L25`.
- Append-attribute vector/clamp patches at VA `0x00580370` and `0x00580381`.
- Both `en_us` and `zh_cn` copies of:
  - `Localization/<locale>/Settings/Sys/ItemBaseAttribute.xml`
  - `Localization/<locale>/Settings/Sys/ItemColor.xml`
  - `Localization/<locale>/Settings/Sys/EquipForge.xml`
  - `Localization/<locale>/Settings/Sys/BijouForge.xml`
  - `Localization/<locale>/Settings/Sys/ItemAppendAttribute.xml`
  - `Localization/<locale>/UI/Base/font.lua`
  - `Localization/<locale>/UI/Texture/Icon4.gwo`
  - relevant `Text/EquipDescription.dat` and `Text/EquipName.dat` data.
- `tools/PatchClientForgeBoundlessGrade25.ps1` is the authoritative idempotent
  ordinary-forge patch. It owns the Q20/G25 executable gates, both locale data
  sets, Level-5 material rows/tooltips, forge probability and money vectors,
  item-stat vectors, and constructor defaults. The older Q13 and G18 scripts
  are superseded stepping stones; rerunning either over this installation can
  lower gates or restore an earlier terminal sentinel.
- `tools/PatchClientGlobalEquipmentRanks.ps1` is the separate authoritative
  all-tier rank patch. Apply it after the ordinary-forge patch. It verifies the
  native Q1..Q10/G1..G12 nonweapon prefixes, installs the normalized extended
  score/rank profiles in both locales, validates all four profession effect
  families, and preserves GM Spear `1499` plus GM Armor `2190` exactly. Follow it with
  `tools/GenerateItemTemplates.ps1` so generated C#/SQL and live PostgreSQL do
  not reload an older item-tier curve.
- The eleven `Origin.exe` progression/candidate immediates have different
  inclusive/exclusive meanings and must not be assigned one common value:

  | File offset | Patched compare value | Required behavior |
  | ---: | ---: | --- |
  | `0x23A18` | `0x13` / 19 | Sapphire preflight accepts current quality through Q19 |
  | `0x23A24` | `0x18` / 24 | Emerald preflight accepts current grade through G24 |
  | `0x2459C` | `0x14` / 20 | Shared success path accepts a Q20 result/current cross-axis item |
  | `0x245B0` | `0x19` / 25 | Shared success path accepts a G25 result/current cross-axis item |
  | `0x24776` | `0x14` / 20 | Generic result validation accepts quality through Q20 |
  | `0x24781` | `0x19` / 25 | Generic result validation accepts grade through G25 |
  | `0x24981` | `0x14` / 20 | Sapphire increments only below the Q20 ceiling |
  | `0x15DEC4` | `0x15` / 21 | Main candidate path uses exclusive Q21, admitting Q20 |
  | `0x15E818` | `0x15` / 21 | Alternate candidate path uses exclusive Q21, admitting Q20 |
  | `0x160CA2` | `0x13` / 19 | Sapphire UI suitability accepts current quality through Q19 |
  | `0x160CAF` | `0x18` / 24 | Emerald UI suitability accepts current grade through G24 |

  The shared quality/grade result checks are intentionally global: Sapphire
  must work on G25 equipment, Emerald on Q20 equipment, and Ruby on Q20/G25
  equipment. The native Emerald result routine increments grade unconditionally,
  so the G24 preflight/material range is also what prevents G25 to G26.
- The native `BijouForge.xml` loader parses `Round` with `%d,%d`, so each range
  is exactly two inclusive endpoints. Level-4 Sapphire remains `8,12` and
  Level-4 Emerald remains `10,17`. Level-5 Sapphire is `8,19` at `+32`,
  Level-5 Emerald is `10,24` at `+32`, and Level-5 Crystal is `+25` with the
  ordinary-forge selection capped at 25. An enumerated value such as
  `8,9,10,11,12` is silently parsed only as `8,9`.
- Every `EquipForge.xml` row needs 20-entry `BaseProyAdd`/`Bmoney` vectors and
  25-entry `AppendProyAdd`/`Cmoney` vectors. Current Q13..Q19 use probability
  adjustments `-255,-265,-275,-285,-295,-305,-315` and cost multipliers
  `35,40,45,50,55,60,65`; Q20 is a zero terminal. Current G18..G24 use
  `-395,-420,-445,-470,-495,-520,-545` and multipliers
  `55,60,65,70,75,80,85`; G25 is a zero terminal. At G24 the raw formula is
  `-545 + 32 + (24 * 25) = 87`; 25 crystals produce raw `112`, clamped to 100.
- Every forgeable `ItemBaseAttribute.xml` row must cover Q20 in its core numeric
  quality vectors and `BaseFraction`, and G25 only in the grade-indexed
  `AppFraction`. A short indexed vector can invoke the client's fatal bounds
  handler at VA `0x005814AE` while building an extended item's tooltip or score.
  `MainAttribute` is an allowed-attribute list, and `ArmEffFraction`/`ArmEff`
  plus `DefendFraction`/`DefendEff` are independent rank tables; the
  authoritative ordinary-forge patch preserves all of them byte-for-byte. The
  subsequent global equipment-rank patch deliberately replaces those rank
  tables with the class-neutral all-tier model described above.
- XML attributes may be absent legitimately. The native item-base constructor
  at VA `0x00436E70` supplies defaults at 27 `push` count immediates
  (AttackSpeed has two chunks). Only the 22 core quality/base sites must push 20 at
  file offsets `0x37202`, `0x37217`, `0x3722C`, `0x37241`, `0x37256`,
  `0x3726F`, `0x37280`, `0x37295`, `0x372AA`, `0x372BF`, `0x372D6`,
  `0x372ED`, `0x37304`, `0x37319`, `0x37330`, `0x37347`, `0x3735C`,
  `0x37371`, `0x37388`, `0x3739F`, `0x373BA`, and `0x373CB`. Only the
  `AppFraction` site at `0x373E0` must push 25. Preserve the armor-rank sites
  `0x373F5`/`0x3740A` and defend-rank sites `0x3741F`/`0x37434` at their current
  count of 13. Padding those four rank tables changes rank thresholds and can
  inflate displayed aura rank accidentally; the global rank patch instead
  supplies explicit XML rank curves and leaves these missing-field defaults
  unchanged. A missing true quality/grade XML field otherwise retains a short
  native default and reaches the bounds failure above.

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
   `0..10`, the legacy projected visual byte, and the `GWX1` Q/G arrays.
10. Two-account UI retest after clearing the old session/cache state by relogging.
11. Starter, mid-tier, and endgame weapons for Warrior, Champion, Priest, and
    Mage: Q20/G25 with five attributes must score `8050`/WR10; four attributes
    must score `6780`/WR9 and use the correct profession effect family.
12. Starter and endgame `armor`/`cloth` carriers must expose AR14, while their
    Q1..Q10/G1..G12 prefixes remain unchanged.
13. Complete no-shield and shield-bearing sets must score `25350` and `26000`
    respectively; GM Spear `1499` and GM Armor `2190` must retain every authored
    score/rank array.
