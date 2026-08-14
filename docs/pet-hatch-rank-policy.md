# Pet hatch-rank policy

Status: implemented server policy. The probability table is a project-authored
extension approved on 2026-08-12; the installed stock client does not contain a
hatch probability table.

## Ownership and versioning

- PostgreSQL immutable pet-content revisions own the outcomes and weights.
- The process loads and validates one sealed revision at startup.
- The full ordered table contributes to the pet-content SHA-256 fingerprint.
- A hatch receipt, command audit, inbox result, and outbox event retain the
  selected rank, the 0-99 roll, outcome order, and source content revision.
- Existing pets are not backfilled or rerolled. Their current rank is preserved.

## Approved brackets

Every row uses low/middle/high probabilities of 60% / 30% / 10%.

| Aptitude pair | Low (60%) | Middle (30%) | High (10%) |
|---|---:|---:|---:|
| Weak / Fool | 0.00 | 0.30 | 0.40 |
| Cowish / Moderate | 0.30 | 0.40 | 0.80 |
| Rational / Calm | 0.40 | 0.80 | 1.00 |
| Grumpy / Brave | 0.80 | 1.00 | 1.50 |
| Zealous / Smart | 1.00 | 1.50 | 2.00 |
| Overbearing / Ferocious | 1.50 | 2.00 | 2.70 |
| Almighty / Godly | 2.00 | 2.70 | 3.00 |
| Celestial / Transcendent | 2.70 | 3.00 | 3.60 |

## Roll semantics

The entropy boundary supplies one integer from 0 through 99. Content resolves
it deterministically:

- 0-59: low outcome
- 60-89: middle outcome
- 90-99: high outcome

The generated birth rank is persisted as both the pet's initial current rank
and immutable hatch evidence. Later merge operations may change current rank,
but must not rewrite the birth evidence.

The authoritative wire-safe maximum pet rank is `655.35`. Hatch and merge
policies share that database-owned cap. Individual skill bonus tables saturate
at their last authored threshold instead of treating it as the pet-rank cap.

## Failure and compatibility rules

- Invalid or incomplete content fails startup before gameplay becomes ready.
- An invalid injected roll fails the hatch transaction before the egg is
  consumed.
- Legacy pets retain null hatch evidence and their existing rank unchanged.
- New hatches always write complete evidence in the same PostgreSQL transaction
  that consumes the egg and inserts the pet.
- New bag-activation receipts use contract version 2. Historical version 1
  hatch receipts remain replay-decodable without inventing missing evidence.
- Audit, inbox, and outbox evidence is not owned by the pet row, so it remains
  available after a deputy pet is consumed by a later merge.

## Deployment order

Receipt version 2 and pet-content contract V7 are a maintenance-boundary
change, not a mixed-writer rolling deployment:

1. Stop admission and drain active gameplay commands and outbox work.
2. Back up PostgreSQL and inspect legacy pet ranks. Migration 081 deliberately
   fails when a stored rank is above `655.35` or has more than two decimal
   places; reconcile such rows explicitly before retrying.
3. Apply migration 081 while writers remain stopped. Valid legacy ranks are
   preserved and their new hatch-evidence columns remain null.
4. Publish the sealed V7 pet-content revision and deploy only binaries that
   understand the V7 fingerprint and bag-activation receipt version 2.
5. Verify every process reports the same content fingerprint, then restore
   admission.

Version 1 hatch receipts are supported for replay during the transition, but
an old process must not resume as a writer after V7 is published. Rollback also
requires a drain: stop admission, retain migration 081 and its data, deploy the
last binary that can read V7/v2, verify fingerprints, and only then reopen.
