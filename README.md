# Godswar .NET Server

Minimal .NET 10 server-side emulator for the Godswar Origin client protocol.

## Run

```powershell
dotnet run --project src\Godswar.Server
```

## Docker

```powershell
docker compose up --build
```

This starts:

- `godswar-postgres`: PostgreSQL with the schema from `database/postgres/001_init.sql`
- `godswar-server`: .NET 10 server using PostgreSQL storage

If Windows already has listeners on `5999` or `7000`, free those ports first or override the host ports in `.env`.

```powershell
Copy-Item .env.example .env
docker compose up --build
```

The default listener matches `C:\Godswar Origin\config.ini`:

```ini
[SERVER]
PORT=5999
IP=127.1.1.110
```

Login is intentionally permissive while protocol coverage is being mapped. Local non-Docker runs default to `data\state.json`; Docker runs use PostgreSQL.

The ECS gameplay architecture, completed cutovers, parity gates, and
reversible monster/player runtime selectors (both default to `Ecs`) are
documented in
[`docs/ecs-migration.md`](docs/ecs-migration.md). Forward-only PostgreSQL
migrations and the recoverable table-cleanup policy are documented in
[`docs/database-migrations.md`](docs/database-migrations.md).

## Current Scope

Implemented:

- Login server on TCP `5999`
- Game server redirect to TCP `7000`
- Stream XOR cipher and packet framing
- Account auto-create
- Character list, create, delete, and preview
- Working-server-compatible 63-record post-login bootstrap manifest, including the trailing client version record; the separate native avatar-resource lifecycle crash is handled by the guarded client patch documented below
- Camp-aware character starts: Sparta/camp 0 enters map 0 and Athens/camp 1 enters map 1 at the captured `(165, -97)` starting position
- PostgreSQL-backed accounts and characters under Docker
- Enter-game packet stream based on the Go reference
- Ping echo, server time, and map-scoped talk/walk broadcast
- Two-client world synchronization using server-built remote-player spawn, equipment/appearance, weapon and armor aura, position, and derived-status packets
- World presence registration only after both the client-ready signal and the player-detail exchange have completed, so other sessions do not see a partially initialized character
- Server-built static NPC definitions assembled from authoritative actor tables first, then validated captured/normalized fallbacks; Sparta uses all 108 unique actors from `C:\Users\Iamc1\Downloads\Sparta\Sparta\NPC.INI` (SHA-256 `A7DFDF9D3C90D27960F730B4B65A7EA37D7F41FC80F7788E584AD80E59BFF340`)
- Generation-safe ECS gameplay state for monsters, map membership/NPCs, player movement, status/recovery/Ride, outgoing combat, and incoming monster damage; protocol and transactional persistence remain boundary adapters, with restart-level `Legacy` rollback selectors
- Movement-driven NPC visibility matching the working server's `32x32` world sectors: each client receives only its current sector and the eight neighboring sectors, with remove/spawn diffs when crossing a boundary
- Movement-driven captured-monster visibility on the same `32x32`/`3x3` sector model, using the raw appearance packet's coordinates as authoritative, validating captured metadata at map load, and sending removals before newly visible monsters
- Server-owned monster roaming, retaliation, extended chase, authoritative leash despawn/fresh full-health replacement at home, ordinary attacks, learned-skill damage, death, revival, and persisted fighter/talent rewards
- Live server time plus full Zodiac state synchronization and persistent five-minute continuous-login energy accounting across reconnects
- Ordinary equipment forging with the client's 611 `EquipForge` rules, Sapphire quality upgrades through Q20/Boundless, Emerald grade upgrades through G25, optional Crystal probability boosts, atomic inventory/silver persistence, and an allowlisted material-grant command
- Authoritative Gear Mentor Add/Enhance/Delete, decomposition, 99-dust Attribute Stone creation, Crystal downgrade transformation, and Level-4/5 gem-piece combination workflows
- Map-specific NPC interaction IDs, including Holy Stone Artisan dialog/action routing in both Sparta and Athens

