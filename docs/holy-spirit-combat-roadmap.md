# Holy Spirit Combat and Balance Roadmap

Status: design proposal only. Holy Spirit implementation, mounting, detaching,
wire projection, and exact rolled-value persistence exist. The combat effects
in this document must remain disabled until their individual acceptance gates
are implemented and tested.

The authoritative recovery simulation currently pulses every six seconds in
`PlayerRecoverySimulationSystem`. Percentage recovery below uses that same
pulse rather than introducing a second timer.

## Family organization

The original 20 client items and their effects remain available, but the old
Fire and Water labels should be replaced by families that describe their real
purpose.

| Family | Existing item IDs | Responsibility |
|---|---|---|
| Ares | 9060, 9062, 9066 | Physical offense |
| Hecate | 9061, 9063, 9067 | Magical offense |
| Tyche | 9064, 9065 | Percentage and flat critical-strike damage |
| Aegis | 9080-9083, 9086, 9087 | Direct and critical mitigation |
| Nemesis | 9084, 9085 | Percentage and flat retaliation |
| Aether | 9068, 9069, 9088, 9089 | HP and MP sustain |

The percentage and flat legacy pairs are deliberately retained. Flat values
are useful against smaller repeated hits while percentage values scale against
large hits. They are different build choices, not duplicate state.

Holy Stone compatibility is an explicit combat temperament rather than an
elemental family:

- Heated Holy Stone: effects whose primary outcome increases outgoing
  pressure, including Ares, Hecate, Tyche, hostile debuffs, and interrupts;
- Cooled Holy Stone: effects whose primary outcome is mitigation, sustain,
  cleansing, resistance, or defensive counterplay, including Aegis, Nemesis,
  and Aether.

An offensive self-buff remains Heated; "buff" does not automatically mean
Cooled. A hostile dispel or debuff is also Heated even when it does not deal
direct damage. Every Spirit must declare its required affinity explicitly.
Compatibility must never be inferred from its display name, icon, or item-ID
range.

The four published sustain items `9068`, `9069`, `9088`, and `9089` are not
yet part of the authoritative effectiveness catalog. Two were historically
associated with each stock stone. Before all four are activated as Cooled,
the client compatibility rule and detach restoration identity must be migrated
without changing already-mounted stones.

Existing wire item and effect IDs should not be renumbered. Display names may
change through versioned item content after the client presentation is ready.
The mount-gear-only Zephyr proposal is deliberately separated into
`mount-gear-spirit-roadmap.md`; it must not reuse character Holy Stone sockets.

## Non-redundancy decisions

The repository already reserves elemental resonance and Class Suit mechanics
for barriers, life steal, execute damage, movement speed, reflection, and
other effects. Holy Spirits must not publish a second indistinguishable source.

| Earlier Spirit idea | Replacement | Reason |
|---|---|---|
| Ares Execution | Ares Prowess | Execute already belongs to Hades resonance and the Thanatos prototype. |
| Aegis Intercession | Aegis Anchoring | Barriers already belong to Apollo resonance. |
| Zephyr Momentum | Zephyr Reprieve | Movement and momentum already belong to Aeolus resonance and the Nike prototype. |
| Nyx Hunger | Nyx Exhaustion | Life steal already belongs to Hades resonance and the Styx prototype. |
| Generic Grace or Providence | Aether Conservation | Healing amplification already has Apollo and Asclepius ownership. |
| Generic Conduction | Astrape Disruption | Chain damage already belongs to Zeus resonance. |
| Generic Reversal | Chronos Delay | Control resistance belongs to Gaia Stability. |
| Nyx Withering | Nyx Lethargy | Healing reduction already belongs to the Dark elemental Wither effect. |

These ownership decisions must be checked again before activation because the
elemental resonance execution switch is currently disabled, but its reserved
identity is still intentional.

The original Tyche pair does **not** add critical chance: one Spirit adds
percentage critical-strike damage and the other adds flat critical-strike
damage. Tyche Fortune below is therefore a distinct critical-chance effect.

## Effectiveness model

- A Holy Spirit is assigned one random value inside its stone-level bracket
  when implemented.
- The committed value is stored and remains unchanged through mounting,
  detaching, reconnecting, and equipment transfer.
- Level 10 is the only level that can reach the published maximum.
- Percentage values use percentage points in this document. Persistence and
  combat should use integer basis points or another reviewed fixed-point unit.
- Passive effects may add across equipment only up to their loadout cap.
- Chance-based, control, cleanse, and timing effects use the strongest equipped
  roll only. Their probabilities must not be added across duplicate copies.
- Each triggered effect has one server-owned shared cooldown per character,
  not one cooldown per equipped item.

## Aether Spirit Level 1-10 values

