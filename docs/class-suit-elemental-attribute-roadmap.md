# Class Suit elemental attribute roadmap

Status: implementation contract for the current elemental catalog,
authoritative item persistence, typed stat profile, 3/6/10 resonance totals,
protocol projection, and patched-client UI. Applying those typed totals to
combat remains separately gated because skills and attacks do not yet have an
authoritative element. The eight non-elemental prototypes remain roadmap-only.

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

Each element owns three consecutive attributes and three matching stones.
Power increases outgoing damage of that element, Resistance reduces incoming
damage of that element, and Penetration ignores only the matching Resistance.

| Element | Signature | UI color | Power | Resistance | Penetration |
|---|---|---:|---:|---:|---:|
| Fire | Volcanic: direct pressure and burst | `#FF5A36` | attr `480`, item `16300` | attr `481`, item `16301` | attr `482`, item `16302` |
| Water | Tidal: sustain and measured defense | `#2F8CFF` | attr `483`, item `16303` | attr `484`, item `16304` | attr `485`, item `16305` |
| Lightning | Tempest: fast, precise pressure | `#F2C94C` | attr `486`, item `16306` | attr `487`, item `16307` | attr `488`, item `16308` |
| Earth | Bastion: stability and durability | `#9B6A3A` | attr `489`, item `16309` | attr `490`, item `16310` | attr `491`, item `16311` |
| Wind | Zephyr: mobility and evasion | `#42C99A` | attr `492`, item `16312` | attr `493`, item `16313` | attr `494`, item `16314` |
| Light | Radiant: protection and restoration | `#F4E7A1` | attr `495`, item `16315` | attr `496`, item `16316` | attr `497`, item `16317` |
| Dark | Umbral: attrition and finishing pressure | `#7653B8` | attr `498`, item `16318` | attr `499`, item `16319` | attr `500`, item `16320` |

The item names are `<Element> Power Stone`, `<Element> Resistance Stone`, and
`<Element> Penetration Stone`. Color is supplemental presentation only: every
tooltip and item name contains both the element and family, so players are not
forced to distinguish the stones by hue. This first client slice reuses one
existing `Icon2.gwo` cell per element; a future art pass may add separate
Power/Resistance/Penetration glyphs without changing item IDs.

These are percentage attributes. The runtime profile stores integer basis
points (`100` means one percent), avoiding floating-point drift. Content
publication may retain decimal fractions where the existing attribute schema
requires them, but conversion into basis points happens once at the content
adapter and client presentation converts back only for display.

### Shared grade curve

All seven elements use the same curve. Asymmetric elemental numbers would make
the strongest element a content accident rather than a player choice.

| Gear grade | Power | Resistance | Penetration |
|---:|---:|---:|---:|
| 1 | 0.40% | 0.40% | 0.20% |
| 5 | 2.00% | 2.00% | 1.00% |
| 10 | 4.00% | 4.00% | 2.00% |
| 15 | 6.00% | 6.00% | 3.00% |
| 20 | 8.00% | 8.00% | 4.00% |
| 25 | 10.00% | 10.00% | 5.00% |

For every grade, Power and Resistance are `40 * grade` basis points and
Penetration is `20 * grade` basis points. Clamp grade to `1..25`; never
extrapolate from untrusted client values. These values remain typed elemental
totals. They are not a license to add the same percentage to the current global
physical or magical damage fields.

## Elemental resonance at 3/6/10

A gear item contributes one count to an element when either of its two
elemental Class Suit fields contains an attribute from that element. The two
elemental fields must name **different** elements; a second attribute from the
same element is rejected even when it belongs to another family. Therefore one
gear item may contribute once to each of two different resonance tracks, but
never twice to one track. The profession-specific Class Suit field remains
separate from these two elemental fields. Only equipped, owned, valid Class
Suit III/IV items in regular slots `0..11` count. Each element's count is
capped at ten, allowing two-handed and shield-using classes to reach the same
ceiling without changing equipment-slot rules.

Bonuses are cumulative and deliberately identical for every element:

| Matching equipped items | Added bonus for that element |
|---:|---|
| 3 | `+2.00` percentage points Power |
| 6 | retain the 3-piece bonus and add `+3.00` points Resistance |
| 10 | retain 3/6 bonuses and add `+2.00` points Penetration |

A mixed loadout can activate more than one element's threshold, but every
resonance addition remains in that element's typed total. The signature
descriptions and colors provide identity in this slice; they do not secretly
add different proc, crowd-control, critical, movement, or healing bonuses.
Element-specific signature mechanics should wait for combat telemetry and a
separate balance decision.

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
totals, resonance calculation, protocol projection, and UI presentation only.
Skills and ordinary attacks do not yet carry an authoritative element, so the
server must not silently reinterpret elemental Power as global physical/magic
damage, Resistance as global absorption, or Penetration as the existing
physical/magical defense-ignore fields.

A later combat patch requires a separate reviewed balance decision and must:

1. let skill/content data, never the client packet, choose the attack element;
2. define Neutral behavior for unclassified skills and ordinary attacks;
3. select and test all-source caps independently for Power, Resistance, and
   Penetration before any typed total changes damage;
4. guarantee Penetration cannot create negative Resistance or bonus damage;
5. apply the elemental stage exactly once at a documented point in the current
   physical/magic damage pipeline; and
6. ship PvE/PvP telemetry and rollback controls with that activation.

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
- Before combat activation, dedicated tests must demonstrate that Penetration
  never creates vulnerability and that no elemental modifier is applied twice.