The multiplayer, NPC, and captured-monster synchronization above is server-side. It does not require game client code changes; a client already configured to connect to this server can use it as-is. The patches below cover separate extended-grade, rank, aura, talent, and native client-stability work.

Remote-player equipment inspection now follows the captured packed-record plus
source-slot-mask layout, preserves detailed Q20/G25 values, and uses stable
per-character/item identities so both rings and their grade, attributes, holy
suit, and four holy stones can be associated correctly. The packet layout,
capture evidence, and complete future-ceiling checklist are recorded in
[`docs/player-inspection-equipment-protocol.md`](docs/player-inspection-equipment-protocol.md).

Not complete yet:

- Full world simulation
- NPC coverage beyond the current static city baseline: the authoritative actor tables now place 108 Sparta and 111 Athens NPCs, while most NPC dialog scripts and quests remain unimplemented
- Monster coverage beyond the current 270 static captured appearances on Sparta/map 0; Athens and other maps do not yet have captured monster baselines
- Monster simulation currently covers local roaming, chase/retaliation, leash replacement at home, normal and learned-skill damage, death, and timed revival; parties, drops, multi-player threat selection, and broader skill effects remain incomplete
- Kill progression now persists carried fighter levels, fighter EXP, talent EXP, and talent points and refreshes the client through the captured monster-death/EXP/level packets; passive HP/MP recovery uses the captured six-second absolute-vitals update
- Talent upgrades now support each class's full ID range (including warrior node `0`) with server-owned persisted rank/cost validation; complete inventory, skill, and talent gameplay is still unfinished
- Ordinary equipment forging is implemented. Material combination and equipment combination (forge modes 1 and 2) remain unsupported. Level-5 Sapphire, Emerald, and Crystal are local extensions and require the matching patched client data.
- Packet coverage outside the mapped reference opcodes

## Current Reverse Engineering Notes

Grade/quality/rank support for local testing has been extended beyond the original client limits:

- Ordinary Sapphire and Emerald forging now share the local Q20/Boundless and G25 ceilings. `tools/PatchClientForgeBoundlessGrade25.ps1` is the authoritative idempotent client patch: it synchronizes both locales, all eleven native progression/candidate gates, the 22 quality/base constructor counts plus the one `AppFraction` count, material rules, tooltips, and the true forge-indexed item vectors. It deliberately preserves `MainAttribute` and every `ArmEff*`/`Defend*` rank table byte-for-byte. The older Q13 and G18 scripts are historical stepping stones and must not be rerun over the higher ceiling. `tools/GenerateEquipmentForgeCatalog.ps1` and `tools/GenerateItemTemplates.ps1` regenerate the matching authoritative server data.
- The apparent Warrior WR7 cap was an item-tier mismatch, not a profession cap: account 13 used starter sword `1000`, whose legacy client rank curve ended after the `600` threshold, while the Champion comparison used endgame spear `1435`. `tools/PatchClientGlobalEquipmentRanks.ps1` intentionally normalizes rank-score and rank-effect tables for every ordinary forgeable item in both locales. All ordinary weapons except special GM Spear `1499` use one Q20/G25 score profile: five append attributes produce `1700 + (5 * 1270) = 8050`/WR10, while four produce `6780`/WR9. Warrior/Champion retain the physical effect family, Priest the `201` family, and Mage the `51` family.
- The same global rank patch gives every ordinary forgeable `armor`/`cloth` carrier the AR14 curve through score `25300`. Nonweapon Q1..Q10 and G1..G12 score prefixes stay unchanged; their tails end at Q20=`3 * Q10` and G25=`4 * G12`. A complete five-attribute no-shield set scores `25350`; the normalized shield contributes `650`, so complete Warrior/Priest equipment scores `26000`, below the client's signed-16-bit boundary. Custom GM Armor `2190` remains untouched. Regenerate `ItemTemplateSeed.Generated.cs` and `002_item_templates.sql` after applying the client patch.
- Level-4 material ranges stay native/local-compatible: Sapphire `4213` applies to current Q8..Q12 and Emerald `4223` to current G10..G17. Local Level-5 Sapphire `4215` gives `+32` across Q8..Q19, Level-5 Emerald `4225` gives `+32` across G10..G24, and Level-5 Crystal `4234` gives `+25` per selected crystal, with at most 25 selected.
- The added quality probability tail for current Q13..Q19 is `-255,-265,-275,-285,-295,-305,-315`, followed by the Q20 zero terminal; its silver-cost multipliers are `35,40,45,50,55,60,65`. The added grade tail for current G18..G24 is `-395,-420,-445,-470,-495,-520,-545`, followed by the G25 zero terminal; its multipliers are `55,60,65,70,75,80,85`. At G24, 24 Level-5 Crystals give a raw `87%`; 25 give raw `112%`, clamped to `100%`. The dedicated Level-5 sprites remain in `Icon4.gwo` at Crystal `0,0`, Sapphire `36,0`, and Emerald `72,0`; their piece sprites use `108,0`, `144,0`, and `180,0`. The shared stock `Icon2.gwo` atlas remains untouched.
- `ItemAppendAttribute.xml` is extended to `L25` for all append attributes in both `en_us` and `zh_cn`.
- `item_attribute_templates` is regenerated from the patched client XML; all 193 rows have `max_level = 25` and 25 `level_values`.
- `tools/GenerateItemAttributeTemplates.ps1` now derives the legacy `character_equip.type*/quality*/value*` mirror from `character_items`, not `character_kitbag`, so `character_items` remains the source of truth.
- The current local DB mirror for user `1` keeps weapon rank testing active through score `8050`, weapon rank `10`, and weapon aura effect `9`.
- `Origin.exe` has three append-attribute grade clamp patches for the client's original `L15` vector limit; the newest patch is at VA `0x580370`/`0x580381` and backs up to `Origin.exe.pre-append-attribute-clamp3.bak`.
- `Origin.exe` also patches the `ItemAppendAttribute` XML loader at VA `0x43F275` so it accepts `L1..L25` instead of the original hardcoded `L1..L15`; backup is `Origin.exe.pre-itemappend-l25-loader.bak`.
- `Origin.exe` patches the passive talent parser at file offset `0xB73BF` from `0x3C`/60 to `0x64`/100 so talent ranks can continue past the stock `60/60` UI cap. Backup is `Origin.exe.pre-talent100-cap.bak`.
- Champion passive talent tooltip values in `Localization\en_us\Settings\Sys\Skill.ini` are scaled by `2.6x` so rank-100 tooltips match the server's progressive effective-rank total (`100 -> 260`). Backup is `Skill.ini.pre-progressive-talent-tooltip.bak`. The label `NextLevel` in `Localization\en_us\Text\Message.dat` is changed to `Next level (progressive curve)`; backup is `Message.dat.pre-talent-tooltip-label.bak`. This is a data-level display fix only; exact per-rank curve text or per-talent milestone descriptions need a native tooltip patch because `Skill.ini` has no description field.
- Talent milestone bonuses are intentionally parked for now. The current talent system only has progressive base scaling through `talent_effective_rank(rank)` and the client tooltip compatibility patch above. There is no separate milestone-attribute layer yet. Future implementation should add a `talent_milestone_effects` table, include those effects in `character_stat_summary`, and then patch/discover the client tooltip path so rank milestones like `40/60/80/100` can show their bonus text separately from the base stat line.
- `Origin.exe` has a weapon rank item-score cap patch in the single-item score path at file offsets `0xA70AA` and `0xA70B3`: quality is capped at `0x14`/20 and grade is capped at `0x19`/25. The original client compared against quality `10` and grade `12`, so future weapon quality/grade upgrades must revisit this path.
- `Origin.exe` has a separate armor rank aggregate-score cap patch at file offsets `0xA7505` and `0xA750E`: quality now rejects only `>= 21` and grade now rejects only `>= 26`. The original client skipped armor contribution for quality `>= 11` or grade `>= 13`, which is why G13 gloves showed `0` contribution until this was patched. Backup is `C:\Reborn\backups\origin-armor-rank-q20-g25-20260518\Origin.exe`.
- Remote-player opcode `0x2725` now retains a native Q13/G12 nibble projection and appends a versioned `GWX1` full-byte Q/G tail. `tools/PatchRemoteWorldEquipmentExtension.ps1` installs or reverts the guarded `Origin.exe` decoder. This lets nearby-player weapon/armor rank and aura calculations use Q20/G25 while unextended 260-byte packets keep the original decoder. The wire extension supports bytes through Q255/G255; future ceilings must still extend client template arrays and the score checks at `0xA70AA`, `0xA70B3`, `0xA7505`, and `0xA750E`.
- `tools/PatchClientAvatarPreviewGuard.ps1` repairs the stale LOGIN avatar-initialization flag and guards both known native selection-avatar builders. The installed client changes exactly 169 bytes, has SHA-256 `1BBD41D4E148E040B363D2A83D36CD326A2C2CFE1EA44E08DA6B2680CA1BB329`, and can be restored from `C:\Reborn\backups\origin-avatar-preview-guard-Apply-20260722-135817166\Origin.exe`. Root-cause addresses, safety checks, and the runtime acceptance sequence are in [`docs/client-avatar-preview-crash-fix.md`](docs/client-avatar-preview-crash-fix.md).
- Body armor rows (`armor` and `cloth`) need an extra final `DefendFraction`/`DefendEff` sentinel after the highest armor rank. For AR14 testing, use `DefendFraction="330,475,750,950,1350,1720,2225,3860,5250,8000,12000,17000,22000,25300,-1"` and `DefendEff="1,2,3,4,5,6,7,8,9,10,11,12,13,14,14"`; otherwise the client displays the wrong next threshold for the current armor rank.
- Armor/weapon rank labels are translated through `Localization\<locale>\Text\EquipDescription.dat` keys `EffLv*`. The stock English client mapped higher ranks back to `9`, so `tools/PatchEquipDescriptionRankLabels.ps1` changes `EffLv10..EffLv14` to `10..14`.
- Armor ranks `11`, `12`, `13`, and `14` are now reserved at scores `12000`, `17000`, `22000`, and `25300`. AR14 is currently the max server/client rank cap.
- AR11 currently uses the no-rotation primordial body-effect package from `C:\Users\Iamc1\Downloads\body_effect_0013_primordial_no_rotation_fix`, installed as `male/female_body_effect_0011.*` in both effect folders. Its compressed JCS files still reference `male/female_body_effect_0013.tga` internally, so those TGA files are also mirrored beside the AR11 files as compatibility texture references.
- AR12 currently uses the emerald soulfire body-effect package from `C:\Users\Iamc1\Downloads\body_effect_0014_emerald_soulfire_male_female_package`, installed as `male/female_body_effect_0012.*` in both effect folders. `tools/PatchArmorRank12JcsReferences.ps1` rewrites the compressed JCS internals from `0014`/`14.tga` to `0012`/`12.tga`; `12.tga` is mirrored beside the AR12 files.
- AR14 uses `male/female_body_effect_0014.*`. `tools/MirrorArmorRank14EffectFiles.ps1` mirrors the complete AR14 effect set from `Characters\effect` into `Characters_New\effect`.
- Experimental append attributes `VampiricPer` (`Type=27`, `ID=460`) and `ReflectDamagePer` (`Type=28`, `ID=470`) are defined in `ItemAppendAttribute.xml` and `item_attribute_templates`. They are data-visible but still need combat resolver support before they affect damage.
- Armor rank 10 uses `male_body_effect_0010.gwo` / `female_body_effect_0010.gwo` in both `Characters\effect` and `Characters_New\effect`. `tools/RecolorArmorRank10Effect.py` recolors those RLE TGA payloads to an angelic white/gold palette; backups are under `C:\Reborn\backups\armor-rank10-angelic-white-gold-*`.
- `tools/RecolorArmorRank10Crimson.py` is the current AR10 recolor script; it preserves the RLE packet layout and shifts the AR4-style texture to near-black crimson. Backups are under `C:\Reborn\backups\armor-rank10-near-black-crimson-*`.
- `tools/RecolorArmorRank10Gold.py` is the latest AR10 recolor script; it preserves the RLE packet layout and shifts the AR4-style texture to near-black gold. Backups are under `C:\Reborn\backups\armor-rank10-near-black-gold-*`.
- Current AR10 test state literally renames the full `0012` body effect set to `0010`, including `_0.jcs`, `_1.jcs`, and `_2.jcs`, and mirrors it into `Characters_New\effect`. Backup is under `C:\Reborn\backups\armor-rank10-literal-rename-0012-to-0010-*`.
- `ItemAppendAttribute.xml` now keeps `L1..L12` unchanged and widens each `L13..L25` increment by 25% of the original `L11->L12` gap. XML backups are `ItemAppendAttribute.xml.pre-progressive-l13.bak`.
- `character_equip.value1..value5` now mirrors final grade-scaled attribute values from `item_grade`; `quality1..quality5` still mirrors each attribute's own upgrade level.
- `Ancient_color` in both locale `font.lua` files is overridden to crimson RGB `220,20,60`; backups are `font.lua.pre-ancient-crimson.bak`.
- `Primordial_color` in both locale `font.lua` files is overridden to dark void RGB `35,20,45`; backups are `font.lua.pre-primordial-void.bak`.
- `Boundless_color` in both locale `font.lua` files is overridden to dark ruby RGB `150,0,32`; backups are `font.lua.pre-boundless-whitegold.bak` and `font.lua.pre-boundless-cyanpearl.bak`.
- Grade 25 `AppLevel25` in both locale `ItemColor.xml` files now uses `Boundless_color`; backups are `ItemColor.xml.pre-boundless-grade25-color.bak`.
- Holy-stone weapon sockets are capped back to the native four-slot model in server logic and active stat calculations. Socket 5/6 DB columns remain only as dormant compatibility fields and are cleared by `database/postgres/045_holy_stone_socket_cap_4.sql`.
- The previous six-socket client patch tooling remains under `tools/PatchSixSocketItemRecord.ps1` and `tools/PatchSixSocketLayoutCap.ps1` for reference, but it is not part of the active local server behavior.
- Equipment/kitbag persistence no longer does a section-wide `DELETE` before reinserting compact item strings. `ReplaceCharacterItemsFromCompactAsync` now applies per-slot upserts/deletes and writes deleted rows to `character_item_audit`; this prevents unrelated equipment slots from being dropped during item moves.
- Starter HP/MP potions (`4000`/`4030`) are granted only when a character is created. Empty inventory fallbacks remain empty, and the legacy `character_kitbag` projection is imported into authoritative `character_items` once under the `20260721_legacy_character_kitbag_import` data-migration marker so deleted items cannot return after a restart.
- Extended item rows need their core numeric quality vectors and `BaseFraction` through Q20, plus `AppFraction` through G25; otherwise the client may render the color but fail stat lookup or close while entering the scene. `MainAttribute` is an allowed-attribute list, while `ArmEffFraction`/`ArmEff` and `DefendFraction`/`DefendEff` are independent rank tables, so ordinary-forge ceiling patches must not pad them.
- `tools/PatchLevel135WeaponCaps.ps1`, `tools/PatchLevel135ArmorRankThresholds.ps1`, and the per-slot level-135 scripts record the earlier endgame-only experiment. Their `PlayLv >= 135` selection is why starter and mid-tier items retained short rank curves. Use `tools/PatchClientGlobalEquipmentRanks.ps1` for the class-neutral, all-tier rank model; do not use a level-gated script to reconstruct the authoritative rank data.
- The global rank patch covers non-body gear rows (`head`, `amulet`, `glove`, `cuff`, `girdle`, `shoes`, `leggins`, `ring`, `shield`) at every forgeable item tier. It preserves native Q1..Q10/G1..G12 score data and normalizes only the extended tails, keeping a full Warrior/Priest set safely below `32767`.
- Ring rows with `PlayLv` minimum `135+` can be patched independently with `tools/PatchLevel135RingCaps.ps1`. The current local test character uses two `Celestial Vigor Ring` (`3246`) rows at Boundless/G25 with append attributes `AttackF`, `PhysicalDamage`, `IgnorePhyPer`, `FuryAkAdd`, and `MaxHPF`, all at level 5.
- Chest (`armor`/`cloth`) and amulet rows with `PlayLv` minimum `135+` can be patched independently with `tools/PatchLevel135ChestAmuletCaps.ps1`. The current local test character uses Boundless/G25 chest attributes `DefenceF`, `AddMagicRecF`, `Miss`, `FuryAkRec`, `MaxHPF`; amulet attributes `Miss`, `FuryAkRec`, `InjureImbibeF`, `StateImmunity`, `MPRestoreF`; all at level 5.
- Boots (`shoes`) and girdle rows with `PlayLv` minimum `135+` can be patched independently with `tools/PatchLevel135BootsGirdleCaps.ps1`. The current local test character keeps the existing boots/girdle attributes and upgrades both equipped slots to Boundless/G25 with `database/fixtures/053_test_character_boundless_g25_boots_girdle.sql`.
- Sleeve (`cuff`) rows with `PlayLv` minimum `135+` can be patched independently with `tools/PatchLevel135SleeveCaps.ps1`. The current local test character keeps the existing sleeve attributes and upgrades the equipped sleeve slot to Boundless/G25 with `database/fixtures/055_test_character_boundless_g25_sleeves.sql`.
- Leggings (`leggins`) rows with `PlayLv` minimum `135+` can be patched independently with `tools/PatchLevel135LeggingsCaps.ps1`. `database/fixtures/056_test_character_sleeves_leggings_attributes.sql` sets sleeve attributes to `DefenceF`, `AddMagicRecF`, `Hit`, `FuryAkAdd`, `State`; and leggings to `DefenceF`, `AddMagicRecF`, `Miss`, `FuryAkRec`, `StateImmunity`, all at level 5, with equipped leggings upgraded to Boundless/G25.
- `database/fixtures/054_test_character_boots_girdle_attributes.sql` changes the current local boots to `Miss`, `FuryAkRec`, `StateImmunity`, `InjureImbibeF`, and `MPRestoreF`; and the girdle to `DefenceF`, `AddMagicRecF`, `MaxHPF`, and `InjureImbibeF`, all at level 5.
- `database/fixtures/057_test_character_girdle_crit_resist.sql` adds `FuryAkRec` as the fifth level-5 girdle attribute for crit resistance.
- `database/fixtures/051_test_character_ring_holy_stones.sql` mirrors the current weapon holy-stone sockets into both equipped ring slots for local testing.