| Stone level | Renewal: max HP per 6s | Ichor: flat HP when struck | Flow: max MP per 6s | Serenity: flat MP when struck |
|---:|---:|---:|---:|---:|
| 1 | 0.08%-0.10% | 50-60 | 0.12%-0.15% | 6-8 |
| 2 | 0.12%-0.15% | 70-85 | 0.18%-0.22% | 9-12 |
| 3 | 0.16%-0.20% | 95-115 | 0.24%-0.30% | 13-17 |
| 4 | 0.20%-0.25% | 125-150 | 0.30%-0.38% | 18-23 |
| 5 | 0.24%-0.30% | 160-195 | 0.37%-0.46% | 24-30 |
| 6 | 0.28%-0.35% | 200-240 | 0.44%-0.55% | 31-38 |
| 7 | 0.32%-0.40% | 245-295 | 0.52%-0.65% | 39-47 |
| 8 | 0.36%-0.45% | 300-360 | 0.60%-0.75% | 48-57 |
| 9 | 0.40%-0.50% | 365-435 | 0.70%-0.87% | 58-68 |
| 10 | 0.50%-0.60% | 440-520 | 0.80%-1.00% | 69-80 |

### Aether execution limits

- Renewal and Flow join the existing six-second recovery pulse. They do not
  create catch-up pulses after lag, logout, reconnect, or server downtime.
- Renewal has a Spirit-family loadout cap of 1.00% maximum HP per pulse in PvE
  and 0.50% in PvP. All passive periodic HP recovery sources together have a
  2.00% PvE and 1.00% PvP cap per pulse.
- Flow has a Spirit-family loadout cap of 1.50% maximum MP per pulse in PvE and
  1.00% in PvP. All passive periodic MP recovery sources together have a 2.50%
  PvE and 1.50% PvP cap per pulse.
- Ichor and Serenity trigger only from positive, final, direct hostile damage
  at least 0.50% of the recipient's maximum HP. Each has its own four-second
  PvE and six-second PvP shared cooldown. Multi-hit and area skills provide at
  most one eligibility check per cast and primary recipient.
- Ichor's summed trigger is capped at 1.00% maximum HP, with its final recovery
  halved in PvP.
- Serenity's summed trigger is capped at 1.00% maximum MP, with its final
  recovery halved in PvP.
- Renewal, Ichor, Flow, and Serenity pass through the normal authoritative
  recovery modifiers, including applicable healing or recovery reduction.
- None of the four effects can revive, over-heal, exceed maximum MP, trigger
  from self-damage, or trigger from DoT, reflection, delayed damage, proc
  damage, environmental damage, or another recovery effect.

The values are intentionally below the existing level-160 test character's
ordinary recovery output while remaining meaningful: they supplement normal
recovery rather than replacing potions, healers, or resource management.

## Level 1-10 curves for additional Spirits

Every entry is the inclusive bracket rolled once when the Spirit is
implemented. Effects with very different combat value have separate curves;
one generic percentage curve would make rare dispels useless or passive stats
overpowered.

| Level | Prowess per stack | Nullification chance | Fortune critical chance | Anchoring chance | Reckoning stored damage | Conservation MP refund |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.10%-0.20% | 3%-5% | 0.25%-0.40% | 1%-2% | 2%-3% | 8%-10% |
| 2 | 0.20%-0.35% | 4%-6% | 0.50%-0.80% | 2%-4% | 3%-4% | 10%-12% |
| 3 | 0.30%-0.50% | 5%-7% | 0.75%-1.20% | 3%-6% | 4%-5% | 12%-14% |
| 4 | 0.40%-0.65% | 6%-8% | 1.00%-1.60% | 4%-8% | 5%-6% | 14%-16% |
| 5 | 0.50%-0.80% | 7%-10% | 1.25%-2.00% | 5%-10% | 6%-7% | 16%-18% |
| 6 | 0.60%-0.95% | 8%-12% | 1.50%-2.40% | 6%-12% | 7%-8% | 18%-20% |
| 7 | 0.70%-1.10% | 10%-14% | 1.75%-2.80% | 7%-14% | 8%-9% | 20%-22% |
| 8 | 0.80%-1.25% | 12%-16% | 2.00%-3.20% | 8%-16% | 9%-10% | 22%-24% |
| 9 | 0.90%-1.40% | 14%-18% | 2.25%-3.60% | 9%-18% | 10%-11% | 24%-27% |
| 10 | 1.00%-1.50% | 16%-20% | 2.50%-4.00% | 10%-20% | 11%-12% | 27%-30% |

