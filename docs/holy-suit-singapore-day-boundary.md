# Holy Suit Singapore day boundary

Holy Suit daily EXP storage uses the realm-local calendar day published in
the pinned Holy Suit operation policy. Tempest's alpha policy is
`Asia/Singapore`, so a new quota day begins at **00:00 Singapore time
(UTC+08:00)**. Audit timestamps remain UTC `timestamptz` values; only the
quota bucket key is realm-local.

The PostgreSQL executor resolves the day from the pinned IANA time-zone name.
It locks that exact `usage_day` and carries the key through the transaction.
The final quota update does not recalculate the date, so an operation that
starts immediately before midnight cannot debit a different day's row after
midnight.

Migration `20260802_049_holy_suit_singapore_day_boundary` changes only the
database documentation. Existing UTC-keyed rows are retained exactly as
historical records. They cannot be moved safely because one UTC bucket can
contain operations from two different Singapore calendar days, and the
aggregate row cannot be split without guessing.

After the policy cutover, the executor creates and reads only the current
Singapore key. Historical command audits keep their original UTC timestamps.
For the live cutover, test2's 89,000,000 EXP operation occurred at 14:07 UTC
on 2026-08-01 (22:07 Singapore time), so it correctly remains on the prior
Singapore day and is not charged to 2026-08-02.
