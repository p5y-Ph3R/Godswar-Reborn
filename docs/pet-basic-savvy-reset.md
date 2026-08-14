# Fairy's Feather Basic-Savvy reset

## Proven stock-client behavior

- Item `11000` is **Fairy's Feather**.
- Pet Manager dialogue `36`, action `116`, redistributes the six Basic
  (Savvy) values: Agility, Strength, Accuracy, Technique, Wisdom, and Luck.
- The stock client says the total Basic Savvy is preserved and randomly
  redistributed. It does not define probabilities, per-stat bounds, or an
  RNG algorithm.
- Result page `120` renders the six values committed by the Reset operation.
- The project client exposes only `Reset` on that result page. The stock
  `OK`/`Cancel` preview controls are intentionally retired.

The tier probabilities below are therefore project-authored balance, not a
claim about the original server.

## Authoritative redistribution policy

Policy version: `fairy-basic-savvy-v4`.

| Probability | Shape | Bounds |
| ---: | --- | --- |
| 1% | Extreme focus | One random stat receives 90-92%; the other five share the remainder closely |
| 4% | Strong focus | One random stat receives 82-86%; the other five share the remainder closely |
| 5% | Dual extreme | One random stat receives 53-57%, a second completes a rounded 90% focused total, and the other four share the approximately 10% remainder |
| 5% | Dual medium | Three distinct random stats receive 42-47%, 28-32%, and 17-21%; combinations must leave at least 5.1% for the other three |
| 10% | Dual focus | Two distinct random stats each receive 41-45%; the other four share the remainder closely |
| 25% | Trio | Three distinct random stats each receive 27-31%; the other three share the remainder closely |
| 30% | Quad | Four distinct random stats each receive 19-22%; the other two share the remainder closely |
| 20% | Ordinary | All six stats use the existing ordinary-random 5-30% bounds |

The first seven rows are the named special outcomes. The remaining 20% is the
ordinary redistribution path used whenever no special outcome is selected.

For Extreme focus, 90% leaves an average 2.00% for each remainder stat and
91% leaves 1.80%. A 92% focus leaves only 8% total, so an exactly even split
is 1.60% each; that mathematically necessary case is allowed even though the
usual requested remainder band is 1.7–2.2%.

Every residual group is randomized around an even split. Where the pet's
hundredth-point total makes it feasible, each recipient stays within 0.25
percentage point of the group's average and the group has at most a 0.50-point
spread. At low totals, where those percentage bounds cannot be represented in
whole hundredths, the allocator uses the tightest positive exact split.

All values use the native client's hundredth-point precision. Rounding is
distributed without creating or deleting Savvy, so the six proposed values
always sum to the pet's exact pre-roll Basic total. Focus stats are selected
uniformly, and every stated focus range is inclusive and randomized.

## Durable operation semantics

`Reset` is the single atomic commit boundary:

1. Lock the authoritative character, summoned pet, six stat rows, inventory,
   and first Fairy's Feather stack.
2. Generate the next vector using the pinned policy and verify its exact total.
3. Replace only the six current Basic values, advance the six stat revisions
   and pet revision, consume exactly one Fairy's Feather, and advance the
   inventory revision in the same PostgreSQL transaction.
4. Write immutable before/after, roll-policy, item-instance, inbox/outbox, and
   economy evidence.
5. Send the authoritative 68-byte pet refresh (`10286`) before page `120`.

Requests are idempotent. Replaying the same Reset operation identity returns
its stored receipt and cannot consume another feather or apply another roll.
A delayed replay after a newer Reset renders the freshly reloaded current pet
values, not the historical receipt values. Active owner Merge, missing items,
malformed frames, and lost ownership fail closed.

Old `OK` requests and old two-phase Preview/Accepted receipts remain readable
during rolling upgrades, but they cannot mutate the pet. They resolve to the
harmless preview-unavailable result (`129`) instead of disconnecting a client.
Migration 080's preview table and its pinned `fairy-basic-savvy-v1` constraint
are retained unused for schema compatibility. The current one-phase path never
inserts a row into that historical table; v4 and all applicable focus
identities are recorded in the reset audit.

## Persistence invariants

- `initial_savvy` is current Basic: immutable hatch Basic plus committed
  pet-to-pet Merge gains, redistributed as one conserved pool.
- `birth_initial_savvy` and `rarity_added_savvy` remain immutable hatch
  provenance and are not rewritten by Fairy's Feather.
- A pet has exactly six rows; the birth total equals its recorded hatch
  baseline; the current Basic total cannot be below that baseline.
- Growth Rate and level-scaled Added value are independent of this operation.
- An active owner-to-pet Merge blocks the reset, preventing stale character
  bonuses.

## Native request shapes

The request is the exact 92-byte little-endian NPC action frame (`10069`).

- Reset: nested path `36/100/116`, with remaining arguments `-1`.
- Legacy OK: the same path with the native confirm marker set to `0`; it is a
  non-mutating compatibility request returning result `129`.
- Cancel: no request and no server state.

Direct action-`116` forms are accepted only with the same exact padding rules
for compatibility. Extra fields, alternate padding, wrong dialogue IDs, and
truncated or oversized frames are rejected.
