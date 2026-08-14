# Authored Base Combat V1

Status: implemented in the working tree and under release verification. This is
the Reborn V1 balance contract, not a claim that the original server formulas
were recovered. Captured packet meanings are kept separate from authored
arithmetic so a later capture-backed formula can be versioned without silently
changing replay evidence.

## Proven client contract

### Basic attack

- Client opcode `10026` is 32 bytes. Meaningful request fields are the attacker
  object ID at `+4`, reported `X/Y/Z` at `+8/+12/+16`, and target object ID at
  `+20`. The native sender leaves `+24..+31` uninitialized, so the server ignores
  those bytes for identity, authorization, and combat math.
- Server opcode `10026` is 30 bytes: attacker at `+4`, impact position at
  `+8/+12/+16`, target at `+20`, damage at `+24`, animation selector at `+28`,
  and outcome at `+29`.
- Animation selector `0` is used for monsters, `3` for Warrior/Champion, and
  `5` for Priest/Mage. Outcome values are `0=critical`, `1=normal`, and
  `2=miss/dodge`. A miss carries the captured unsigned `0xFFFFFFFF` damage
  sentinel and performs no HP mutation.
- The request position is only a bounded reconciliation hint. It may differ
  from the authoritative position by at most `0.5` world units and never grants
  arbitrary reach.

### Cadence and range

- Full-status opcode `10166` carries the authoritative basic-attack interval at
  both `+114` (`u16`) and `+224` (`u32`), matching the native attack gate.
- An equipped item can affect cadence/range only when it is the reviewed weapon
  in equipment slot `10`. V1 converts the weapon's grade-indexed
  `AttackSpeed` to `round(1000 + AttackSpeed * 1000)` milliseconds and uses its
  grade-indexed `AttackRadius` as reach.
- With no reviewed weapon, cadence is `1500 ms` and range is `1.7` units. The
  server permits only a `25 ms` arrival-jitter allowance after resolving that
  interval. Client packet cadence is never authority.

### Death and revive

- Client revive opcode `10019` is exactly 12 bytes: local player object ID at
  `+4` and revive type at `+8`. The preserved original vector identifies type
  `2` as the free camp revive. Other types remain rejected until captured.
- Server death opcode `10018` is 28 bytes and carries player identity,
  position, map, and the captured terminal marker.
- Dead `WalkBegin`, `WalkEnd`, and `Walk` traffic is rejected before cast
  interruption, broadcast, transition, or persistence in both Legacy and ECS
  runtimes. A valid type-2 revive advances life state once, relocates to camp,
  restores 10 percent HP/MP, checkpoints both, and re-enters the world.

## Authoritative stat inventory

Percentage channels use basis points (`10,000 bp = 100%`). Rating and flat
channels use whole stat/damage units.

| Combat channel | Unit | V1 use |
|---|---:|---|
| Physical / Magic Attack | flat | Selects the offensive base by profession or skill property |
| Physical / Magic Defense | flat | Selected target defense before ignore-defense |
| Hit / Dodge | rating | Deterministic hit chance |
| Critical / Critical Resistance | rating | Deterministic critical chance after a hit |
| Ignore Physical / Magic Defense | bp | Reduces only the selected defense, capped at 80% |
| Physical / Magic Damage Bonus | bp | Multiplies skill core damage |
| Physical / Magic Append Damage | flat | Added after typed damage bonus and critical bonus |
| Critical Damage | bp + flat | Adds critical-only bonus damage |
| Critical Damage Reduction | bp + flat | Reduces only the critical bonus, capped at 80% before flat reduction |
| Physical / Magic Damage Reduction | bp | Reduces typed final damage, capped at 80% |
| Physical / Magic Flat Absorption | flat | Subtracted after typed reduction |
| Life Absorption | bp | Heals from damage actually committed, capped by missing HP |
| Damage Rebound | bp + flat | Returns damage actually received; reflected damage cannot recurse |
| HP / MP Recovery | flat | Feeds the authoritative six-second recovery pulse |
| Elemental potency / chance / resistance | bp | Drives one selected elemental status and 3/6/10 resonance rules |

