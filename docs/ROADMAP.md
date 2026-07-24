# Godswar Server Roadmap

This is the current step-by-step plan for moving the local C# server from packet replay toward real MMORPG gameplay.

The long-term secure networking migration is tracked separately in
[`docs/network-infrastructure-goal.md`](network-infrastructure-goal.md). It
selects an in-process x86 client shim followed by TLS control traffic,
authenticated UDP realtime traffic, server authority, bounded overload
behavior, and upstream DDoS integration. The reversible Origin/`Net.dll`
compatibility experiments V1–V4 are rejected. V3 reproduced the roughly
15-second `0x005F58BC` timeout/crash. V4 failed its final pre-selection smoke
(`20260724T095739213Z-db16daa7` / `Fail`) and was rolled back to predecessor
Origin `753BE49F...9ED79`, stock Net `1CC3F9AA...BCA00C`, and no
`NetLegacy.dll`. Phase 2 slice 3 extracted and parity-tested the raw byte
transport seam without changing existing listeners or enabling TLS/UDP. Slice
4 bounded transport lifecycle is next and specified in
[`docs/network-infrastructure-phase2.md`](network-infrastructure-phase2.md),
but no TLS/UDP traffic has started. The issue is parked; Phase 2 continues
without Phase 1 acceptance. Records:
[`docs/network-infrastructure-phase1.md`](network-infrastructure-phase1.md).

## 1. Map And Session Foundation — Baseline Implemented

- Logged-in characters are tracked by account, character, and current map.
- New characters start in their faction capital: Sparta/camp 0 on map 0 and Athens/camp 1 on map 1; a one-time migration repairs characters created by the previous map-1 fallback.
- A character joins the visible map registry only after both `ClientReady` and the player-detail exchange have completed.
- Movement and chat are broadcast only to sessions in the same map instance.
- Two-client visibility sends server-built remote spawn, equipment/appearance, weapon and armor aura, position, and derived-status packets in both directions.
- Same-account relog behavior remains in place: a new login replaces the stale session.
- The post-login bootstrap now matches the working server's exact 63-record manifest and trailing version record. That server parity is retained, but dump analysis proved the intermittent first-attempt account-switch crash was also a distinct native client lifecycle defect.
- Loading-gate v1 (`2D8199...`) failed by starving native processing. V2
  (`73E65F...`) fixed scheduling but failed cycle 3 when its five-second
  unready handoff stayed blank beyond 44 seconds. Readiness-only V3
  (`17A721...`) still hit the native timeout/crash. V4 scheduled state 2 from
  exact AfterLogin, retained preview order until readiness, and guarded the
  timeout, but its final smoke failed earlier in login and is sealed `Fail`.
  It is rolled back and not accepted. See
  `docs/client-avatar-preview-loading-gate.md`.
- Captured opcode-10090 pages were identified in the native dispatcher as character-specific `MSG_PLAYER_ACCEPTQUESTS` records, not generic game-data bootstrap. Runtime replay is blocked until quests are implemented authoritatively; the separate dump diagnosis and future packet-order requirement are recorded in `docs/accepted-quest-login-crash-fix.md`.
- The later world-target crash at `0x00493A4E` was isolated to a null QuestView root in the client's target-reset path. `tools/PatchClientQuestViewTargetGuard.ps1` now guards both roots without re-entering the UI loader; an empty opcode-10090 packet was explicitly rejected as unsafe.
- Multiplayer synchronization itself remains server-side and requires no game client change; the avatar patch is a separate native stability correction.
- Continue two-account testing around reconnects, equipment changes, and future map transitions.
- Defer a separate map-server process until the in-process map boundary is clean.

## 2. Character Stats

