# Class Suit elemental attribute roadmap

Status: implementation contract for the seven Greek-themed elemental stones,
their 21 gear-slot-derived attributes, authoritative item persistence, typed
stat profile, and unique 3/6/10 resonance definitions. The authoritative
equipment profile exposes active cumulative definitions and their exact
parameters. Executing those effects remains a separately tested milestone.
The eight non-elemental prototypes remain roadmap-only.

## Repository evidence and boundary

- The generated item catalog currently contains 1,206 unique IDs from `1000`
  through `16209`. The block `16300..16328` is unused in both the generated
  server seed and the checked-in original client item XML.
- The generated attribute catalog currently ends at `470`. IDs `480..506` are
  unused. Existing Class Suit attributes use `200`, `201`, `210`, `211`, `220`,
  `221`, `230`, and `231`; life steal and reflection prototypes already use
  `460` and `470`.
- Regular equipment is represented by slots `0..11` in
  `src/Godswar.Server/State/EquipmentSlots.cs`. The current Holy/Ware Suit
  calculation also examines these twelve slots.
- The current Ware/Holy Suit is **not** a 3/6/10 set system.
  `EquipSuitInfoIni.xml` exposes display conditions `1/5/8/10`, while
  `EquipEffectSuit.xml` publishes effects at `5/10/20/40/60/80/100/120`
  Holy Suit points. Elemental resonance therefore has its own name, state, and
  rules. It must not change `holy_suit_points` or reuse Ware Suit unlock rows.

The original client cannot infer names, colors, or behavior for new item and
attribute IDs. A release must publish matching server content, database
content, client XML/text/icon content, tooltip decoding, and protocol support.
Unknown or mixed-version content fails closed.

## Reserved elemental catalog

Each element owns one consumable stone and three consecutive applied
attributes. The authoritative gear slot chooses a semantic role when the stone
is consumed. The immutable `Power`, `Penetration`, and `Resistance` families
still select weapons; helmets, gloves, and rings; and defensive gear
respectively, but the visible meaning is the exact effect label below.

| Element | Stone | Item | Effect identity | UI color | Effect attr | Resistance attr | Chance attr |
|---|---|---:|---|---:|---:|---:|---:|
| Fire | Prometheus Stone | `16300` | Burn | `#FF5A36` | `480` | `481` | `482` |
| Water | Poseidon Stone | `16303` | Drench | `#2F8CFF` | `483` | `484` | `485` |
| Lightning | Zeus Stone | `16306` | Shock | `#F2C94C` | `486` | `487` | `488` |
| Earth | Gaia Stone | `16309` | Fracture | `#9B6A3A` | `489` | `490` | `491` |
| Wind | Aeolus Stone | `16312` | Gale | `#42C99A` | `492` | `493` | `494` |
| Light | Apollo Stone | `16315` | Dazzle | `#F4E7A1` | `495` | `496` | `497` |
| Dark | Hades Stone | `16318` | Wither | `#7653B8` | `498` | `499` | `500` |

The stone tooltip lists the slot rule because an inventory item cannot know
which gear the player will choose later. After a successful operation, the
persisted role-specific attribute ID makes the gear tooltip show an exact
element-themed effect label. Color is supplemental presentation only. Immutable
item-content manifest v9 publishes the seven active stones and retains the 14
manifest-v8 item identities only as non-issuable compatibility tombstones.
The active icons occupy columns `648,684,720,756,792,828,864` at row `180` in
the table order above. Existing equipped attribute IDs `480..500` do not
change.

The client V2 sheet gives each stone its matching Greek-deity symbol:
Prometheus's flame, Poseidon's trident, Zeus's thunderbolt, Gaia's mountain and
olive branch, Aeolus's winged wind spiral, Apollo's sun and lyre, and Hades's
bident and underworld crown. Only the seven row-`180` cells are active; the two
retired family-specific rows remain transparent.

The following labels are the player-facing contract:

| Element | Weapon | Helmet, gloves, rings | Defensive gear |
|---|---|---|---|
| Fire | `[Burn] Damage over time` | `[Burn] On-hit chance` | `[Burn] Damage resistance` |
| Water | `[Drench] Movement slow` | `[Drench] Slow chance` | `[Drench] Slow resistance` |
| Lightning | `[Shock] Paralyze duration` | `[Shock] Paralyze chance` | `[Shock] Paralyze resistance` |
| Earth | `[Fracture] Defense reduction` | `[Fracture] Defense-break chance` | `[Fracture] Defense-break resistance` |
| Wind | `[Gale] Movement speed` | `[Gale] Movement activation chance` | `[Gale] Slow resistance` |
| Light | `[Dazzle] Accuracy reduction` | `[Dazzle] Accuracy-reduction chance` | `[Dazzle] Accuracy-loss resistance` |
| Dark | `[Wither] Healing reduction` | `[Wither] Healing-suppression chance` | `[Wither] Healing-reduction resistance` |

The old `Power`, `Resistance`, and `Penetration` enum members, XML tags,
database name keys, stat types, and V8/V9 material names remain unchanged as
opaque compatibility identities. They must not be presented as gameplay
semantics and changing them would require a new immutable content manifest.
This runtime/display-label revision does not alter those identities and does
not create a manifest v10.

These are percentage attributes. The runtime profile stores integer basis
points (`100` means one percent), avoiding floating-point drift. Content
publication may retain decimal fractions where the existing attribute schema
requires them, but conversion into basis points happens once at the content
adapter and client presentation converts back only for display.

### Shared grade curve

All seven elements use the same curve. Asymmetric elemental numbers would make
the strongest element a content accident rather than a player choice.

| Gear grade | Effect Potency | Effect Resistance | Application Chance |
|---:|---:|---:|---:|
| 1 | 0.40% | 0.40% | 0.20% |
| 5 | 2.00% | 2.00% | 1.00% |
| 10 | 4.00% | 4.00% | 2.00% |
| 15 | 6.00% | 6.00% | 3.00% |
| 20 | 8.00% | 8.00% | 4.00% |
| 25 | 10.00% | 10.00% | 5.00% |

For every grade, Effect Potency and Effect Resistance are `40 * grade` basis
points and Application Chance is `20 * grade` basis points. Clamp grade to
`1..25`; never extrapolate from untrusted client values. These values remain
typed elemental-effect totals. They are not a license to add the same
percentage to current global physical or magical fields.

The intended execution meanings are deliberately narrow. Burn is periodic
damage. Drench slows authoritative movement. Shock prevents movement for a
bounded paralyze duration. Fracture reduces physical and magical defense. Gale
is a movement-speed boost for the equipped player. Dazzle reduces hit rate,
and Wither reduces healing received. Burn, Drench, Shock, Fracture, Dazzle,
and Wither roll their chance only after an authoritative attack has committed;
rejected, cancelled, or purely visual attacks cannot trigger them. Gale is the
exception: it rolls after authoritative movement is accepted and activates the
self movement-speed boost. Chance rolls, durations, caps, boss immunity, and
resistance are all server-owned. These labels define the future execution
contract only: the combat, crowd-control, healing, and movement processors
remain disabled in this slice.

## Elemental resonance at 3/6/10

A gear item contributes one count to an element when either of its two
elemental Class Suit fields contains an attribute from that element. The two
elemental fields must name **different** elements; a second attribute from the
same element is rejected even when it belongs to another role. Therefore one
gear item may contribute once to each of two different resonance tracks, but
never twice to one track. The profession-specific Class Suit field remains
separate from these two elemental fields. Only equipped, owned, valid Class
Suit III/IV items in regular slots `0..11` count. Each element's count is
capped at ten, allowing two-handed and shield-using classes to reach the same
ceiling without changing equipment-slot rules.

Tiers are cumulative and each element has its own locked contract. Basis points
(`bp`) use `10,000 bp = 100%`. Distances are authoritative world distance and
all hit counters consume only accepted server-side events.

