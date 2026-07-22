# Fighter EXP boosts

Fighter EXP modifiers are applied after the monster tier and player-level
falloff calculation. Different kinds add their bonus rates together; only the
highest-priority status within one kind is active.

```text
awarded EXP = truncate(base EXP * max(0, 1 + sum(active bonus rates)))
```

Talent EXP uses the same calculation with only kind `20` statuses. Talent
bonuses never enter the fighter-EXP aggregate or its wire field.

## Supported families

| Family | Kind | Maximum configured bonus | Notes |
|---|---:|---:|---|
| Potion, mooncake, or Passion Rose | 14 | +300% | Exactly one consumable status |
| Talent Potion or Talent EXP Boost | 20 | +400% | Exactly one Talent-only status |
| Weekend | 22 | +200% | Stock status 511 |
| Trick or Treat | 23 | +10% | Stock status 512 |
| Guild | 100 | +100% | One duration variant |
| VIP | 1008 | +5/10/15/20% | One account-wide tier; statuses 1500-1503 |
| Faction area control | 1009 | +25% | Status 1504; matching faction and current map only |

With the strongest status from all six fighter families, the bonus sum is `+655%`
and the total multiplier is `7.55x`. Party distribution is a separate reward
calculation. The strongest Talent status grants `+400%`, for a `5x` Talent EXP
multiplier.

## Online-only duration

Every timed row in `character_experience_modifiers` is a character-owned grant.
Its authoritative `remaining_online_ticks` budget starts only after the
character enters the world, checkpoints every status-reconciliation cycle and
when a reward resolves, and saves its final partial interval on logout or
session replacement. Merely logging into an account, remaining at character
selection, being disconnected, or restarting the server consumes no duration.
The status packet derives its displayed remaining seconds from this same
persisted budget.

Legacy `expires_at` rows migrate to the complete originally granted duration
(`expires_at - activated_at`). Historical online usage cannot be reconstructed,
so this restores rows that expired under the old offline-burning behavior. The
old field remains only as migration input.

The client-defined Talent statuses supported by this model are IDs `580`,
`587`, `581`, `509`, `582`, `588`, `583`, `589`, `584`, and `590` (kind `20`,
50–400%, one- or eight-hour variants).

VIP expiration and faction world-boss area control are external calendar
entitlements, not character-owned duration rows, so their clocks continue while
the character is offline. A future server-wide weekend schedule should use the
same calendar-entitlement path; an explicitly granted per-character weekend
row remains an online-only personal duration.

## World-boss area control

`WorldBossCatalog` selects one non-elite world boss in each eligible outdoor
area. Athens and Sparta (`0/1`), their Newbie suburbs (`2/4`), and dungeon or
timed event/instance maps are excluded. The nineteen ready primary areas are
maps `3`, `5-22`. Parnassus (`68`) is also classified as an eligible outdoor
area, but remains explicitly pending until a distinct neutral boss is authored;
its Athenian and Spartan Generals are faction quest objectives, not world bosses.
A selected boss respawns after 43,200 seconds.
Its killer's faction controls that boss's map until the same 12-hour expiry.
The area status and bonus resolve only when the character's faction and current
map match the persisted control row.

The catalog and control state do not fabricate monster appearances. The live
database must contain a captured or authored spawn packet for a selected boss
before the runtime can display or fight it.

Lelantine Farm (`42`) is a scheduled faction-scoring event whose Cerberus is
already its event final boss. Troy (`44`) is the timed Trojan Expedition with
sequential bosses and guild rewards. Heracles (`210`) is a twelve-stage,
server-driven challenge with no static monster catalog. They remain outside the
generic area-control lifecycle.

Area-control resolution also validates the currently enabled catalog entry and
its selected template, so a stale row cannot continue granting EXP after a
catalog change. Online clients reconcile their personalized EXP status set
every 30 seconds; this removes expired VIP icons and picks up administrative
boost changes without requiring a new login.
