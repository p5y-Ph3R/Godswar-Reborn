## Client-derived rules

### Owner merge

The original client requires:

- a summoned pet;
- the Merge talent;
- full pet energy;
- at least 40 amity;
- a pet that is not already merged.

The Merge action is a toggle: a second opcode-10274 action authoritatively
ends the active Merge. Other pet actions remain blocked while merged. The
runtime energy cadence and server-authoritative expiry policy are documented
with the implemented lifecycle.

All owner-Merge contribution calculations use each attribute's current
player-visible total: `Basic Savvy + cumulative Added Value`. Basic contains
the hatch allocation plus pet-to-pet Merge gains. Added is `current level *
(base_growth_rate + growth_acceleration)`; raw Growth Rate is never counted a
second time.

`Pet_Alter.xml` exposes the 16 contribution effect IDs and their six-savvy
curves. The numeric tables are authoritative evidence, but the native
interpolation and rounding function has not yet been recovered. The server
stores normalized fixed-point decimal contributions so that a later verified
calculator can replace the preview calculation without changing persistence.

### Pet merge

- Both primary and secondary pets must be level 30 or higher.
- They must be different pets owned by the same character.
- The primary pet must be summoned.
- The primary pet survives; the secondary pet is sacrificed on success.
- Merged Spirit (`10103`) improves the result.
- Fused Harpyia (`10097`) is the restricted equivalent accepted only when the
  primary pet is bound.
- At most five standard and restricted merge spirits may be used in total.
- The operation improves the primary pet's rank and six Basic Savvy values.
- Basic Savvy is the hatch allocation plus committed pet-to-pet Merge gains.
  Pet merge does not change base Growth Rate, growth acceleration, or the
  level-derived cumulative Added Value.
- A locked, dispatched, sealed, or already-consumed pet is not eligible.

#### Client-compatible Savvy result

Pet-to-pet Merge evaluates and rolls all six Savvy attributes independently in
fixed hundredths. The client displays a preview, but it never supplies an
authoritative roll or result. For each attribute the server computes:

`q = floor(deputy Added hundredths / 5) - primary Basic hundredths + deputy Basic hundredths`

It resolves the greatest `Pet_Alter.xml` `Restrict` value not above `q`, takes
that row's `Values`, and multiplies it by the deputy species factor: `0.8` for
species 2/3/6/10, `1.4` for 1/7, and `2.6` for every other stock species. The
product is truncated to an integer hundredth and becomes the maximum gain.
Values below the first lookup row produce a zero gain for that attribute.

The historical test case is reproduced exactly. With deputy Added `447.44`,
its contribution is `floor(44744 / 5) = 8948`. Agility produces
`8948 - 12289 + 4403 = 1062`, Accuracy produces `2348`, and both saturate at
lookup value `300`; factor `2.6` gives `7.80`. Luck produces `-635`, resolving
the `Restrict=-656, Values=162` row; `162 * 2.6` truncates to `4.21`.

No spirit uses `0.01..maximum` for an eligible attribute. One through five
Merge Spirits set the inclusive minimum to the nearest hundredth, halves up,
of 10/20/30/40/50% of that maximum; the maximum remains unchanged.

| Spirits | A `7.80` maximum | A `4.21` maximum |
| ---: | ---: | ---: |
| 0 | `0.01-7.80` | `0.01-4.21` |
| 1 | `0.78-7.80` | `0.42-4.21` |
| 2 | `1.56-7.80` | `0.84-4.21` |
| 3 | `2.34-7.80` | `1.26-4.21` |
| 4 | `3.12-7.80` | `1.68-4.21` |
| 5 | `3.90-7.80` | `2.11-4.21` |

The server rolls uniformly over each inclusive integer-hundredth interval and
persists every input, lookup row, bound, and draw with the Merge transaction.
This replaces the former aptitude-wide project bands.

The same formula yields the exact remaining-Savvy requirement. Species
2/3/6/10 need `q >= -39.90`; other configured species need `q >= -40.00`.
Here Added is cumulative Added Value, not raw Growth Rate, and only
`floor(Added hundredths / 5)` contributes. Full `Basic + Added` is not the
Merge comparison.