Important client-side files touched while allowing grade 25 / Boundless quality / rank testing:

- `C:\Godswar Origin\Origin.exe`
- `C:\Godswar Origin\Origin_sixsocket.exe`
- `C:\Godswar Origin\Localization\en_us\Settings\Sys\ItemBaseAttribute.xml`
- `C:\Godswar Origin\Localization\zh_cn\Settings\Sys\ItemBaseAttribute.xml`
- `C:\Godswar Origin\Localization\en_us\Settings\Sys\ItemColor.xml`
- `C:\Godswar Origin\Localization\zh_cn\Settings\Sys\ItemColor.xml`
- `C:\Godswar Origin\Localization\en_us\UI\Base\font.lua`
- `C:\Godswar Origin\Localization\zh_cn\UI\Base\font.lua`
- `C:\Godswar Origin\Localization\en_us\Settings\Sys\EquipForge.xml`
- `C:\Godswar Origin\Localization\zh_cn\Settings\Sys\EquipForge.xml`
- `C:\Godswar Origin\Localization\en_us\Settings\Sys\BijouForge.xml`
- `C:\Godswar Origin\Localization\zh_cn\Settings\Sys\BijouForge.xml`
- `C:\Godswar Origin\Localization\en_us\Settings\Sys\ItemAppendAttribute.xml`
- `C:\Godswar Origin\Localization\zh_cn\Settings\Sys\ItemAppendAttribute.xml`
- `C:\Godswar Origin\Localization\en_us\UI\Texture\Icon4.gwo`
- `C:\Godswar Origin\Localization\zh_cn\UI\Texture\Icon4.gwo`
- `C:\Godswar Origin\Localization\zh_cn\Text\EquipName.dat`
- `C:\Godswar Origin\Localization\en_us\Text\EquipDescription.dat`
- `C:\Godswar Origin\Localization\zh_cn\Text\EquipDescription.dat`