- Build a `CharacterStats` calculation pipeline from class, level, gear, item quality, item grade, append attributes, holy suit, skills, and talents.
- Use derived stats in enter-game, player status refresh, player detail, combat, and item/talent updates.
- Keep database tables as source-of-truth and avoid duplicated stat mirrors unless they are generated compatibility views.
- Track experimental append attributes that are not fully wired into combat yet: vampiric/life-steal, damage reflect, attack speed percent, movement speed percent, cooldown reduction, boss damage, monster damage, player damage, player damage reduction, skill damage, normal attack damage, elemental damage/resistance, shield block, armor/magic penetration, tenacity percent, critical damage resistance, debuff duration reduction, buff duration bonus, holy damage, gold drop, item drop, and experience gain.
- Prototype `VampiricPer` and `ReflectDamagePer` in `ItemAppendAttribute.xml`; they must remain server/data-visible first, then be consumed by the combat resolver once combat damage is implemented.
- Armor rank progression is extended past AR10: AR11 at `12000`, AR12 at `17000`, AR13 at `22000`, and AR14 at `25300`.
- Equipment rank ceilings are profession-neutral and item-template-driven. The previous apparent Warrior WR7 limit came from comparing starter sword `1000` with Champion endgame spear `1435`; the level-135-only patch family had left starter and mid-tier rank tables on their native short curves.
- `tools/PatchClientGlobalEquipmentRanks.ps1` is the authoritative all-tier correction. Every ordinary forgeable weapon except special GM Spear `1499` shares the canonical Q20/G25 rank-score curve and reaches WR10 at score `8050` with five attributes; four attributes deliberately reach only `6780`/WR9. Physical classes use the physical aura-effect family, Priest the `201` family, and Mage the `51` family.
- Every forgeable `armor`/`cloth` carrier exposes the common AR14 curve through `25300`. Nonweapon Q1..Q10/G1..G12 scores remain native, while Q20 ends at three times Q10 and G25 at four times G12. A complete no-shield set scores `25350`; a shield adds `650`, keeping complete Warrior/Priest equipment at `26000`, below the signed-16-bit client boundary.

## 3. Skills And Talents

- Send a proper skill list from `character_skills` and skill templates.
- Implement skill unlock/upgrade rules and packet refreshes.
- Tighten talent requirements, costs, rank caps, and stat effects.
- Feed skill/talent effects into the shared stat calculator.

## 4. Static NPCs And Holy Stone Gameplay

- Static NPC spawn packets are built from server-owned definitions rather than replaying a raw city stream. Validated captures are preferred, normalized appearance/position references fill missing definitions, and same-number capital NPCs provide a fallback where one city lacks a position.
- NPC visibility is streamed per client using the working server's `32x32` sector grid. Bootstrap sends only the current `3x3` sector neighborhood; movement across a sector edge removes the old row or column before spawning the newly visible one.
- Map-specific object and interaction IDs are assigned deterministically. The Holy Stone Artisan resolves to the correct identity and script in both Sparta and Athens.
- Holy Stone Artisan dialog/action handling covers opcodes `10067`, `10068`, `10069`, and `10070`, including drilling, mounting, removing, validation, item mutation, and post-action item/visual/stat refreshes.
- PostgreSQL Holy Stone operations must use authoritative `character_items` rows and update only affected slots/socket columns; never round-trip the client-capped loadout view, because doing so can permanently lower unrelated extended quality/grade data.
- Before treating Holy Stone gameplay as complete, validate native behavior for stacked-stone consumption, invalid stone IDs, occupied sockets, full-bag removal, equipped-weapon argument ordering, and whether mounting may open the first socket without drilling.
- The current actor-table baseline resolves 108 Sparta and 111 Athens NPC identities. Sparta is imported from the exact recovered original-server `NPC.INI`; most NPC dialog scripts and all quest flows still need implementation.
- Add full NPC behavior/AI only after the static spawn and interaction baseline is stable.

## 5. Mobs, Bosses, And Combat

