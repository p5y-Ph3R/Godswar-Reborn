# Zephyr Mount-Gear Spirit Roadmap

Status: design proposal only. No Zephyr item, socket, combat effect, or client
workflow is active yet.

## Identity

Use **Zephyr Holy Stone**, not "Aired Holy Stone." Zephyr is Greek-themed,
grammatically natural, and does not imply that ordinary Holy Spirits are still
Fire or Water elements.

- Proposed item ID: `9032` (unused in inspected client/server content)
- Proposed item key: `Stone9032`
- Affinity: `Zephyr`, separate from `Heated` and `Cooled`
- Eligible equipment: mount gear slots `15..19` only
- Ineligible equipment: character gear and the mount item in slot `20`
- Proposed texture: a new dedicated cell in `Icon5.gwo`

The stock client contains no third Holy Stone, no suitable unused stock icon,
and no mount-gear Holy Stone workflow. This feature therefore requires
versioned server content, client presentation work, persistence, and a secure
authoritative command path. It cannot be enabled as a name-only alias.

## Socket and activation rules

- Give each mount-gear item two dedicated Zephyr sockets, for ten possible
  sockets across the five-piece set. Do not overload the four ordinary
  `holy_socket*` fields or pretend that a mount is character vestment.
- Heated and Cooled Stones remain character-gear-only. Zephyr Stones remain
  mount-gear-only.
- The same Spirit cannot occupy both sockets on one mount-gear item, and at
  most two copies of one Spirit may be active across the entire set.
- Host-stat effects activate while that mount gear and a compatible mount are
  equipped. Movement, control, and charge effects activate only while the
  character is genuinely riding.
- Resolve and revalidate the five equipped mount-gear rows atomically when the
  Ride cast completes. Include the resolved Zephyr loadout in the runtime
  status fingerprint.
- Clear every ride-only effect immediately on dismount, death, invalid mount
  equipment, zone transfer, logout, or session ownership loss.
- Triggered effects use the strongest roll. Numeric effects may add only when
  their definition explicitly permits it and always stop at a server-owned
  loadout cap.
- The server validates every item instance, socket, roll, and equipped slot;
  client-reported Spirit effects are never authoritative.

## Recommended first effects

Zephyr Spirits must remain useful in every combat context. Do not create
monster-only damage, monster-only defense, experience, drop-rate, or Gold
effects.

| Spirit | Effect | Alpha-safe limit |
|---|---|---|
| Hermes Spirit of Celerity | Additional movement speed while riding | Additive bonus capped at `3%`; remains subject to authoritative movement budgets and every global speed cap |
| Daedalus Spirit of Attunement | Increases the native base stat contributed by the host mount-gear item | Strongest roll per item; `6%` cap; excludes append attributes, sockets, mount Speed, and values contributed by other items |
| Hephaestus Spirit of Tempering | Increases the grade-based append-attribute values contributed by the host mount-gear item | Strongest roll per item; `4%` cap; rounds once after aggregation and cannot amplify another Spirit |
| Boreas Spirit of Surefootedness | Reduces eligible slow duration while riding | Additive bonus capped at `20%`; never grants stun, silence, paralysis, pull, or knockback immunity |
| Poseidon Hippios Spirit of Stability | Reduces eligible forced-displacement distance while riding | Strongest roll only; `15%` cap; scripted displacement remains unaffected |
| Hippolyta Spirit of Readiness | Reduces Ride casting time | Additive bonus capped at `20%` with a server-owned minimum cast time; movement, stun, silence, and interruption still cancel the cast |
| Arion Spirit of Charge | After at least 12 metres of accepted mounted movement, empowers the next eligible direct attack | Strongest roll only; maximum `3%` applied-damage bonus and ten-second successful-trigger cooldown |
| Pegasus Spirit of Grace | After at least 12 metres of accepted mounted movement, reduces the next eligible direct hit received | Strongest roll only; maximum `5%` reduction and ten-second successful-trigger cooldown |

## Effects worth considering later

These need mechanics the repository does not currently own, so they must not
be advertised or implemented yet.

| Candidate | Possible effect | Required dependency |
|---|---|---|
| Bucephalus Spirit of Resolve | Reduces forced-dismount chance | A real server-owned forced-dismount mechanic |
| Castor Spirit of Convoy | Shares a small mounted movement benefit with nearby mounted party members | Party aura ownership, strongest-aura rules, interest management, and anti-stacking tests |
| Pollux Spirit of Escort | Shares a small control-duration benefit with nearby mounted party members | The same party-aura foundation and deterministic source selection |

Do not add raw attack, damage, Hit, Dodge, HP, critical chance, monster-only
stats, item-drop, Gold, or experience Spirits to this family. Attunement and
Tempering scale only the host item's existing authored contribution, allowing
mount-gear customization without publishing a second independent stat ladder.

## Implementation boundary

The initial implementation should be split into reviewable slices:

1. Capture/confirm the client workflow and choose the Zephyr socket UI.
2. Add versioned item content and a unique icon for item `9032`.
3. Add dedicated durable Zephyr socket fields or child rows plus migrations.
4. Add a bounded, idempotent mount-gear Spirit command executor.
5. Add ride-time aggregation and authoritative movement/combat enforcement.
6. Add forged-item, stale-cast, equipment-swap, reconnect, zone-transfer,
   dismount, death, and duplicate-command tests.

The existing Holy Stone Artisan operation named `Mount` means installing a
Holy Stone into drilled character gear; it is unrelated to riding an animal.
The Zephyr workflow must keep those meanings separate in code and UI.
