# Godswar Server Roadmap

This is the current step-by-step plan for moving the local C# server from packet replay toward real MMORPG gameplay.

## 1. Map And Session Foundation — Baseline Implemented

- Logged-in characters are tracked by account, character, and current map.
- A character joins the visible map registry only after both `ClientReady` and the player-detail exchange have completed.
- Movement and chat are broadcast only to sessions in the same map instance.
- Two-client visibility sends server-built remote spawn, equipment/appearance, weapon and armor aura, position, and derived-status packets in both directions.
- Same-account relog behavior remains in place: a new login replaces the stale session.
- This synchronization is server-side and requires no game client code changes for a client that is already pointed at the server.
- Continue two-account testing around reconnects, equipment changes, and future map transitions.
- Defer a separate map-server process until the in-process map boundary is clean.

## 2. Character Stats

- Build a `CharacterStats` calculation pipeline from class, level, gear, item quality, item grade, append attributes, holy suit, skills, and talents.
- Use derived stats in enter-game, player status refresh, player detail, combat, and item/talent updates.
- Keep database tables as source-of-truth and avoid duplicated stat mirrors unless they are generated compatibility views.
- Track experimental append attributes that are not fully wired into combat yet: vampiric/life-steal, damage reflect, attack speed percent, movement speed percent, cooldown reduction, boss damage, monster damage, player damage, player damage reduction, skill damage, normal attack damage, elemental damage/resistance, shield block, armor/magic penetration, tenacity percent, critical damage resistance, debuff duration reduction, buff duration bonus, holy damage, gold drop, item drop, and experience gain.
- Prototype `VampiricPer` and `ReflectDamagePer` in `ItemAppendAttribute.xml`; they must remain server/data-visible first, then be consumed by the combat resolver once combat damage is implemented.
- Extend armor rank progression past AR10 for high-end gear tests: AR11 at `12000`, AR12 at `17000`, and AR13 at `22000`.

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
- The current PostgreSQL baseline resolves 100 Sparta and 95 Athens NPC identities, including 48 directly normalized references in each city. Most other NPC dialog scripts and all quest flows still need implementation.
- Add full NPC behavior/AI only after the static spawn and interaction baseline is stable.

## 5. Mobs, Bosses, And Combat

- Captured map-0 monster appearances now use the working server's `32x32` sector grid: bootstrap sends only the player's `3x3` neighborhood, and movement sends global-object removals before newly visible raw `10020` appearances.
- Capture ingestion recognizes the observed monster appearance-type variants by their shared low-byte `0x12` discriminator, but the current PostgreSQL baseline is still limited to 270 static Sparta/map-0 snapshots.
- Capture tools now require an explicit monster map for spawn upserts (`--monster-map-id` in the live proxy). The historical importer additionally requires `-CaptureSessionId` with `-MonsterMapId`, preventing template-only map guesses and cross-session mixing. Deriving the active map automatically from protocol session state remains future work.
- Convert captured mob spawns into server-owned monster state per map instance. Replace frozen capture positions with live sector membership when monster movement is implemented.
- Add HP, death, respawn, drops, threat, and movement/AI.
- Add world boss scheduling after normal mob state and combat are stable.
