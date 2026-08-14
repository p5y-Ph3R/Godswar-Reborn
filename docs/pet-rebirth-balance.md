# Pet rebirth balance

This document records the pet rebirth contract, including the explicit
level-30 product override from the installed stock client's level-50 gate.

## Eligibility and state transition

- Rebirth 1 requires pet level 30 as a deliberate product override. The
  installed stock `Pet_Alter.xml`, `LuaText.lua`, and
  `HelpSystemSkillConfig.lua` resources say level 50; all six guarded locale
  resources must therefore be patched with the server.
- Rebirth 2 requires pet level 80.
- Rebirth 3 requires pet level 100.
- Rebirth 4 requires pet level 110.
- Rebirth 5 and every later rebirth require pet level 120.
- The pet must be summoned, available, and not merged with its owner.
- The pet must have a Soul Contract and an available rebirth attempt.
- Rebirth returns the pet to level 1.
- Basic Savvy remains unchanged: it is the immutable hatch allocation plus
  any durable pet-to-pet Merge gains.
- Growth acceleration is cumulative across Rebirths. Per attribute,
  `effective Growth Rate = base_growth_rate + growth_acceleration`.
- Rebirth recomputes the cumulative Added Value for the resulting level 1 as
  `effective Growth Rate * 1`. Subsequent level-ups recompute it as
  `effective Growth Rate * current pet level` without changing Basic.
- The native Added column carries that cumulative Added Value. It does not
  carry raw base Growth Rate or acceleration.
- Model evolution occurs at rebirth counts 8 and 20.
- Rebirth 100 is the project maximum.

Every complete level above the applicable gate is refunded at its historical
transition cost. The pet's full pre-rebirth unspent EXP pool is then added to
that refund. This is an explicit, deterministic server compatibility policy:
the stock client proves only that excess levels become an opaque server-issued
EXP value, not the conversion formula. For example, reaching level 120 costs
`252,947,820` EXP and reaching the level-30 first-rebirth gate costs
`9,967,020` EXP. Its historical surplus is therefore `242,980,800` EXP, plus
whatever EXP remained on the level-120 pet. EXP is never accepted from the
client. The combined pool must fit the client's unsigned 32-bit EXP field;
otherwise rebirth rejects before RNG or inventory consumption.

For a pet reborn at level 120, the active-step refunds are:

| Next rebirth | Active gate | Historical surplus | Plus unspent EXP |
|---:|---:|---:|---:|
| 1 | 30 | 242,980,800 | full current pool |
| 2 | 80 | 156,880,350 | full current pool |
| 3 | 100 | 93,759,075 | full current pool |
| 4 | 110 | 51,664,650 | full current pool |
| 5-100 | 120 | 0 | full current pool |

The level-120 gate does not erase EXP. At rebirth 5 and later, historical
surplus is zero because there are no complete levels above 120, but every
unspent point already held at level 120 carries through the reset.

Level 50 is not an active server gate. It appears only in the unpatched stock
first-rebirth resources, which the level-30 product override replaces.

## Native protocol and durable authority

- Client request `10272` is exactly 12 bytes: material template at offset 4,
  quantity at offset 8, and zero reserved bytes at offsets 9-11.
- Zero through five Rebirth Spirits (`10104`) or Reborn Harpyias (`10098`)
  are accepted. Reborn Harpyia additionally requires a bound pet when its
  quantity is positive.
- A fresh zero-item modal sends material `0`; after the final selected item is
  removed, the native modal can retain `10104` or `10098` while sending count
  zero. Those are the only accepted count-zero shapes and none consumes an
  item.
- The active summoned pet is resolved by the server. Level, Soul Contract,
  remaining attempts, inventory stacks, stat revisions, RNG, and the complete
  result are never trusted from the client.
- A successful response `10273` is exactly 16 bytes. Offsets 4-9 are six
  unsigned integer-hundredth Growth increases in Agility, Strength, Accuracy,
  Technique, Wisdom, and Luck order; offsets 10-11 remain zero. The open
  Samsara modal copies those six bytes into its existing result view and
  changes from confirmation state 1 to result state 2. Offset 12 is the
  little-endian next-level requirement, so the server sends the reset level-1
  cost (`1,500`), not the current EXP pool. The packet is additive and is sent
  only for the first committed execution; a legacy v1 duplicate without exact
  roll evidence never replays it. The narrow targeted `10286` that
  follows carries both the authoritative current EXP pool and next-level
  requirement, then the server refreshes the bag.