- Captured map-0 monster appearances now use the working server's `32x32` sector grid: bootstrap sends only the player's `3x3` neighborhood, and movement sends global-object removals before newly visible raw `10020` appearances.
- Capture ingestion recognizes the observed monster appearance-type variants by their shared low-byte `0x12` discriminator, but the current PostgreSQL baseline is still limited to 270 static Sparta/map-0 snapshots.
- Capture tools now require an explicit monster map for spawn upserts (`--monster-map-id` in the live proxy). The historical importer additionally requires `-CaptureSessionId` with `-MonsterMapId`, preventing template-only map guesses and cross-session mixing. Deriving the active map automatically from protocol session state remains future work.
- Captured spawns now feed one shared server-owned runtime per map. Monsters roam within an eight-unit home radius, cross visibility sectors live, retain authoritative HP, leave a timed corpse, and respawn at home.
- Aggroed monsters now leash at the same eight-unit home boundary. A lost or escaped target starts a smooth authoritative return leg; the monster evades during the reset, reaches its exact home position, restores full health, and only becomes attackable after the movement-end and health refresh have been serialized to viewers.
- Skill and ordinary `10026` attacks share monster HP and award one atomic kill reward. Fighter EXP uses the original 200-level threshold table with carry and `10030` level-up notices; normal-monster EXP and Talent EXP are persisted together.
- Ordinary melee attacks accept the exact 2.5-unit collision boundary and reconcile at most 0.5 units of the client-reported auto-approach position. This fixes legitimate warrior hits lost between the final attack animation and the last movement sample without trusting arbitrary client coordinates.
- Normal monsters are passive until damaged, then chase the attacker, strike on the captured cadence, persist player damage/death, and clear aggro on death, disconnect, map change, or leash failure. The original `10019` free-revive path returns dead players to their camp map with 10% HP/MP.
- Higher-tier monster attack extrapolation is isolated in `MonsterCombatResolver`; the captured normal-monster EXP multiplier and original tier curve are isolated in `MonsterRewardCatalog`; fighter thresholds live in `PlayerExperienceCatalog`. Update those catalogs when broader working-server captures replace the current normal-field assumptions.
- Fighter EXP modifiers use the original additive cross-kind rule. The server persists mutually exclusive consumable, weekend, event, and guild modifiers; resolves account-wide Bronze/Silver/Gold/Platinum VIP rates at 5/10/15/20 percent; and conditionally adds the 25-percent faction-area benefit. Talent EXP remains separate.
- Active EXP effects are composed into the native full-status snapshot on opcode `10167`; custom client statuses `1500` through `1504` identify the VIP tiers and faction-area control.
- The world-boss catalog selects one non-elite boss for each of 19 ready outdoor maps (`3`, `5`, and `6-22`). Capitals `0/1`, Newbie suburbs `2/4`, and timed dungeon/event instances are excluded. Parnassus (`68`) is explicitly eligible but pending a newly authored neutral boss; its faction Generals remain quest objectives. A winning faction controls only that map for the 12-hour boss cycle.
- World-boss control persistence and the 12-hour runtime respawn rule are implemented, but the live database still has only Sparta/map-0 normal spawn packets. Each selected boss still needs a captured or deliberately authored `10020` spawn packet, coordinates, tier, and HP before it can appear and be attacked.
- Dragging a kit-bag item onto the ground now handles the confirmed `10052` delete sentinel, deletes the owned slot through the audited item path, and returns the client acknowledgement that clears the slot.
- Add drops, multi-player threat selection, level-derived base-stat growth, and paid in-place revival economics after normal combat is validated in the client.
- Capture or author the 19 world-boss spawn packets, then add fixed twice-daily announcements and spawn windows around the implemented 12-hour lifecycle.

## 6. In-Game Time And Zodiac

- Opcode `10311` now returns a live 14-byte server-time packet using current Unix time and the original server's captured fixed UTC-8 offset instead of replaying a frozen 2021 timestamp.
- Opcode `10297`, module `0`, SID `1` now returns the mandatory 328-byte Zodiac state. Character creation preserves the selected Zodiac independently from Faith, and the core type, Lucky Day state/expiry, level, energy, and accumulated EXP fields persist in JSON and PostgreSQL.
- SID `3` Zodiac level-up is server-authoritative: client values are treated as intent, shipped level/energy requirements are revalidated, and the level plus energy deduction commit atomically.
- The full-sync state follows the native 16-grid array at state `+48`; captured grid 0/4/8/12 anchors and zero-based row markers are covered by golden protocol checks.
- SID `100` skill-grid activation is authoritative and persistent for all 16 grids. The shipped `UnlockG` premium-gold costs are revalidated and atomically deducted from legacy `Stone`; rejected requests cannot trigger a false client success.
- SID `101` skill-grid upgrades revalidate the shipped Zodiac-level gate plus `UpdateE` energy and `UpdateS` Talent Point costs, then atomically persist both resource deductions and the new grid level. Rejected requests cannot trigger the native client's unconditional success increment.
- Continuous-login energy now persists daily online duration across reconnects, awards only on five-minute boundaries, switches rate after the first three daily online hours, enforces the shipped per-level storage ceilings, and pushes client-decoded SID `5`. Daily rollover follows the original fixed UTC-8 clock; one-hour compensation covers a prior sub-hour day or an absence longer than one day. The numeric `20`/`10` tick rates are explicitly configurable emulator defaults because no stored retail SID `5` capture establishes them.
- The captured SID `7` accumulation frame and an atomic persistence primitive are recorded, but automatic accumulation is deferred until the stone/effect-level rate and cap semantics are confirmed. Lucky Day activation, stone upgrades, skill-grid selection, and the selected skills' combat effects remain separate gameplay slices rather than guessed economy mutations.
- See `docs/zodiac-sync-10297.md` for the confirmed packet layout, capture evidence, and remaining SID map.

## 7. Forging Materials And Inventory Tooling