For every failed row, the native calculation passes its live, species-aware
shortfall to Lua and the widened dialog displays `Need N more`. The value is
`threshold - q`, where `threshold` is `-3990` or `-4000` hundredths as
described above. For example, a primary Strength Basic of `157.82` and a Rock
Elf deputy with `22.05` Basic and `104.38` Added gives an effective deputy
value of `22.05 + floor(104.38 / 5) = 42.92`; the exact target is `117.82`, so
the row shows `Need 74.90 more`. The narrow opcode-10286 Basic/Added refresh
also recomputes the Merge dialog while it is hidden behind the Fairy Feather
modal, so the shortfall changes immediately after every redistribution.

The stock action handler sends Merge without aggregating these six preview
rows, so an ineligible row becomes an authoritative zero delta and does not
reject the whole operation. A client-sent rank, Savvy gain, or success result
is never accepted as authoritative.

The reversible resource and native patches update the byte-identical `en_us`
and `zh_cn` `PetInosculateUI.xml`/`.lua` pairs plus the audited callback bridge
through exact known-state hashes. They stage and verify replacements, retain
verified backups, and restore the exact predecessor on any failure.
The generic message catalog also references a deputy-quality restriction and
a 30-level EXP-gap restriction. Their exact native comparison semantics are
not yet proven, so the planner does not pretend to implement them.

#### Native durable Merge protocol

The implemented client request is C2S opcode `10268`, exactly 20 bytes:
primary pet ID at `+4`, deputy pet ID at `+8`, selected material **template**
ID at `+12`, quantity at `+16`, and three zero reserved bytes. No-spirit Merge
is exactly `(material ID, quantity) = (0, 0)`; mixed zero/material forms are
rejected. Spirit Merge accepts an approved item with quantity 1 through 5. The server
authenticates both pets and inventory, derives all six rolls itself, and commits
the primary update, deputy deletion, material consumption, ledger, audit,
inbox, and outbox in one PostgreSQL transaction.

The native success response is S2C opcode `10269`, exactly 38 bytes. It carries
the primary/deputy IDs, six signed fixed-hundredths increments in Agility,
Strength, Accuracy, Technique, Wisdom, and Luck order, then a rank delta. This
packet is additive and is emitted only for a newly committed operation. An
operation-ID retry sends authoritative pet list `10237` plus the bag refresh
instead, preventing the client from adding the same gains twice.

#### Deferred hardening: bind preview intent to commit

Status: **backlog**, recorded 2026-08-13; not implemented.

The current request safely treats its pet IDs and material selection as the
player's intent. PostgreSQL then resolves and locks those exact owned pets and
the server derives and rolls the result. A modified or delayed client cannot
inject stats, species, bounds, or a roll, but it can replace the request with a
different eligible pair belonging to the same character. The resulting merge
is valid for the locked pair, yet the server cannot prove that this pair and
state are the ones shown by an earlier client preview.

Close that gap with a server-issued, short-lived, single-use preview token. The
token or its durable server record must bind at least:

- account and character identity;
- primary and deputy pet IDs and revisions;
- the six stat revisions or a canonical digest of every formula input;
- material template and quantity;
- pinned pet-content revision;
- ownership generation, issue time, expiry, and a cryptographically random
  token identifier.

Commit must reload and lock the authoritative rows, atomically claim the token,
and fail closed if any bound value changed, the token expired, or it was
already used by another operation. The random Savvy and rank draws must remain
server-only and occur only after successful token validation. A duplicate of
the same committed operation must replay its saved receipt; a different
operation or payload must never reuse the token or reroll. Persist only a token
digest plus its bound fields, displayed bounds, consumption outcome, and
rejection reason in audit evidence; never trust or persist a client-supplied
result as authoritative.

The stock 20-byte opcode `10268` has no room for this contract. Implementation
therefore needs an explicitly versioned secure-shim sidecar or a new preview /
commit opcode; it must not silently reinterpret existing fields.

Required negative integration coverage:

- swapped primary/deputy IDs, same ID, foreign-owned IDs, and token theft by a
  different account or character;
- changed pet, stat, species, presence, binding, material, ownership, or
  content revisions after preview;
