# Godswar Server Roadmap

This is the current step-by-step plan for moving the local C# server from packet replay toward real MMORPG gameplay.

The long-term secure networking migration is tracked in
[`network-infrastructure-goal.md`](network-infrastructure-goal.md). It selects
an in-process x86 shim, TLS control traffic, authenticated UDP realtime
traffic, bounded overload behavior, and upstream DDoS integration. V1–V5 are
rejected, restored history. PreviewReadyV6 completed original-client Baseline,
forced Fallback, ten-minute Soak, exact stock rollback, and a protected
completion receipt under campaign
`0a73fd79-961b-42c7-82cc-9e4a6f9e3355`. Secure defaults remain off after
rollback. Phase 5A replay, bounded load/soak, observability, and operations
now pass locally; production activation remains open. Records:
[Phase 1](network-infrastructure-phase1.md),
[Slice 8](network-infrastructure-phase2-slice8-activation.md),
[Slice 9A](network-infrastructure-phase3-slice9a-udp-foundation.md),
[Slice 9B](network-infrastructure-phase3-slice9b-authenticated-binding.md), and
[Slice 9C](network-infrastructure-phase3-slice9c-protected-udp.md), and
[Phase 4](network-infrastructure-phase4-authoritative-movement.md), and
[Phase 5A](network-infrastructure-phase5a-replay-load-observability.md).

## 1. Map And Session Foundation — Baseline Implemented

- Logged-in characters are tracked by account, character, and current map.
- New characters start in their faction capital: Sparta/camp 0 on map 0 and Athens/camp 1 on map 1; a one-time migration repairs characters created by the previous map-1 fallback.
- A character joins the visible map registry only after both `ClientReady` and the player-detail exchange have completed.
- Movement and chat are broadcast only to sessions in the same map instance.
- Two-client visibility sends server-built remote spawn, equipment/appearance, weapon and armor aura, position, and derived-status packets in both directions.
- Same-account relog behavior remains in place: a new login replaces the stale session.
- The post-login bootstrap now matches the working server's exact 63-record manifest and trailing version record. That server parity is retained, but dump analysis proved the intermittent first-attempt account-switch crash was also a distinct native client lifecycle defect.
- Loading-gate V1-V4 all failed acceptance and are rolled back. Exact behavior,
  evidence, and recovery remain in `docs/client-avatar-preview-loading-gate.md`.
- Captured opcode-10090 pages were identified in the native dispatcher as character-specific `MSG_PLAYER_ACCEPTQUESTS` records, not generic game-data bootstrap. Runtime replay is blocked until quests are implemented authoritatively; the separate dump diagnosis and future packet-order requirement are recorded in `docs/accepted-quest-login-crash-fix.md`.
- The later world-target crash at `0x00493A4E` was isolated to a null QuestView root in the client's target-reset path. `tools/PatchClientQuestViewTargetGuard.ps1` now guards both roots without re-entering the UI loader; an empty opcode-10090 packet was explicitly rejected as unsafe.
- Multiplayer synchronization itself remains server-side and requires no game client change; the avatar patch is a separate native stability correction.
- Continue two-account testing around reconnects, equipment changes, and future map transitions.
- All 81 stock-client runtime map IDs are catalogued. Ordinary authoritative
  walking transitions cover the verified reciprocal city/world graph for IDs
  `0-22`; dungeon, event, arena, and test maps remain gated behind explicit
  server admission rules. The support/content boundary and live
  `0 -> 4 -> 0` acceptance route are recorded in
  `docs/map-runtime-and-travel-support.md`.
- Defer a separate map-server process until the in-process map boundary is clean.

## 2. Character Stats

- Build a `CharacterStats` calculation pipeline from class, level, gear, item quality, item grade, append attributes, holy suit, skills, and talents.
- Use derived stats in enter-game, player status refresh, player detail, combat, and item/talent updates.
- Keep database tables as source-of-truth and avoid duplicated stat mirrors unless they are generated compatibility views.
- Life absorption and damage rebound now execute from damage actually committed,
  with exact-once source-event claims, missing-HP caps, and non-recursive reflected
  damage. Typed physical/magic reductions, flat absorption, critical resistance,
  weapon cadence/range, and reviewed pet/Holy/Owner-Merge channels feed the shared
  resolver. The remaining experimental append attributes stay data-visible only
  until each receives an authored cap, ordering rule, and replay-safe producer.
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
- Holy Stone dialog still covers opcodes `10067`-`10070`; exact captured `10069` Mount, Remove, and basic Drill now verify page-aware bagged-weapon selection, stacked-material consumption, invalid/occupied sockets, full-bag removal, and item/stat/visual refreshes.
- Secure Holy Stone mutations lock authoritative PostgreSQL `character_items`; basic Drill charges 230 then 2,300 Gold. Only affected rows, revisions, ledgers, and receipts are written, never the client-capped loadout view.
- Still open: capture the advanced third/fourth-socket drill materials and costs plus any non-Fire Holy Stone families. Mount requires an already drilled socket.
- The current actor-table baseline resolves 108 Sparta and 111 Athens NPC identities. Sparta is imported from the exact recovered original-server `NPC.INI`; most NPC dialog scripts and all quest flows still need implementation.
- Add full NPC behavior/AI only after the static spawn and interaction baseline is stable.

