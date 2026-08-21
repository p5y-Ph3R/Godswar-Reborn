# Cooled Holy Stone balance authority

Percentage values use hundredths of one percentage point. For example, `550`
is `5.50%` and `600` is `6.00%`.

The mutable singleton row in `holy_spirit_balance_settings` is the production
authority for the three adjustable Cooled Holy Stone maxima. Its initial
values are:

| Effect | Channel | Grade-10 maximum |
|---:|---|---:|
| 9 | Physical damage reduction | 5.50% |
| 10 | Magic damage reduction | 5.50% |
| 13 | Critical-damage reduction | 6.00% |

Effects 11, 12, and 14 remain physical, magic, and critical flat reduction.
Their Grade-10 maxima remain `400`, `350`, and `1000`; they are applied after
their corresponding percentage channel and are not percentage points.

## Startup pinning and management updates

Each worker loads and validates exactly one balance row before it becomes
ready. The pinned values are injected into both new Holy Spirit rolls and the
character combat projection. Operators must drain gameplay, commit the edit,
and perform a coordinated restart of every realm worker. This prevents two
paths or two workers from using different revisions during one rollout.

The row includes `revision`, `updated_at`, and `updated_by`. Future management
writes use one transaction and a compare-and-swap update guarded by
`expectedRevision`; the update trigger advances the revision and timestamp.
Within that same transaction, all four persisted socket ordinals are clamped
to the newly committed maxima. An adjustable `NULL` is materialized at the
new maximum when its client-visible legacy fallback would exceed that maximum;
this keeps raw/wire and combat values aligned. Non-adjustable `NULL` values are
untouched. A revision conflict changes neither settings nor sockets. Allowed
Grade-1 maxima are `22..80` for physical and magic reduction and `28..70` for
critical reduction.

Lowering a maximum is intentionally irreversible for existing rolls because
their raw values are clamped. Raising a maximum does not inflate historical
rolls; it only expands the range available to future implementations after the
coordinated worker restart.

## Forward migrations and existing sockets

Migration `20260821_098_cooled_holy_stone_balance` is sealed history. It moved
explicit effect 9 or 10 rolls at the former `55 * grade` maximum to
`80 * grade`. Its rendered SQL and checksum must never change.

Migration `20260821_099_holy_spirit_balance_settings` creates and seeds the
mutable singleton, then clamps every explicit effect 9 or 10 socket value above
`55 * grade` to that cap and every explicit effect 13 value above `60 * grade`
to that cap. It also materializes adjustable `NULL` values at those caps when
their legacy fallback would otherwise serialize above the effective combat
value. All four socket ordinals are covered. Lower explicit values and
non-adjustable `NULL` or flat effects are preserved.

Compiled `80/80/70` bounds remain only as immutable historical acceptance
envelopes. They allow old command receipts and detached stones to replay after
the live cap is lowered. Combat and newly implemented Cooled stones always use
the startup-pinned PostgreSQL maxima.