- expired, fabricated, already-used, concurrent, and conflicting tokens;
- disconnect/lost-response replay proving one commit, one deputy consumption,
  one material consumption, and one saved roll;
- proof that invalid/stale token paths perform no RNG draw or gameplay
  mutation.

Those six increments are applied to Basic Savvy. The cumulative Added vector
remains a level/rate-derived value and changes only when level, base Growth
Rate, or growth acceleration changes.

The recovered rank preview uses signed fixed hundredths. It subtracts the
primary rank from the deputy rank, resolves the greatest `Qualityadd` threshold
not above that difference, and multiplies its `Values` result by the deputy
species factor (`0.8` for species 2/3/6/10, `1.4` for 1/7, and `2.6` for the
other stock species). The historical result uses the authored decimal factor,
so base `250` becomes `200`, `350`, or `650`. A guarded two-locale resource
correction compensates for the installed executable's binary32 underflow while
leaving the canonical factors unchanged. No spirit uses the single adjusted
rank result. One through five spirits set its inclusive lower bound to
10/20/30/40/50% and retain a 100% upper bound. The server uniformly rolls
within that interval, persists the inputs, bounds, draw, and result with the
primary update, and returns the exact delta in opcode `10269`. Differences below the first `Qualityadd` row
produce zero rank gain. Rank saturates at the native UInt16 hundredths ceiling
of `655.35`; it is not capped at `100.00`, because a stock client fixture
already displays rank `100.94`. Skill-effect tables may independently saturate
at their final threshold.

Migration `20260812_081_pet_rank_content` publishes the lookup, deputy-species
factors, and spirit bounds as immutable database content. Migration
`20260812_084_pet_merge_savvy_lookup_content` adds the separate 200-row Savvy
`Restrict` lookup and zero-spirit row in pet-content V8. Startup compares all
lookup rows, the exact 45-species factor map, and all six spirit bounds
against a compiled compatibility sentinel because the installed client's
preview hard-codes those values; an unreviewed divergent revision fails closed.
Runtime rolls still read the process-pinned database revision. An operation-ID
retry reuses its durable receipt and never commits or sends the additive delta
twice.
Migration `20260811_077_pet_durable_evidence_v3` exposes both
`pet_to_pet_merge` and `pet_rebirth` records through the durable pet evidence
view.

#### Deployment boundary

Migrations `081` and `084`, pet-content V8, hatch receipt V2, and the Merge
audit evidence are one maintenance-boundary release. Do not run old and new gameplay
writers together: stop admission, drain commands and outboxes, apply the
migrations, publish and verify the sealed V8 fingerprint, deploy only V8-aware
workers, and then reopen. Rollback likewise requires a drain and a binary that
can read V8 and the durable V2 receipts; the migrations and committed evidence
must not be removed. The detailed shared sequence is recorded in
[Pet hatch rank policy](pet-hatch-rank-policy.md#deployment-order).

### Rebirth

The rebirth level gates, material tiers, zero-through-five spirit balance,
growth-acceleration ranges, level cap, and EXP evidence are maintained in
[Pet rebirth balance](pet-rebirth-balance.md). Rebirth preserves the immutable
quality-derived hatch Basic plus pet-to-pet Merge gains, accumulates Growth
acceleration, resets level to 1, and recomputes cumulative Added as effective
Growth Rate times the resulting level.

### Soul Contract

- Contract Spirit (`10105`) may be inserted, maximum five.
- Client `Base_Alter` values for zero through five spirits are
  `300, 400, 500, 600, 700, 800`.
- A new contract replaces the previous contract result.
- The result is a fixed +3 through +8 on every displayed Savvy total. It is
  derived from the persisted stage and does not rewrite raw Basic or Added.
- The detailed stock-client pet-merge instructions explicitly say contract
  status has no effect on pet merge. An older generic merge rejection string
  conflicts with that instruction, so original-server packet capture remains
  the final compatibility check.
- Rebirth does require a contract: `PetCodeReturn114` explicitly rejects a
  rebirth when the pet has not signed one, and both Pet Manager NPC
  descriptions state that the contract enables rebirth.
- The exact durable/wire contract is recorded in
  [Pet Soul Contract](pet-soul-contract.md).

[Pet system foundation](pet-system-foundation.md)