Important server/database files for the same work:

- `database/postgres/003_item_quality.sql`
- `database/postgres/004_equipment_scores.sql`
- `database/postgres/005_item_attributes.sql`
- `database/postgres/017_patch_item_1435_quality11.sql` through `database/postgres/030_item_grade_levels_25.sql`
- `database/postgres/046_character_item_audit.sql`
- `src/Godswar.Server/State/PostgresGameStore.*.cs`
- `src/Godswar.Server/State/ItemTemplateSeed.Generated.cs`
- `src/Godswar.Server/State/ItemAttributeTemplateSeed.Generated.cs`
- `src/Godswar.Server/Packets/PacketBuilder.*.cs`
- `tools/GenerateItemTemplates.ps1`
- `tools/GenerateItemAttributeTemplates.ps1`
- `tools/GenerateEquipmentForgeCatalog.ps1`
- `tools/PatchClientForgeBoundlessGrade25.ps1`
- `tools/PatchClientGlobalEquipmentRanks.ps1`
- `tools/PatchLevel135GearCaps.ps1`
- `tools/PatchEquipDescriptionRankLabels.ps1`
- `tools/PatchRemoteWorldEquipmentExtension.ps1`
- `tools/PatchClientAvatarPreviewGuard.ps1`
- `tools/RecolorArmorRank10Effect.py`
- `tools/RecolorArmorRank10Crimson.py`
- `tools/RecolorArmorRank10Gold.py`