| Level | Stability CC reduction | Reprieve cast reduction | Purity cleanse chance | Exhaustion recovery reduction | Lethargy attack/cast slow | Disruption interrupt chance | Delay deferred damage |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 1%-2% | 1%-2% | 10%-15% | 1%-2% | 0.5%-1.0% | 2%-4% | 2%-3% |
| 2 | 2%-4% | 2%-3% | 15%-20% | 2%-3.5% | 1.0%-1.5% | 3%-5% | 3%-4% |
| 3 | 3%-6% | 3%-4.5% | 20%-25% | 3%-5% | 1.5%-2.0% | 4%-6% | 4%-5% |
| 4 | 4%-8% | 4%-6% | 25%-30% | 4%-6.5% | 2.0%-3.0% | 5%-7% | 5%-6% |
| 5 | 5%-10% | 5%-7.5% | 30%-35% | 5%-8% | 2.5%-4.0% | 6%-9% | 6%-7% |
| 6 | 6%-12% | 6%-9% | 35%-40% | 6%-9.5% | 3.0%-5.0% | 8%-11% | 7%-8% |
| 7 | 7%-14% | 7%-10.5% | 40%-45% | 7%-11% | 3.5%-6.0% | 10%-13% | 8%-9% |
| 8 | 8%-16% | 8%-12% | 45%-50% | 8%-12.5% | 4.0%-6.5% | 12%-15% | 9%-10% |
| 9 | 9%-18% | 9%-13.5% | 50%-60% | 9%-14% | 4.5%-7.0% | 14%-17% | 10%-12% |
| 10 | 10%-20% | 10%-15% | 60%-75% | 10%-15% | 5.0%-8.0% | 16%-20% | 12%-15% |

## Additional Spirit definitions and limits

| Spirit | Curve meaning | Server-owned rule and cap |
|---|---|---|
| Ares Spirit of Prowess | Direct-damage bonus gained per consecutive hit on the same target | Strongest equipped roll; maximum three stacks; expires after four seconds or on target change. Level 10 reaches 3.00%-4.50%, capped at 5% PvE and 3% PvP. One stack per cast/primary target; procs and DoTs cannot build it. |
| Hecate Spirit of Nullification | Chance for a completed direct magical cast to remove one dispellable positive status | Strongest roll only; six-second attempt cooldown and 45-second PvE or 60-second PvP successful-dispel cooldown. PvP chance is multiplied by 0.75 and elite chance by 0.50. Boss, system, mount, entitlement, and unremovable statuses are never eligible. |
| Tyche Spirit of Fortune | Additive critical-strike chance | This is distinct from the original percentage and flat critical-damage Spirits. Numeric copies may add to a 6% Holy Spirit cap in PvE and 4% in PvP. It changes critical chance only and cannot make an otherwise ineligible event critical. |
| Aegis Spirit of Anchoring | Chance to resist a knockback, pull, or forced displacement | Strongest roll only; 20% maximum. It does not resist damage, stun, silence, paralysis, or ordinary slows. Scripted boss movement remains unresistable. |
| Nemesis Spirit of Reckoning | Portion of post-mitigation direct damage stored for the next direct attack | Strongest roll only; eight-second capture cooldown and eight-second expiry. Reservoir is capped at 3% own max HP in PvE and 1.5% in PvP. Release cannot exceed 50% of the triggering attack's applied damage, reduced to 25% against bosses. |
| Aether Spirit of Conservation | Percentage of MP actually paid that may be refunded after successful skill commitment | Strongest roll only and a fixed 20% proc chance, with a three-second successful-refund cooldown. PvP refund is capped at 20%. The full cost is required and spent first; cancelled, interrupted, free, toggled, channel ticks, and replayed casts do not refund MP. |
| Gaia Spirit of Stability | Reduction to eligible crowd-control duration | 30% all-source cap in PvE, 20% in PvP, and a 50% original-duration floor. It affects stun, silence, paralysis, and slow duration, not chance or potency. Scripted mechanics, damage, and forced displacement are excluded. |
| Zephyr Spirit of Reprieve | Reduction to the next eligible cast time after a genuine dodge | Strongest roll only; ten-second shared cooldown, 300ms minimum cast time, and 10% PvP cap. It cannot affect instant, channelled, teleport, or Ride skills. |
| Helios Spirit of Purity | Chance to remove a newly applied, explicitly cleanseable negative status | Strongest roll only; eight-second failed-attempt cooldown and 45-second PvE or 60-second PvP successful-cleanse cooldown. At most one status is removed, selected deterministically by server priority. Control immunity is never granted. |
| Nyx Spirit of Exhaustion | Reduction to the target's HP/MP recovery after a direct damaging hit | Six-second debuff, refreshed rather than stacked; 15% cap. Does not reduce direct heals, potions, current resources, or maximum resources. |
| Nyx Spirit of Lethargy | Reduction to attack and cast speed after a direct damaging hit | This replaces duplicate Withering. Four-second debuff, strongest roll only, capped at 8% PvE, 5% PvP, and 2% against bosses. It never changes cooldowns, committed animations, or channel tick frequency. |
| Astrape Spirit of Disruption | Chance for a direct hit to interrupt a target that is actively casting | Strongest roll only; one roll per attacker/target cast identity, 20-second PvE and 30-second PvP successful-interrupt cooldown. PvP chance is multiplied by 0.67 and elite chance by 0.50. World bosses and uninterruptible skills are immune. It never stuns or silences. |
| Chronos Spirit of Delay | Portion of an eligible burst converted into four one-second delayed-damage ticks | Strongest roll only; eligible direct hit must be at least 20% maximum HP, with a 30-second PvE and 45-second PvP cooldown. One pool is capped at 10% own max HP PvE and 5% PvP. Total damage is unchanged and an existing pool cannot be extended or stacked. |