The calculated snapshot composes reviewed equipment templates, ordinary and
Class Suit attributes, talents, Holy/Ware Suit effects, mount-gear spirits,
Owner Merge bonuses, carried-pet passive skills, and socketed Holy Spirits.
The generic legacy `DamageAbsorb` packet field remains for compatibility, but
combat consumes the typed physical/magic projections exactly once.

Reviewed carried-pet passives currently contribute: Wild Bump to
ignore-physical-defense, Wild Strength to physical attack, Focus to hit,
Violent Strength to physical-damage bonus, and Resolute Physique to maximum HP.
The highest learned-skill curve step whose rank requirement is met is selected
at runtime. Soul Contract's displayed `+3..+8` Savvy now also participates in
skill-book Trait admission.

Owner Merge contributes all sixteen published channels, including physical and
magic reductions, critical reduction, life absorption, and rebound. Reviewed
Holy Spirit effects `1..14` and `19..20` project their ignores, damage bonuses,
append damage, critical bonus, typed reductions/absorption, and rebound
channels. Effects `15..18` remain gated because their material items do not yet
have approved effectiveness and affinity definitions.

## Reborn V1 formula

The basic channel is Magic for Priest/Mage professions (`2`/`3`) and Physical
for Warrior/Champion. A combat skill uses Magic when its sealed `Property` is
`1`, otherwise Physical.

For attacker level `La`, target level `Lt`, Hit `H`, Dodge `D`, Critical `C`,
and Critical Resistance `R`:

```text
ratingScale = 100 + 25 * max(La, Lt)
hitChanceBp  = clamp(9000 + 4000 * (H - D) /
                     (ratingScale + H + D), 500, 9800)
critChanceBp = clamp( 500 + 4500 * (C - R) /
                     (ratingScale + C + R),   0, 5000)
```

Ratings are clamped non-negative before these calculations. Hit and critical
rolls come from the formula version, authenticated identities, server-owned
combat/health revisions, skill identity, and deterministic target order. Wall
clock time and untrusted packet bytes never choose a roll. A miss consumes an
otherwise admitted action but skips the critical roll and HP mutation.

For the selected Attack `A`, Defense `D`, ignore-defense `I`, skill coefficient
`P1`, flat skill power `P2`, typed bonus `B`, append damage `X`, typed reduction
`M`, and flat absorption `F`:

```text
effectiveDefense = D * (10000 - clamp(I, 0, 8000)) / 10000
afterDefense     = max(0, A - effectiveDefense)
skillCore        = afterDefense * max(0, 1 + P1) + P2
typedDamage      = skillCore * (1 + max(0, B) / 10000)

criticalBonus    = typedDamage * (5000 + max(0, CritDamageBp)) / 10000
                 + max(0, CritDamageFlat)
criticalBonus    = criticalBonus *
                   (10000 - clamp(CritReductionBp, 0, 8000)) / 10000
                 - max(0, CritReductionFlat)

preMitigation    = typedDamage + (critical ? max(0, criticalBonus) : 0)
                 + max(0, X)
afterReduction   = preMitigation *
                   (10000 - clamp(M, 0, 8000)) / 10000
finalDamage      = afterReduction - max(0, F)
```

V1 uses saturating decimal intermediates and rounds the final result away from
zero. An admitted basic hit floors at one damage. A positive damaging skill
also floors at one after mitigation; a genuinely zero-power skill may remain
zero and cannot mutate HP.

Post-commit effects are based on applied damage, not requested or overkill
damage:

```text
lifeHeal = appliedDamage * clamp(LifeAbsorptionBp, 0, 10000) / 10000
rebound  = appliedDamage * clamp(ReboundBp, 0, 10000) / 10000
         + max(0, ReboundFlat)
```