| Element | 3 matching pieces | 6 matching pieces | 10 matching pieces |
|---|---|---|---|
| Prometheus / Fire | A committed direct hit applies one non-stacking 3-second Burn for `600 bp` of that hit, split into three ticks. A weaker hit never lowers an existing Burn. | Replaces the 3-piece Burn with `1,000 bp` over four seconds and four ticks; it retains the non-stacking, strongest-source rule. | Every fifth committed direct hit detonates all remaining Burn damage, adds `1,200 bp` of the triggering hit, then reapplies the eligible Burn. |
| Poseidon / Water | Every six seconds restore HP and MP equal to `100 bp` of each respective maximum. | Every fifth incoming direct hit has its final damage reduced by `2,500 bp`. | When that guard activates, restore HP equal to `5,000 bp` and MP equal to `2,500 bp` of prevented damage, capped at `300 bp` of the respective maximum resource. |
| Zeus / Lightning | Every fourth committed direct hit adds a bolt for `1,500 bp` of its applied damage. | The nearest additional enemy within 5 metres receives `1,000 bp` of the original applied hit. | A second additional enemy receives `500 bp`, and the primary target is stunned for one second when it is not a boss. |
| Gaia / Earth | Maximum HP increases by `800 bp`. | Final incoming damage is reduced by `800 bp`. | Reflect `1,500 bp` of post-mitigation damage, capped at `200 bp` of the attacker's maximum HP. Reflected damage cannot recursively reflect. |
| Aeolus / Wind | Movement speed increases by `500 bp`. | After 5 metres of accepted movement, the next hit within three seconds gains `1,000 bp` damage and consumes Momentum. | Every sixth incoming hit is evaded. |
| Apollo / Light | Each eligible authoritative recovery pulse is amplified by `1,000 bp`. | `5,000 bp` of overhealing becomes a barrier, capped at `1,000 bp` of maximum HP. | A positive barrier can be consumed to prevent lethal damage and leave the target at exactly 1 HP. |
| Hades / Dark | Heal for `200 bp` of applied damage, capped at `200 bp` of maximum HP per hit. | Damage against a target below `2,500 bp` HP gains `1,200 bp`. | A credited kill restores HP and MP equal to `800 bp` of each respective maximum. |

A mixed loadout can activate more than one track. Resonance does **not** add
generic values to the grade-scaled elemental-effect totals.
`ElementalResonanceCatalog` is the one typed
definition source, while `ElementalEquipmentProfile.ActiveResonanceTiers`
projects the active `[3]`, `[3,6]`, or `[3,6,10]` definitions for each element.
The projection defines entitlement and exact parameters; it does not claim that
the combat, recovery, movement, or crowd-control execution paths are wired yet.

## GWA3 item-record contract

The native item record remains exactly 72 bytes. `GWA3` replaces neither the
five native ordinary fields nor the final identity fields:

| Record offset | Size | GWA3 meaning |
|---:|---:|---|
| `+4..+20` | 20 | Five native ordinary attribute IDs, unchanged |
| `+52` | 4 | One signed profession-specific Class Suit attribute ID; `-1` is empty |
| `+56` | 4 | Packed elemental IDs: low 16 bits are slot 1, high 16 bits are slot 2; `0xFFFF` is empty |
| `+60` | 4 | Exact little-endian marker `0x33415747`, bytes `47 57 41 33` (`GWA3`) |
| `+64..+71` | 8 | Existing source/owner/slot identity, unchanged |

Every elemental ID fits in unsigned 16 bits (`480..500`). Decode neither half
unless the marker is exact, reject values outside the published catalog, and
canonicalize an empty first half plus populated second half rather than
accepting an ambiguous durable shape. The existing exact `GWA2` path remains a
backward-compatible read path during migration; new writes use `GWA3`.

The patched client's internal item object uses tagged, aligned pointers rather
than adding fields beyond its fixed allocation: the Class Suit pointer carries
tag bit `1`, elemental slot 1 remains aligned, and elemental slot 2 carries tag
bit `2`. Tooltip code verifies the complete tag pattern and clears the low two
bits before any dereference. It must test the exact legacy `GWA2` marker path
first. A partial tag pattern, unknown marker, or unknown ID renders no extension
instead of dereferencing attacker-controlled data.

## Separately gated combat patch

This slice owns content identity, authoritative item state, typed elemental
totals, resonance entitlement/parameter calculation, protocol projection, and
UI presentation only.
Skills and ordinary attacks do not yet carry an authoritative element, so the
server must not silently reinterpret Effect Potency as global physical/magic
damage, Effect Resistance as global absorption, or Application Chance as an
existing critical, hit, or defense-ignore field.

A later execution patch must consume the locked definitions without copying
their constants into gameplay systems. It requires a separate review and must:

1. let skill/content data, never the client packet, choose the attack element;
2. define Neutral behavior for unclassified skills and ordinary attacks;
3. select and test all-source caps independently for Effect Potency, Effect
   Resistance, and Application Chance before any typed total affects combat;
4. define counter reset, death, reconnect, dispel, boss-immunity, target-order,
   and attribution behavior for every unique resonance trigger;
5. guarantee resistance cannot invert an effect and application chance cannot
   exceed its reviewed authoritative cap;
6. apply the elemental stage exactly once at a documented point in the current
   physical/magic damage pipeline; and
7. ship PvE/PvP telemetry and rollback controls with that activation.

Snapshots and tooltips remain projections. Equip, add, delete, conversion, and
the future combat resolver always reread authoritative item state and the
pinned content revision.

The implementation must reject duplicate attribute IDs on one item, a second
profession-specific or third elemental Class Suit attribute, elemental
attributes on Class Suit I/II or Common gear, two attributes of the same
element on one item, unknown IDs, stale expected state, forged slots, and
replayed operation IDs without consuming a stone or Flame Spark.

## Roadmap-only non-elemental prototypes

The following IDs are reserved but must not be seeded as usable stones yet.
Styx and Nemesis retain the already generated attribute semantics `460` and
`470`. Their old documentation-only item proposals `9986` and `9987` were never
present in the item seed or original client XML and are superseded by the
contiguous reserved identities below.

| Prototype stone | Item ID | Attribute ID | Intended authoritative rule | Required safety limit |
|---|---:|---:|---|---|
| Styx Stone | `16321` | `460` (`VampiricPer`) | Heal from final direct damage actually dealt | 10% loadout cap; no DoT, reflection, proc, or over-heal |
| Nemesis Stone | `16322` | `470` (`ReflectDamagePer`) | Return a portion of final direct damage received | 15% cap; cannot reflect recursively, crit, life-steal, or trigger procs |
| Aegis Stone | `16323` | `501` (`DamageAbsorbPer`) | Reduce final incoming damage | 10% cap; applied once after typed mitigation; never grants immunity |
| Nike Stone | `16324` | `502` (`MoveSpeedPer`) | Increase authoritative movement speed | 10% cap; cannot bypass server movement budgets or stack twice with a mount |
| Asclepius Stone | `16325` | `503` (`HealingReceivedPer`) | Increase authoritative healing received | 20% cap; excludes fixed revive HP and cannot change maximum HP |
| Chronos Stone | `16326` | `504` (`CooldownReductionPer`) | Reduce server-owned skill cooldowns | 15% cap and a 50% base-cooldown floor; server monotonic time only |
| Moirai Stone | `16327` | `505` (`CriticalDamageResistancePer`) | Reduce bonus critical damage received | 20% cap; cannot reduce a critical below its normal-hit damage |
| Thanatos Stone | `16328` | `506` (`ExecuteDamagePer`) | Bonus direct damage below 25% target HP | 10% cap; pre-hit HP check, no DoT/proc chaining, reduced or disabled for bosses |

Before any prototype is enabled it needs an individual ADR or balance ticket,
a server combat implementation, client presentation, migration/content
publication, cap tests across a complete loadout, PvE/PvP tests, replay and
duplicate-consumption tests, and a rollback that removes availability without
destroying already persisted player value.

## Acceptance gate

- Static catalog checks prove all item and attribute IDs are unique and inside
  the wire/database numeric domains.
- Golden client/server records cover the profession-specific field and zero,
  one, and two elemental fields under the new marker/version; older clients
  never receive unsupported content.
- Add/Delete/convert/equip transactions prove exact-once material use and
  preserve five ordinary attributes independently from one profession-specific
  and two elemental Class Suit fields.
- Tests cover every grade anchor, slot `0..11`, two-handed versus shield gear,
  same-element pair rejection, two-track counting, count capping, 3/6/10
  boundary transitions, stale requests, and retries.
- Character inspection and relog show the same two fields, names, values, and
  colors; another player sees the same authoritative result.
- Before combat activation, dedicated tests must demonstrate that matching
  resistance never inverts an effect, application chance cannot exceed its
  reviewed cap, and no elemental modifier is applied twice.
