# Training-dummy damage-skill adapter

This is Reborn-authored, LocalDevelopment-only behavior. It does not claim
that the stock hostile-player skill-result wire is understood, and it does not
relax the native hostile-player skill gate.

The profession-neutral adapter admits a skill only when its complete tuple
exactly matches the process-pinned runtime catalog, the catalog assigns it to
the attacker's profession, it is instantaneous and damaging, and its shape is
either hostile scalar or self-centred hostile area. Learned-skill admission is
still performed by the normal handler before this adapter. Cast-time and
ground-targeted shapes remain on their existing paths.

| Profession | Published instantaneous damage families |
| --- | --- |
| Warrior | scalar 0-4, 10-14, 20-24, 50-54, 60-64; self-area 30-34, 40-44 |
| Champion | every catalog-owned instantaneous scalar/self-area damage rank, including Spear Blast 320-324 and Meteor Blast 330-334 |

Each scalar cast may select only an exact configured training dummy. Each area
cast uses the authoritative caster position, snapshots status state before the
registry gate, then selects only live exact dummy sessions inside the strict
radius in world-object-ID order. With no dummy in range it returns to the
unchanged monster-PvE resolver. Ordinary players are never area candidates.

An exact training-dummy admission is passive: target stat Rebound and
target-sourced Gaia Reflection neither damage the attacker nor consume Gaia's
reflection replay state. The dummy's defenses, reductions, absorption,
incoming elemental mitigation, and the attacker's own elemental effects remain
authoritative. This exception is entitlement-scoped; ordinary PvP keeps its
normal counter-damage behavior.

Area MP and cooldown are claimed once, and the admitted combat revision is
called once and reused across targets; the target identity differentiates each
deterministic PvP V2 event. Each target is a serialized subtransaction using
the existing physical-damage, vitals, and death publication path. An admitted
scalar also projects the captured cast visual (`0x2738`) before its physical
damage (`0x272A`) and the captured impact (`0x273E`) after it, translating both
player identities for each viewer. A self-area cast projects one translated
visual and impact before its ordered per-target physical-damage packets. No
skill-damage or cluster-damage opcode is inferred. All expected validation
finishes before the first commit. Because elemental commit state is not
reversibly transactional, an unexpected internal exception after a target
commit retains the action's MP/cooldown and every earlier target commit instead
of pretending to roll them back.

Spear Blast 320-324 and Meteor Blast 330-334 make one deterministic Injury
landing roll after each committed, nonlethal damaging hit. The project-authored
neutral chance is 50%, contested by the attacker's final Hit plus StatusHit
against the target's final Dodge plus StatusResistance and clamped to 5-95%.
Native `StatusOdds` remains audit metadata because its original conversion is
not recovered.

| Rank | Skill IDs | Status / native odds | Physical damage taken | Duration |
| --- | --- | --- | --- | --- |
| 1 | 320 / 330 | 130 / 200 | +10% | 12 seconds |
| 2 | 321 / 331 | 131 / 220 | +10% | 20 seconds |
| 3 | 322 / 332 | 132 / 240 | +15% | 12 seconds |
| 4 | 323 / 333 | 133 / 300 | +15% | 20 seconds |
| 5 | 324 / 334 | 133 / 300 | +15% | 20 seconds |

A landed Injury never changes its triggering hit or magic damage. Buff 344,
out-of-family IDs, drifted catalog tuples, skill/profession mismatches, dummy
attackers, forged caster IDs, ordinary targets, and moved or recreated dummy
identities remain excluded by this damage adapter. Status-only control 354 is
handled by the separate exact-dummy hostile-status transaction.

The focused protocol check is named
`Authoritative development-only training-dummy damage skills`. It enumerates
every published instantaneous scalar/self-area damage definition against its
owning profession; explicitly covers all 35 Warrior damage ranks, skill ID 0,
Warrior areas 30 and 40, and the existing Champion/Injury matrix; and covers
exact policy sealing, scalar per-skill resources, strict area selection/order, single
action revision and resource claim, deterministic V2 routing, no-target PvE
fallback, zero-mutation prevalidation, ordinary-player isolation, lethal scalar
commit, captured animation ordering and viewer identity translation, one
animation per area cast, deterministic all-rank Injury application and resource
misses, stock status effects and durations, subsequent physical-only
vulnerability, lifecycle clearing, wrong-profession and drift rejection,
ID-zero cooldown replay, and unchanged ordinary-PvP publication.
