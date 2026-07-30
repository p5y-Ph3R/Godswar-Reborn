# Godswar .NET Server

Minimal .NET 10 server-side emulator for the Godswar Origin client protocol.

## Run

Checked-in settings fail closed with legacy raw authentication disabled.
For the unmodified local client, use the explicit loopback Docker rollback
profile described below. Use the secure profile for TLS plus authenticated
UDP.

Focused Phase 2 codec check:

```powershell
dotnet run --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj --configuration Release -- "Secure Phase 2"
```

Focused Slice 9 protected-UDP checks:

```powershell
dotnet run --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj --configuration Release -- "Secure Phase 3 UDP"
```

Focused Phase 4 authoritative-movement checks:

```powershell
dotnet run --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj --configuration Release -- "Secure Phase 4"
```

Phase 5A deterministic replay, metrics, decoder fuzz, and bounded local
load/soak gate:

```powershell
dotnet restore GodswarServer.sln
dotnet build GodswarServer.sln --configuration Release --no-restore --nologo
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll "Secure Phase 5A"
.\tools\TestPhase5ABaseline.ps1 -Bots 64 -SoakSeconds 10 -Seed 20260728
```

The load runner is in-process only, opens no sockets, accepts no network
target, and enforces a 5,000,000-operation cap. The accepted local baseline
passed the full `149/149` managed suite against implementation commit
`2986466cfbdb641fe849ce62c7cfd951f2715de8` and is recorded in
[`docs/network-infrastructure-phase5a-replay-load-observability.md`](docs/network-infrastructure-phase5a-replay-load-observability.md).

Slice 9 closeout passed the full managed protocol suite (`121/121`), a Win32
Release native build with `/W4 /WX`, and five consecutive native offline
passes. These are local/offline results: checked-in UDP remains disabled, no
client shim was installed, and gameplay remains on TLS.

Phase 4 source/offline closeout passes a zero-warning managed Release build,
native `/W4 /WX`, and the protocol suites. Final PreviewReadyV6 campaign
`0a73fd79-961b-42c7-82cc-9e4a6f9e3355` passed original-client Baseline,
forced TLS Fallback, and a `661.5843391`-second Soak, then restored the exact
stock client. Checked-in secure settings deliberately remain default-off after
rollback; viewer parity was `Unavailable`. See the
[protocol/runtime record](docs/network-infrastructure-phase4-authoritative-movement.md).

## Docker

```powershell
docker compose `
  -f docker-compose.yml `
  --profile legacy-raw `
  up --build -d server
```

The opt-in TLS plus authenticated-UDP Docker profile is documented in
[`docs/network-infrastructure-secure-docker.md`](docs/network-infrastructure-secure-docker.md).
It replaces the raw server container, publishes only loopback secure ports,
and keeps certificate material in read-only Compose secrets.

This explicit local rollback starts:

- `godswar-postgres`: PostgreSQL with the schema from `database/postgres/001_init.sql`
- `godswar-server`: .NET 10 server using PostgreSQL storage

If Windows already has listeners on `5999` or `7000`, free those ports first or override the host ports in `.env`.

```powershell
Copy-Item .env.example .env
docker compose --profile legacy-raw up --build -d server
```

The default listener matches `C:\Godswar Origin\config.ini`:

```ini
[SERVER]
PORT=5999
IP=127.1.1.110
```

Both checked-in appsettings files set legacy raw authentication to `false`.
The raw Docker server starts only with `--profile legacy-raw`, explicitly
enables the rollback capability, and publishes only loopback host ports. It
preserves the unmodified client's plaintext-compatible login and username-only
game binding, so it is not a production security boundary.

The mutually exclusive secure command is:

```powershell
docker compose `
  --env-file .env.secure.local `
  -f docker-compose.yml `
  -f docker-compose.secure.yml `
  --profile secure `
  up --build -d server
```

Certificate/password setup is in the
[secure Docker runbook](docs/network-infrastructure-secure-docker.md).

`Production` is fail-closed: it requires PostgreSQL, a nonempty connection
string, secure TLS listeners, and plaintext credential migration disabled.
Missing or unknown runtime/storage values, JSON or raw TCP in `Production`,
plaintext migration in `Production`, and malformed security environment
values stop startup before storage initialization. Select the profile with
`runtimeProfile` or `GODSWAR_RUNTIME_PROFILE`; never use `LocalDevelopment` as
a production fallback.

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
- Working-server-compatible 63-record post-login bootstrap manifest, including the trailing client version record; the V4 avatar-preload experiment was rejected and rolled back after its final cold smoke failed before character selection
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
- Secure networking: PreviewReadyV6 completed original-client TLS authentication, authenticated encrypted UDP binding, authoritative movement, forced one-way TLS fallback/correction, and a ten-minute Soak. Exact rollback restored stock files and disabled activation; protected receipt `completion-0a73fd79-961b-42c7-82cc-9e4a6f9e3355.json` has SHA-256 `5EB6E369...F4A6F` ([acceptance record](docs/network-infrastructure-controlled-host-acceptance.md)). The secure Docker profile publishes only loopback `6599/TCP`, `7443/TCP`, and `7444/UDP`; viewer parity was `Unavailable`, and production security/capacity gates remain ([Slice 9 overview](docs/network-infrastructure-phase3-slice9c-protected-udp.md), [Phase 4 record](docs/network-infrastructure-phase4-authoritative-movement.md)).
- Raw-authentication retirement: checked-in defaults reject raw startup; the unsafe original-client path now requires the explicit loopback-only `legacy-raw` Docker profile. The playable `7FB43C8D...BA07F9` Origin plus deterministic secure Net `A26096B0...D50AA4` pair passed exact offline gates, but is not installed or live re-accepted. An Origin hash is compatibility metadata rather than authentication or anti-cheat ([B14 evidence](docs/data-architecture-b14-raw-auth-retirement-20260731.md)).

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
(`EF531F8C...817597`) passed automated gates but failed the final cold smoke
before character selection: Origin connected to game TCP `7000`, but the server
received no `LoginGameServer`, so AfterLogin and the V4 preload never ran. The
sealed result is `20260724T095739213Z-db16daa7` / `Fail`; no dump was created.
V4 was rolled back. The client now has predecessor Origin
`753BE49F...9ED79`, stock Net `1CC3F9AA...BCA00C`, and no `NetLegacy.dll`.
The avatar issue is parked and Phase 2 proceeds without Phase 1 acceptance.
Current status and immutable incident records are in
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