## Mandatory combat rules

1. The server resolves every effect from authoritative equipment, pinned
   content revision, validated combat event, and monotonic server time.
2. Reflected, stored, proc, DoT, and delayed damage cannot trigger reflection,
   Reckoning, life steal, recovery, critical rolls, elemental applications,
   additional procs, or themselves.
3. Reflected and delayed damage cannot crit. Reflected damage cannot be
   reflected again or activate life steal.
4. Recovery clamps to maximum resources and cannot revive a dead character.
5. Interrupted and rejected skills produce no Conservation refund or offensive
   Spirit proc.
6. Duplicate or reordered network commands cannot create another roll, trigger,
   refund, cleanse, or cooldown. Combat event IDs require deduplication.
7. PvP and world-boss caps are evaluated using the authoritative target type,
   never a client-supplied flag.
8. A world boss can explicitly opt into an otherwise immune control or dispel
   mechanic through versioned gameplay content; the default is immune.
9. Proc selection and status-removal order must be deterministic for replay and
   testing even where the initial effectiveness roll is random.
10. Cooldowns survive reconnect and zone transfer for their remaining runtime
    window. Equipment swap, detach, death, and relog cannot clear them. Logout
    must not advance online-only durations.
11. The client receives presentation data only. It never supplies a rolled
   value, proc result, target classification, cooldown completion, or final
   damage/recovery amount.
12. Metrics must record activation, rejection reason, cap application, target
   class, and recursion suppression without using account IDs as labels.
13. A multi-hit or area cast gets one eligibility decision per logical effect
    and primary target, not one independent proc roll per damage number.
14. A pet, mount, trap, or summon does not inherit its owner's Holy Spirits
    unless that individual effect explicitly declares and tests such ownership.
15. Equivalent bonuses from Spirits, elemental resonance, Class Suit, skills,
    pets, and future systems must feed one named all-source semantic cap rather
    than creating separately uncapped copies of the same mechanic.

## Why these values are an alpha-safe starting point

- Periodic recovery is useful over a fight but cannot replace potions or a
  healer, and all recovery systems share final caps.
- Low-level proc Spirits have a real chance to matter; high-level Spirits are
  limited by shared attempt/success cooldowns instead of tiny unusable chances.
- PvP coefficients and caps are stricter than PvE, while world bosses remain
  immune to dispel, cleanse abuse, forced movement, and interruption by default.
- Duplicate copies cannot multiply cleanse, dispel, control, or interrupt
  rolls. Only passive numeric effects add, and only until their named cap.
- Burst smoothing, reflection, stored damage, and recovery cannot recursively
  activate each other, preventing infinite chains and damage/healing explosions.

## Activation gate

Before any effect is enabled:

- assign stable item/effect IDs and publish versioned database content;
- update the client names, descriptions, result dialogue, and tooltip units;
- add typed ECS components rather than a generic `DamageAbsorb` bucket;
- define ordering relative to defense, mitigation, criticals, shields,
  reflection, life steal, elemental resonance, and death;
- add deterministic unit and replay tests for every level boundary and cap;
- add PvE, PvP, world-boss, reconnect, zone-transfer, duplicate-event, and
  rollback tests;
- test weak-hit farming, alternate-account farming, self-damage, reflected
  damage, DoTs, pets, monsters, area skills, and simultaneous lethal events;
- publish telemetry and a content-only kill switch; and
- run a staged balance soak before making the materials obtainable.

No balance value in this roadmap is a production guarantee. Values should be
adjusted through versioned content after telemetry, without rewriting already
committed item effectiveness rolls silently.

## Prepared icon content

The four Aether effects and thirteen additional Spirits have reviewed source
art, native 36x36 sprites, and an isolated `Icon5.gwo` mapping under
`assets/holy-spirits`. The proposed item IDs in that manifest are reservations,
not activated content. See `assets/holy-spirits/README.md` for the visual
identity, rebuild commands, installation safeguards, and activation boundary.
