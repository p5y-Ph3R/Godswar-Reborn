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

Policy `project-pet-unite-piecewise-marginal-v2` uses continuous marginal
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
rule. High Savvy still has diminishing returns, but every additional point
always contributes.

The fixed bases and first-band rates are:

| Savvy source | Character effect | Fixed base | First-band rate per Savvy |
|---|---|---:|---:|
| Agility | Maximum MP | 300 | 4.00 |
| Agility | Magical Attack | 80 | 2.00 |
| Agility | Damage Rebound | 150 | 1.50 |
| Agility | Hit Rate | 20 | 0.12 |
| Strength | Maximum HP | 4,000 | 10.00 |
| Strength | Physical Defense | 80 | 2.00 |
| Strength | Life Absorption | 100 | 5.00 |
| Accuracy | Hit Rate | 20 | 0.48 |
| Accuracy | Physical Attack | 100 | 3.00 |
| Accuracy | Magical Defense | 60 | 1.50 |
| Technique | Dodge Rate | 10 | 0.50 |
| Technique | Physical Damage Reduction | 300 | 6.00 |
| Technique | Magical Damage Reduction | 240 | 5.00 |
| Wisdom | Maximum HP | 4,000 | 40.00 |
| Wisdom | Physical Damage Increase | 200 | 5.00 |
| Wisdom | Critical Damage Reduction | 800 | 15.00 |
| Luck | Damage Absorption | 80 | 1.50 |
| Luck | Magical Damage Increase | 150 | 4.00 |
| Luck | Damage Rebound | 150 | 6.00 |

A fixed base is added once per resulting effect, even when two Savvy sources
feed the same effect.

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
deletion of the official publication. Zero is allowed for a later marginal
rate, but valuable character state is never stored in these content tables.

`character_pet_character_bonuses` is a derived materialization. Every row is
stamped with `balance_revision`. Startup rebuilds stale active Merge rows from
the authoritative `Basic + cumulative Added` totals and the process-pinned
publication before gameplay listeners open.

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