Rank reference points:

- The inventory UI labels are in `C:\Godswar Origin\Localization\en_us\UI\XML\ItemBagsUI.xml` and `zh_cn\UI\XML\ItemBagsUI.xml`.
- Armor rank text/value widgets are `EquipEff` and `EquipEffV`.
- Weapon rank text/value widgets are `WepenEff` and `WepenEffV`.
- Weapon rank/aura is driven per template by `ItemBaseAttribute.xml` fields `BaseFraction`, `AppFraction`, `ArmEffFraction`, and `ArmEff`; it is not selected from a separate profession cap.
- Local weapon rank `9` is mapped as score `4000 -> effect 8`; rank `10` is mapped as score `8000 -> effect 9`; client backups include `ItemBaseAttribute.xml.pre-weapon-rank10-effect8.bak` and `ItemBaseAttribute.xml.pre-weapon-rank9-4000-rank10-effect9.bak`.
- Every ordinary forgeable weapon can reach WR10 at Q20/G25 with five append attributes. Four attributes intentionally stop at score `6780`/WR9. GM Spear `1499` and GM Armor `2190` retain their authored score and rank arrays exactly.
- Server-side rank mirrors are `equipment_rank_rules`, `character_equipment_scores`, and `character_rank_summary`.
- Current server weapon rules include ranks `8`, `9`, and `10`.
