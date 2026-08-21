# Pet owner-Merge balance

Owner Merge (the stock Unite operation) is server-authoritative. The client
chooses the action only; it never supplies a pet ID, Savvy value, rate, or
resulting character stat.

The raw input for each attribute is current Basic Savvy (`initial_savvy`) plus
cumulative Added Value (`added_savvy`). Basic is the immutable hatch allocation
plus pet-to-pet Merge gains. Added is `effective Growth Rate * current pet
level`, where effective Growth Rate is `base_growth_rate +
growth_acceleration`. Owner Merge then adds the pet's one current Soul Contract
bonus (+3 through +8 per attribute) before applying the Unite curves. Raw
Growth Rate is not added again, the immutable hatch provenance is not counted
a second time, and a replacement Soul Contract never stacks.

This boundary follows installed `Origin.exe`: effective-Savvy routine
`0x006A0790` adds the stage-derived value from `0x006A1E30`, and the stock
Unite calculator calls it at `0x006ACF74`. Ordinary pet-to-pet Merge remains
different and ignores Soul Contract, matching the Pet Manager guide.

## Current reviewed policy

Policy `project-pet-unite-piecewise-marginal-v4` uses continuous marginal
bands. A rate applies only to Savvy inside its band, so every result remains
continuous and increasing at a boundary.

| Savvy inside band | Effectiveness of the first-band rate |
|---|---:|
| `0-60` | `100%` |
| `60-150` | `85%` |
| `150-300` | `70%` |
| `300-600` | `60%` |
| `600+` | `50%` |

This is a modest adjustment to the earlier project curve, not a flat scaling
rule. High Savvy still has diminishing returns, but every additional point on
an enabled mapping always contributes.

The fixed bases and first-band rates are:

| Savvy source | Character effect | Fixed base | First-band rate per Savvy |
|---|---|---:|---:|
| Agility | Maximum MP | 300 | 4.00 |
| Agility | Magical Attack | 80 | 2.00 |
| Agility | Damage Rebound (disabled) | 150 | 0.00 |
| Agility | Hit Rate | 20 | 0.12 |
| Strength | Maximum HP | 4,000 | 10.00 |
| Strength | Physical Defense | 80 | 2.00 |
| Strength | Fixed HP Recovery on Hit | 100 | 5.00 |
| Accuracy | Hit Rate | 20 | 0.48 |
| Accuracy | Physical Attack | 100 | 3.00 |
| Accuracy | Magical Defense | 60 | 1.50 |
| Technique | Dodge Rate | 10 | 0.50 |
| Technique | Fixed Physical Damage Cancellation | 600 | 12.00 |
| Technique | Fixed Magical Damage Cancellation | 480 | 10.00 |
| Wisdom | Maximum HP | 4,000 | 40.00 |
| Wisdom | Fixed Physical Append Damage | 200 | 5.00 |
| Wisdom | Fixed Critical-Damage Cancellation | 800 | 15.00 |
| Luck | Flat All-Damage Absorption | 80 | 1.50 |
| Luck | Fixed Magical Append Damage | 150 | 4.00 |
| Luck | Fixed Damage Rebound | 150 | 6.00 |

A fixed base is added once per resulting effect, even when two Savvy sources
feed the same effect. The five Agility-to-Rebound rows remain as zero-rate
compatibility placeholders; only Luck Savvy contributes variable Damage
Rebound.

The V4 policy preserves native effects `10`, `23`, `24`, `29`, `30`, `32`,
`34`, and `38` as fixed-value channels. In particular, effects `29` and `30`
are not percentages: their entire fixed output (base and every marginal band)
is exactly twice the reviewed V3 output. Effect `34` restores its fixed HP
amount once for each committed direct hit, capped by missing HP; replayed or
Rebound damage cannot trigger it.

Reborn adds a separate percentage policy from the same effective Technique
used by Unite (`Basic + Added + Soul Contract`):

```text
typed reduction bp = min(3000, round-away(effective Technique * 0.15))
```

The physical and magical results are equal and independently project into the
typed percentage-reduction fields. Combat still applies the global 8,000 bp
(80%) reduction cap after all sources are summed. The percentage policy never
reuses or changes the native meaning of effects `29` and `30`.

## PostgreSQL authority

The active balance is an immutable, SHA-256-addressed publication:

- `pet_owner_merge_content_revisions` stores revision headers.
- `pet_owner_merge_effect_types` and `pet_owner_merge_savvy_types` provide
  stable codes and web-friendly names.
- `pet_owner_merge_effect_bases` stores the 16 fixed bases.
- `pet_owner_merge_savvy_bands` stores the five contiguous bands.
- `pet_owner_merge_rates` stores all 95 typed rates (19 mappings by 5 bands).
- `pet_owner_merge_content_publication` selects the one official revision.
- `published_pet_owner_merge_balance` is the read model intended for an admin
  UI and balance inspection.

Database triggers reject incomplete publications, unsupported mappings,
gapped bands, increasing later-band rates, mutation of sealed content, and
deletion of the official publication. Zero is allowed for a disabled curve or
a later marginal rate, but valuable character state is never stored in these
content tables.

`character_pet_character_bonuses` is a derived materialization. Every row is
stamped with `balance_revision`. An active Merge has the 16 native rows plus
server-only codes `1001` (`TechniquePhysicalReduction`) and `1002`
(`TechniqueMagicReduction`). Those internal codes are valid only in derived
storage and the runtime projection; they are never serialized into the native
16-field PetUnite payload or included in the native content counts. Startup
rebuilds missing, stale-revision, or malformed active Merge rows from the
authoritative effective Savvy and process-pinned publication before gameplay
listeners open.

## Future web-admin workflow

Published rows must never be edited in place. A future admin application
should:

1. Build and validate a complete draft containing 16 bases, 5 bands, and 95
   rates.
2. Compute the canonical revision using the same server revision-hash
   contract.
3. Insert the header and definitions in one database transaction.
4. Move the publication pointer to that revision; database guards seal it.
5. Drain and restart game workers so all workers pin the same revision.
6. Verify the runtime-content fingerprint and rebuilt active-bonus count.

Balance publication is intentionally not a live hot-reload. This keeps one
calculation revision per process and prevents two workers from silently using
different owner-Merge math. Rollback means republishing a previously sealed
revision and performing the same controlled worker restart.

## Login energy authority

Every ordinary player login refills the one authoritative carried pet to its
durable `maximum_energy` before `OwnedPetList` and the first `PetEnergy` packet
are built. The refill reuses the owner-Merge lifecycle transaction: it locks
the carried row, validates the current player-ownership fence, advances the
pet revision only when energy changes, and projects that committed result into
the login snapshot. The first client energy value is therefore the stored
100%, not a UI-only replacement for a partial database value.

The refill runs after stale owner-Merge recovery. It rejects multiple carried
pets, does nothing when no pet is carried, and never touches exact pinned
training dummies. Online recharge restores five normalized points at every
six-second heartbeat. Merge drain, session shutdown, and their existing
generation/cancellation rules remain unchanged.
