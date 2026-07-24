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
- Working-server-compatible 63-record post-login bootstrap manifest, including the trailing client version record; the installed V4 client candidate schedules native character-selection initialization, retains the exact preview until readiness, and guards all three audited null-resource sites
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

Detailed client patches, rank and forge extensions, effect experiments, item
persistence notes, and the current avatar-gate status are maintained in
[`docs/client-reverse-engineering-notes.md`](docs/client-reverse-engineering-notes.md).

Avatar-preview loading-gate V1 (`2D819908...E2AE0`) failed by starving native
processing; V2 (`73E65FBF...F2902FD`) failed after its timed unready handoff
recreated the blank model. Readiness-only V3 (`17A72198...D878D1`) is also
rejected: its immutable `20260724T043833399Z-2bd75dd7` run reproduced the
about-15-second server-unavailable path and `0x005F58BC` null-root crash.
Matched V4 Origin (`E0F5BC95...D22F81C`) and Net
(`EF531F8C...817597`) are installed. V4 schedules native state 2 on the exact
AfterLogin record, synchronously initializes LOGIN after registration, keeps
the preview readiness-only hold, and guards the timeout path. Automated gates
pass; one final cold live smoke is pending and acceptance is not claimed.
Current status, immutable incident records, and the acceptance contract are in
[`docs/client-avatar-preview-loading-gate.md`](docs/client-avatar-preview-loading-gate.md).

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