Bounded process-owned replay fences claim each source event for its active
runtime window. Authoritative primary and secondary mutations are frozen before
network I/O; publication and reward/death work consume that captured result.
Rebound is tagged as derived damage and cannot trigger rebound, life steal, or
another proc chain.

## Monster combat policy

Sealed gameplay content now retains stock `AttackType`: `1` is Physical, `2`
is Magic, and `3` is preserved as Special. Special currently uses physical
mitigation until captures prove another channel. The following explicit V1
ratings replace the old raw-attack-only placeholder. Let `L` be clamped tier
and `S` be `1.00` normal, `1.15` elite, or `1.30` boss:

```text
Physical Attack      = S * (21 + 3L + floor(L/3))
Magic Attack         = S * (23 + 3L + floor(L/2))
Physical Defense     = S * (10 + 6L + floor(L^2/10))
Magic Defense        = S * (10 + 5L + floor(L^2/12))
Hit                   = S * (100 + 20L)
Dodge                 = S * ( 50 + 12L)
Critical              = S * ( 25 +  8L)
Critical Resistance   = S * ( 25 + 10L)
```

Monster attacks and all four hostile-monster skill paths use the shared V1
resolver. Single and area targets have stable ordering; a miss consumes the
admitted cast and mana but performs no false health mutation.

## Elemental and resonance execution

Native attack packets expose no trustworthy element selector. V1 therefore
selects at most one non-Gale source element with positive potency and chance:
highest potency, then highest chance, then stable enum order. It never trusts a
packet selector and never procs every equipped element at once.

Element application chance is capped at `2000 bp`; potency at `1000 bp`; and
resistance at `7000 bp`. Effective potency is
`potency * (10000 - resistance) / 10000`. Burn is derived from committed direct
damage; Drench, Shock, Fracture, Dazzle, and Wither affect their typed runtime
seams; Gale is movement-triggered. Movement and recovery use independent
server-owned accepted revisions. Status/resonance state is fenced to the
session ownership generation and map and is cleared on death or reconnect.

The exact 3/6/10 rules and per-grade curves remain centralized in
[the Class Suit elemental contract](class-suit-elemental-attribute-roadmap.md).

## PvP boundary

PvP is default-deny. V1 admits only distinct, living, same-map Athens/Sparta
opponents on sealed map mode `0`; every unknown or other map mode is safe.
Admission, range, ownership, vitals, elemental state, and death attribution are
revalidated and mutated inside one serialized registry transaction. The frozen
decision is then published and checkpointed outside that lock.

Current PvP execution is deliberately **basic-attacks-only**. Shipped skill
content advertises PK-capable masks, but available captures do not prove
hostile-player skill damage flags, miss representation, area entries,
local/world identity translation, or status packets. Those casts remain
rejected rather than inventing a client contract.

Zephyr effects `21` and `22` reinforce reviewed mount-gear passive stats.
Effects `23` and `24` have bounded policy math but remain inert until an
authoritative hostile mana-burn and cooldown-extension producer is defined.
Likewise, pet skill families outside the reviewed owner-passive allowlist and
autonomous summoned-pet combat remain evidence-gated.

## Acceptance gate

- Legacy and ECS paths must produce the same formula evidence and committed
  state for the same authoritative input.
- Tests cover normal/miss/critical packets, deterministic replay and target
  order, rejected-spam identities, cooldown/resource admission, typed source
  projection, weapon swap/unequip, physical/magic monsters, lifesteal/rebound,
  PvP denial/commit/death, elemental caps/status lifecycle, and all 21
  resonance tiers.
- Migration/content tests must apply on fresh disposable PostgreSQL and prove
  `AttackType` and map mode participate in the sealed gameplay hash.
- Release builds must have zero warnings/errors, `git diff --check` must be
  clean, and every changed or newly added source file must remain below 20,000
  bytes.
