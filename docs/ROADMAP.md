# Godswar Server Roadmap

This is the current step-by-step plan for moving the local C# server from packet replay toward real MMORPG gameplay.

## 1. Map And Session Foundation

- Track logged-in characters by account, character, and current map.
- Broadcast movement/chat only to sessions in the same map instance.
- Keep same-account relog behavior: a new login replaces the stale session.
- Add enough logging to test two different accounts on the same PC.
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

## 4. Holy Stone Gameplay

- Implement NPC dialog/action handling for Holy Stone Artisan opcodes `10067`, `10068`, `10069`, and `10070`.
- Support drilling, mounting stones, removing stones, currency/material validation, and item mutation.
- Refresh item state, equipment visuals, and character stats after each successful action.

## 5. Mobs, Bosses, And Combat

- Convert captured mob spawns into server-owned monster state per map instance.
- Add HP, death, respawn, drops, threat, and movement/AI.
- Add world boss scheduling after normal mob state and combat are stable.