## 5. Mobs, Bosses, And Combat

- Base-combat evidence and the authored V1 contract are tracked in
  [the base-combat roadmap](base-combat-roadmap.md). Death/revive fencing,
  equipment-derived cadence/range, deterministic hit/critical resolution,
  physical/magic mitigation, hostile skills, typed secondary effects, and
  default-deny PvP basic attacks are implemented. Hostile PvP skills remain
  blocked until their result-packet semantics are captured.

- Captured map-0 monster appearances now use the working server's `32x32` sector grid: bootstrap sends only the player's `3x3` neighborhood, and movement sends global-object removals before newly visible raw `10020` appearances.
- Capture ingestion recognizes the observed monster appearance-type variants by their shared low-byte `0x12` discriminator, but the current PostgreSQL baseline is still limited to 270 static Sparta/map-0 snapshots.
- Capture tools now require an explicit monster map for spawn upserts (`--monster-map-id` in the live proxy). The historical importer additionally requires `-CaptureSessionId` with `-MonsterMapId`, preventing template-only map guesses and cross-session mixing. Deriving the active map automatically from protocol session state remains future work.
- Captured spawns now feed one shared server-owned runtime per map. Monsters roam within an eight-unit home radius, cross visibility sectors live, retain authoritative HP, leave a timed corpse, and respawn at home.
- Aggroed monsters may chase to the 32-unit combat leash, independently of the eight-unit idle-roam radius. A lost or escaped target starts a smooth authoritative return leg; the monster evades during reset, reaches home, restores full health, and becomes attackable only after viewers receive movement-end and health refresh.
- Skill and ordinary `10026` attacks share monster HP and award one atomic kill reward. Fighter EXP uses the original 200-level threshold table with carry and `10030` level-up notices; normal-monster EXP and Talent EXP are persisted together.
- Ordinary attacks use the reviewed equipped weapon's grade-specific range and
  cadence, with `1.7` units and `1500 ms` as the unarmed fallback. At most `0.5`
  units of client-reported auto-approach is reconciled; arbitrary client
  coordinates and request-tail bytes remain untrusted.
- Normal monsters are passive until damaged, then chase the attacker, strike on the captured cadence, persist player damage/death, and clear aggro on death, disconnect, map change, or leash failure. The preserved original `10019` type-2 free revive validates the local dead player before returning them to camp with 10% HP/MP; unsupported revive modes do not mutate state.
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
- Ruby changes the template, Sapphire raises quality through Q20, Emerald raises an append-attribute grade through G25, and up to 25 Crystals improve the roll. Exact ranges, tails, multipliers, and clamps live in `docs/player-inspection-equipment-protocol.md`. Every legitimate roll consumes materials and silver atomically; only success changes equipment.
- PostgreSQL locks the character wallet and authoritative bag rows for each attempt. Stale selections, replays, invalid recipes, and insufficient funds are rejected without consuming anything.
- `tools/PatchClientForgeBoundlessGrade25.ps1` is the authoritative idempotent Q20/G25 client patch. It changes only the reviewed vectors, gates, and constructor counts documented in `docs/player-inspection-equipment-protocol.md`; independent rank tables remain unchanged. The older Q13/G18 scripts are superseded.
- Apply `tools/PatchClientGlobalEquipmentRanks.ps1` after the ordinary-forge ceiling patch when reconstructing a client. This second guarded patch deliberately redesigns the independent rank tables and score tails across every ordinary item tier, keeps GM Spear `1499` and GM Armor `2190` untouched, and must be followed by `tools/GenerateItemTemplates.ps1` so PostgreSQL and generated server data use the same curves.
- Gear attribute enhancement reuses the client's shipped Gear Mentor and Origin Enhancer workflows for Add/Enhance/Delete. The Gear Mentor also authoritatively implements Decompose, Make Attribute Stones, Crystal downgrade transformation, and Level-4/5 piece combination. Instructions and Wash Dust remain reserved without inventory mutation. The forge modal hardcodes four native tabs, so an XML-only fifth tab or label-only rename would dispatch the wrong behavior. UI ownership and material recipes are recorded in `docs/gear-enhancement-ui.md` and `docs/gear-mentor-material-workflows.md`.
- Class Suit III/IV gear keeps five ordinary attributes, one profession-specific
  Class Suit attribute, and two different-element fields. The seven elemental
  status families and their cumulative 3/6/10 resonances execute through
  server-owned combat, movement, recovery, death, and reconnect state. The eight
  non-elemental stone prototypes remain gated in
  [the elemental attribute roadmap](class-suit-elemental-attribute-roadmap.md).
- Next: recover material-combination mode 1 and equipment-combination mode 2. Ordinary forging does not guess their result fields or economy rules.

## 8. Signed Client Updates — After Base Combat

- After base combat is stable, follow the [client update roadmap](client-launcher-updater-roadmap.md); stock binaries and live Git pulls remain forbidden.