- The server owns 23 forging-material records and seeds them into `item_templates`: 17 shipped records plus local Level-5 Sapphire `4215`, Emerald `4225`, Crystal `4234`, and their piece items `4216`, `4226`, and `4235`. The shipped `4214`/`4224` records remain Level-4 pieces and are never relabeled as Level-5 gems.
- Ruby levels 4-5 remain unavailable. The three local Level-5 definitions require matching client `ItemBaseAttribute.xml`, name, icon, and `BijouForge.xml` data; a server-only item ID would still render as missing or malformed.
- Level-5 Crystal, Sapphire, and Emerald use dedicated generated 36x36 sprites in `Icon4.gwo` at `0,0`, `36,0`, and `72,0`; their matching piece sprites use `108,0`, `144,0`, and `180,0`. The installer derives the atlas from the client's proven `Icon3.gwo` container and leaves the heavily shared stock `Icon2.gwo` pixels unchanged.
- The allowlisted `/gmitem add` chat command grants only catalogued materials, fills same-item/same-binding stacks first, allocates empty slots second, and rejects the entire operation when capacity is insufficient.
- PostgreSQL grants lock the character inventory but update only matching authoritative rows and new empty slots. They do not rewrite the compatibility loadout projection, preserving unrelated extended quality, grade, attributes, holy stones, and holy-suit data.
- Ordinary equipment forging now handles the native `10110` selection, `10109` Start/result, and `10117` session-reset messages. It imports all 611 shipped `EquipForge.xml` rules and the native `BijouForge.xml` material rules instead of inventing recipes.
- Ruby changes the equipment template, Sapphire raises quality through Q20/Boundless, Emerald raises an existing append-attribute grade through G25, and up to 25 Crystals add their probability bonus. The native loader reads `Round` as exactly two inclusive endpoints. Level-4 Sapphire remains `8,12` and Level-4 Emerald remains `10,17`; local Level-5 Sapphire is `8,19` at `+32`, Level-5 Emerald is `10,24` at `+32`, and Level-5 Crystal contributes `+25` per selected crystal. The added Q13..Q19 probability tail is `-255,-265,-275,-285,-295,-305,-315`, its silver multipliers are `35,40,45,50,55,60,65`, and Q20 is the zero terminal. The added G18..G24 tail is `-395,-420,-445,-470,-495,-520,-545`, its multipliers are `55,60,65,70,75,80,85`, and G25 is the zero terminal. At G24, 24 Level-5 Crystals produce raw `87%`; 25 produce raw `112%`, clamped to `100%`. Every legitimate roll atomically consumes the selected materials and silver; only a successful roll changes the equipment.
- PostgreSQL locks the character wallet and authoritative bag rows for each attempt. Stale selections, replays, invalid recipes, and insufficient funds are rejected without consuming anything.
- `tools/PatchClientForgeBoundlessGrade25.ps1` is the authoritative idempotent Q20/G25 client-data/executable patch. It extends every core numeric quality vector (including physical and magic damage absorption) and `BaseFraction` to 20 entries, extends only the grade-indexed `AppFraction` to 25, updates all eleven progression/candidate gates, changes 22 quality/base constructor counts to 20, and changes the `AppFraction` count at `0x373E0` to 25. `MainAttribute` is an allowed-attribute list; `ArmEffFraction`/`ArmEff` and `DefendFraction`/`DefendEff` are independent rank tables. Their XML and constructor counts remain byte-for-byte unchanged, because padding them can inflate aura/rank calculations. The full ceiling checklist is recorded in `docs/player-inspection-equipment-protocol.md`; the older Q13/G18 scripts are superseded and must not downgrade this installation.
- Apply `tools/PatchClientGlobalEquipmentRanks.ps1` after the ordinary-forge ceiling patch when reconstructing a client. This second guarded patch deliberately redesigns the independent rank tables and score tails across every ordinary item tier, keeps GM Spear `1499` and GM Armor `2190` untouched, and must be followed by `tools/GenerateItemTemplates.ps1` so PostgreSQL and generated server data use the same curves.
- Gear attribute enhancement reuses the client's shipped Gear Mentor and Origin Enhancer workflows for Add/Enhance/Delete. The Gear Mentor also authoritatively implements Decompose, Make Attribute Stones, Crystal downgrade transformation, and Level-4/5 piece combination. Instructions and Wash Dust remain reserved without inventory mutation. The forge modal hardcodes four native tabs, so an XML-only fifth tab or label-only rename would dispatch the wrong behavior. UI ownership and material recipes are recorded in `docs/gear-enhancement-ui.md` and `docs/gear-mentor-material-workflows.md`.
- Next: recover material-combination mode 1 and equipment-combination mode 2. Ordinary forging does not guess their result fields or economy rules.