- A retry replays the durable receipt but never repeats `10273` or destructive
  owned-list opcode `10237`. If the historical pet still exists, current
  authoritative values are sent with targeted `10286`; pet selection and
  presence are never rebuilt after a delayed retry.
- Migration `20260811_077_pet_durable_evidence_v3` admits the `pet_rebirth`
  command/audit/outbox family. Pet, all six stat rows, cross-stack inventory
  consumption, ledger evidence, inbox receipt, audit, and outboxes commit in
  one PostgreSQL transaction.

## Materials

The player may select zero through five units from one stock template:

- Rebirth Spirit (`10104`); or
- Reborn Harpyia (`10098`), the restricted equivalent accepted only for a
  bound pet when at least one is used.

Rebirth-attempt items are tiered separately:

| Rebirths enabled | Item | ID |
|---:|---|---:|
| 1-30 | Spring Water | 10107 |
| 31-60 | Juice of Rebirth | 10145 |
| 61-100 | Ambrosia of Rebirth | 11095 |

The installed client's instructions describe rebirth water as increasing the
available rebirth count. Rebirth-water activation is durable and
server-authoritative; the Pet Manager utility routes for Growth check, Seal,
unseal, charm claims, and Gender change are likewise wired to their pinned
stock request/result shapes.

## Growth-acceleration roll

Each of the six attributes is rolled independently to hundredth precision.
Installed-client `Pet_Alter.xml` and native preview math prove these inclusive
bounds:

| Spirits | Increase per attribute |
|---:|---:|
| 0 | 0.01-0.20 |
| 1 | 0.02-0.20 |
| 2 | 0.04-0.20 |
| 3 | 0.06-0.20 |
| 4 | 0.08-0.20 |
| 5 | 0.10-0.20 |

The client proves the bounds and hundredth rounding, but not the stock
server's RNG distribution. The authoritative server uses an inclusive uniform
integer-hundredth roll as the conservative compatibility policy. A
client-supplied stat result is never authoritative.

Each committed roll is added to the attribute's durable
`growth_acceleration`; it is not added to Basic. The server then resets the pet
to level 1 and publishes Basic plus the newly recomputed cumulative Added
vector. Owner Merge consequently uses the post-Rebirth `Basic + Added` total.

Phoenix reset redraws two independent server-authoritative vectors. First it
rolls the pet nature's base Growth vector. It then rolls one completed-Rebirth
modifier per attribute in the inclusive range
`0.10 * completed_rebirths` through `0.20 * completed_rebirths`, at hundredth
precision. The proposed effective rate is their sum. Consequently a nature
support of `14.00-16.00` and five completed Rebirths has an effective support
of `14.50-17.00` per attribute. These endpoints describe the support; the sum
of the two independent rolls is not a uniform distribution across it.

The comparison page shows proposed `new base + new modifier` against current
`base_growth_rate + growth_acceleration`, adding each modifier exactly once.
Accept replaces both durable vectors with the previewed base and modifier and
recomputes level-scaled Added Value. Cancel leaves both unchanged. A later
Rebirth adds its actual spirit-dependent roll to the accepted modifier; a
later Phoenix redraw replaces the modifier from the then-current completed
count. Thus a low-spirit Rebirth may temporarily leave an attribute below the
next count-derived Phoenix minimum until Phoenix is used again.

Migration `20260813_091_pet_phoenix_rebirth_bracket` versions pending previews.
Pre-migration previews retain legacy `new base + old acceleration` behavior;
new previews carry the nature roll, count, and exact six modifiers explicitly.

## Pet level and EXP evidence

Pet level 120 is confirmed as the terminal level by the installed client:

- `Localization/en_us/Settings/Sys/Pet_Alter.xml`;
- the level-120 rejection text in `Message.dat`;
- the rebirth instructions in `LuaText.lua`; and
- captured original-server level-120 pet records, whose next-level EXP is
  zero.

The complete original level 1-120 EXP table was recovered and independently
validated against the installed client and captured next-level denominators:

| Pet level | EXP needed for next level |
|---:|---:|
| 1 | 1,500 |
| 21 | 575,025 |
| 107 | 4,419,900 |
| 120 | 0 (maximum level) |

The 119 transition costs total exactly `252,947,820`. The authoritative
table, native `10285`/`10286` level-up exchange, overflow behavior, and source
evidence are recorded in `pet-level-progression.md`.
