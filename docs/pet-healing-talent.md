# Pet Healing Talent Runtime

## Authority and balance version

Pet Healing is an authoritative server combat effect. The client may display
the result, but it cannot request a heal, choose an amount, or reset the
cooldown.

The current project-authored balance is `PetHealingTalentPolicy.Version = 2`:

- Trigger after accepted, nonlethal damage leaves the owner at or below 40%
  maximum HP.
- Require the one authoritative pet to be both carried and summoned and to
  have Healing mask bit `8`.
- Resolve a percentage of owner maximum HP from aptitude and pet level.
- A level-1 pet receives 50% of its aptitude rate; effectiveness increases
  linearly to 100% at pet level 120.
- Apply the owner's authoritative Wither healing-received multiplier to that
  resolved amount at the incoming-hit timestamp.
- Clamp the applied amount to the owner's missing HP.
- Start a 180-second cooldown only after a positive heal is applied.
- Do not trigger from rejected, duplicate, stale, zero-damage, or lethal
  damage decisions.

The level-120 aptitude rates are:

| Aptitude | Maximum-HP healing |
|---|---:|
| Smart | 12% |
| Overbearing | 14% |
| Ferocious | 16% |
| Almighty | 18% |
| Godly | 20% |
| Celestial | 22% |
| Transcendent | 25% |

The 180-second cooldown and pet-level dependency are native client-derived
rules. The exact original amount formula was not recoverable; the V2 rates
above are deliberately identified as project-authored. Owner level scales the
result naturally through authoritative maximum HP instead of adding a small
flat level value.

## Runtime flow

1. The character snapshot retains the complete durable pet collection.
2. World join projects at most one carried-and-summoned pet into
   `PetHealingTalentHydrationSnapshot`.
3. Durable pet snapshot reloads replace that bounded runtime projection.
4. `MonsterPlayerDamageSystem` validates and applies damage at ECS order 500.
5. `PetHealingTalentSystem` consumes the accepted damage event at order 510.
6. The live adapter commits the damage HP/revision and then the Healing
   HP/revision to `GameCharacter` under its vitals lock.
7. Network publication sends physical damage, green Healing combat text, and
   final authoritative vitals in that order.
8. The final HP is saved through the existing routine-vitals persistence path.

No database query, Redis call, packet send, or persistence operation runs in
the ECS tick.

## Cooldown ownership and limitation

`ProcessPetHealingCooldownStore` is shared by all player session adapters in
the game-server runtime and is keyed by `(character ID, pet ID)`. It therefore
survives disconnect/reconnect and pet projection reloads. The store has a hard
entry capacity and fails closed when no expired entry can be reclaimed.

The cooldown is intentionally runtime-only in this slice. A full game-server
process restart clears it. Before cross-instance character transfer is enabled,
replace or supplement this ledger with a preloaded, TTL-backed coordination
projection; never call that remote store from the simulation tick.

## Wire compatibility

The green number uses the already verified native `SkillHealing` result flags
and signed negative amount. No unverified pet cast animation is emitted. The
combat-text skill ID remains zero until an original-server capture identifies
a native Healing talent effect ID.
