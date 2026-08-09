# Persistent Mount-Gear and Zephyr Spirit Roadmap

Status: Zephyr foundation implemented. Native mount-gear statistics and the
Daedalus/Hephaestus projection are active independently of Ride. The
Mnemosyne/Themis mitigation contracts are implemented, but remain dormant
until their corresponding hostile combat effects are introduced.

## Confirmed current behaviour

Mount gear is already a persistent secondary equipment set, not equipment that
switches on when the character casts Ride.

- Slots `15..19` are Mount Head, Mount Armor, Mount Soul, Mount Ornament, and
  Mount Amulet. Slot `20` is the mount.
- The PostgreSQL `character_stat_summary` projection reads all equipped item
  rows (`item_location = 0`), including slots `15..20`, without testing the
  Ride status.
- Consequently, each equipped mount-gear item's native stat, quality value,
  grade value, and append attributes apply while walking as well as while
  riding.
- Login/reconnect and authoritative equip/unequip operations rebuild those
  contributions from durable item rows.
- A compatible mount must still be equipped. Existing validation prevents
  equipping mount gear without one and prevents removing the mount while mount
  gear remains equipped.

This behaviour is intentional and must not be changed. Ride is a locomotion
and presentation state; it is not the power switch for mount gear.

## Separate runtime concerns

Keep these concerns separate in the implementation:

### `MountGearPassiveAggregate`

This aggregate is a server-authoritative view of valid installed Zephyr
effects. It remains active while the character has a compatible mount in slot
`20` and valid items in slots `15..19`, whether the character is mounted or on
foot. It records each valid host's equipment slot, item ID, and selected
Attunement/Tempering roll plus the strongest loadout-wide Preservation and
Continuity rolls.

The ordinary item-stat projection remains the owner of each mount-gear item's
native quality stat, grade-based append attributes, and ordinary attributes.
`PostgresMountGearPassiveProjectionSql` adds only the validated Attunement and
Tempering deltas to that projection; it does not duplicate the base values.
Recompose item statistics after login, reconnect, zone handoff, relevant
equipment or socket changes, and content-revision changes. The client never
supplies aggregate values.

### Ride runtime state

There is no concrete `RideRuntimeAggregate` type today. Ride state is held by
the server-owned mount runtime status and activation path. That state exists
only while Ride is active and contains:

- the mount model and animation status;
- the equipped mount's locomotion multiplier; and
- pending Ride-cast or dismount state.

Dismount and death clear this aggregate. They do **not** remove mount-gear
stats or Zephyr passives. Death may suspend combat-trigger evaluation and clear
unconsumed transient proc state, but the passive equipment loadout remains the
same and does not require re-equipping after revival.

| Character state | Mount-gear stats | Zephyr passives | Ride model/speed |
|---|---|---|---|
| Valid mount and gear equipped, walking | Active | Active | Inactive |
| Valid mount and gear equipped, riding | Active | Active | Active |
| Dead | Retained in authoritative stat aggregate; no combat evaluation | Retained; triggers suspended | Inactive |
| Reconnecting or changing zone | Rebuilt from durable equipment | Rebuilt from durable sockets | Restored only from valid session/zone state |
| Mount absent, expired, disabled, or incompatible | Invalid equipment state rejected by normal commands | Disabled fail-closed | Inactive |

## Mount-gear identity

The five stock pieces already have a useful permanent identity:

| Slot | Native contribution | Role |
|---:|---|---|
| `15` Mount Head/Coronet | Hit | Accuracy |
| `16` Mount Armor | Maximum HP | Endurance |
| `17` Mount Soul | Damage absorption | Mitigation |
| `18` Mount Ornament/Saddle | Maximum HP | Endurance |
| `19` Mount Amulet/Tassel | Dodge | Avoidance |

Quality improves these native contributions. Grade improves the item's
authored append attributes. Neither progression path depends on Ride. Maximum
HP recomposition must never heal the player: preserve current HP when maximum
HP rises and clamp it only when maximum HP falls.

Mount gear should remain a secondary, always-on investment layer rather than a
second copy of character Holy Spirits or elemental resonance. Its future
socket identity is **equipment reinforcement and passive counterplay**:

- Character Holy Spirits own active offensive mechanics such as mana burn,
  personal cooldown reduction, and hostile cooldown extension.
- Elemental attributes/resonance own Burn, Drench, Shock, Fracture, Gale,
  Dazzle, Wither, barriers, life steal, and elemental build thresholds.
- Mount-gear Zephyr Spirits reinforce authored mount-gear values or counter
  hostile resource/cooldown disruption. They remain active on foot.

Do not add another `3/6/10` resonance ladder for mount gear.

## Zephyr Stone identity

Use **Zephyr Holy Stone**, not "Aired Holy Stone." Zephyr is Greek-themed,
grammatically natural, and does not imply that ordinary Holy Spirits remain
Fire or Water elements.

- Item ID: `9032`
- Item key: `Stone9032`
- Affinity: `Zephyr`, separate from `Heated` and `Cooled`
- Eligible equipment: mount-gear slots `15..19` only
- Ineligible equipment: character gear and the mount item in slot `20`
- Texture: dedicated cell `612,0` in the locally owned `Icon5.gwo` atlas

The stock client contained no third Holy Stone, no suitable unused stock icon,
and no mount-gear Holy Stone workflow. The implementation therefore publishes
versioned server content, installs a dedicated icon, and adds authoritative
Holy Stone Artisan operation `801`; it is not a name-only alias.

## Zephyr socket and activation rules

- Each mount-gear item uses exactly the first two native client
  `holy_socket*` rows, for ten possible sockets across the five-piece set.
  This is a wire-compatibility choice: the original client already renders
  those two rows, but cannot render an independent child-socket packet.
  Server item-kind validation makes the storage namespaces logical rather
  than physical: mount gear accepts only Zephyr effects in rows 1-2, while
  character gear accepts only Heated/Cooled effects in its rows 1-4.
- Heated and Cooled Stones remain character-gear-only. Zephyr Stones remain
  mount-gear-only.
- A mount-gear item cannot contain the same Spirit twice.
- Host-local reinforcement Spirits activate only on the two pieces with the
  strongest rolls. A global countermeasure uses the strongest installed roll
  only. Extra loadout copies provide no additional benefit; the current
  command path rejects duplicates on one item but does not yet reject a
  harmless inactive copy installed on another item.
- Every Zephyr effect is part of `MountGearPassiveAggregate` and remains active
  while walking. Toggling Ride never enables, disables, or rerolls it.
- Numeric effects may add only where their definition explicitly permits it
  and always stop at a server-owned, all-source cap.
- The server validates item ownership, item instance, eligible slot, socket,
  Spirit identity, level, effectiveness roll, and content revision. Client
  descriptions and reported effect values are never authoritative.

## Implemented initial Zephyr effects

The first set deliberately contains no movement-triggered damage proc and no
ride-only combat power. Effectiveness uses the existing Holy Spirit wire unit
(one hundredth of one percent) and a server-owned random roll inside each
level bracket.

| Spirit | Persistent effect | Alpha-safe limit |
|---|---|---|
| Daedalus Spirit of Attunement | Increases only the native quality/base stat contributed by its host mount-gear item | Strongest roll on that host; cap `3%`; excludes append attributes, socket effects, mount Speed, and every other item |
| Hephaestus Spirit of Tempering | Increases only the grade-based append-attribute values contributed by its host mount-gear item | Strongest roll on that host; cap `2%`; calculate from the authored pre-Spirit value and round once after aggregation |
| Mnemosyne Spirit of Preservation | Reduces MP removed by an eligible hostile mana-burn effect | Strongest roll across the loadout; cap `20%` in PvE and `12%` in PvP; cannot generate or restore MP |
| Themis Spirit of Continuity | Reduces only the extra cooldown imposed by an eligible hostile cooldown-extension debuff | Strongest roll across the loadout; cap `15%` in PvE and `10%` in PvP; cannot shorten a skill's ordinary cooldown |

| Spirit | Item/effect | Level `L` roll bracket |
|---|---|---|
| Daedalus | `9090` / `21` | `0.15% × L` through `0.30% × L` |
| Hephaestus | `9091` / `22` | `0.10% × L` through `0.20% × L` |
| Mnemosyne | `9092` / `23` | `1.00% × L` through `2.00% × L`, then the PvE/PvP cap |
| Themis | `9093` / `24` | `0.75% × L` through `1.50% × L`, then the PvE/PvP cap |

Attunement and Tempering ship with the Zephyr foundation. Preservation and
Continuity have bounded, tested pure mitigation contracts, but are not applied
by combat yet because no server-authoritative hostile mana-burn or
cooldown-extension producer exists. The client text describes their intended
scope without pretending an absent producer is active.

## Deferred Zephyr candidates

These are potential persistent countermeasures, not approved launch effects.
Each requires a real authoritative mechanic and a non-overlap review first.

| Candidate | Possible always-equipped effect | Required dependency |
|---|---|---|
| Athena Spirit of Composure | Reduces only damage-induced casting pushback or interruption buildup | A server-owned damage-interruption mechanic distinct from stun, silence, movement interruption, and elemental control |
| Hestia Spirit of Constancy | Reduces only hostile increases to MP skill costs | A bounded skill-cost-increase debuff |
| Nike Spirit of Defiance | Reduces hostile outgoing-damage-suppression potency, restoring damage only toward its normal value | A typed outgoing-damage-suppression mechanic and shared cap |
| Bucephalus Spirit of Resolve | Reduces forced-dismount chance | A real server-owned forced-dismount mechanic |
| Castor Spirit of Convoy | Shares a small non-stacking utility benefit with nearby party members | Party aura ownership, deterministic source selection, interest management, and anti-stacking tests |

Do not add raw attack, generic damage, Hit, Dodge, HP, critical chance, life
steal, direct mana burn, ordinary cooldown reduction, cooldown extension,
monster-only stats, item-drop rate, Gold, or experience to the Zephyr family.
Those either duplicate another progression family or turn ten extra sockets
into a mandatory general-power ladder.

## Required competitive-release safeguards

The current foundation validates durable items, compatible affinities, socket
limits, rolls, and command replay. The following broader combat/swap rules are
requirements for the later hostile-effect and competitive-combat slice; they
must not be read as already completed:

- Reject mount and mount-gear transfers while the character has a pending cast
  or is inside the server-owned hostile-combat equipment lock window.
- Recompose from locked authoritative item-instance rows in one transaction,
  then increment a character loadout generation. A committed cast or hit pins
  one generation so one event cannot use two equipment states.
- A debuff records the resistance result at application time. Later equipment
  changes cannot modify that already-applied debuff retroactively.
- Dismount never clears the passive fingerprint. Reconnect and zone transfer
  rebuild it from durable equipment and sockets.
- Any future trigger cooldown belongs to the character, not the item, and
  survives swapping, reconnect, zone transfer, and death. Unequipping cannot
  reset it.
- PvP/PvE classification is server-owned. All-source caps include skills,
  character Spirits, elemental effects, and later systems.
- Zephyr-produced events must carry non-recursive provenance and cannot trigger
  themselves, critical hits, life steal, reflection, or another proc unless a
  later effect explicitly proves that safe.
- Pets and summons never inherit their owner's mount-gear aggregate.

## Implemented foundation slices

The foundation was delivered in these reviewable slices:

1. Added `MountGearPassiveAggregate` tests for compatible mounts, native socket
   limits, strongest/top-two selection, invalid rolls, and Ride independence;
   the ordinary item projection remains the owner of base and append stats.
2. Added Holy Stone Artisan operation `801` and its stock-style one-slot client
   page.
3. Published Zephyr items `9032` and `9090..9093` with unique `Icon5.gwo`
   sprites.
4. Partitioned the client's native socket rows by authoritative item kind and
   capped mount gear at rows 1-2.
5. Routed drilling, implementation, mounting, removal, upgrading, and
   combination through bounded idempotent commands.
6. Projected Attunement and Tempering from durable equipped items, independent
   of Ride; exposed the two future mitigation contracts separately.
7. Added protocol, replay, forged-roll, incompatible-affinity, missing-mount,
   duplicate-command, and Ride-independent projection coverage to the release
   gate.

The existing Holy Stone Artisan operation named `Mount` means installing a
Holy Stone into drilled character gear; it is unrelated to riding an animal.
The Zephyr workflow must keep those meanings separate in code and UI.
