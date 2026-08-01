# B20H observation and final-removal gate

Status: local gate completed and Redis-coordinated local-alpha observation
started on 2026-08-01; deployment evidence and final compatibility deletion
remain pending

## Outcome

B20H now has a machine-checkable, fail-closed authorization boundary. The
repository includes:

- `tools/TestB20RetirementEvidence.ps1` and its strict-JSON helper, which
  validate the complete observation and recovery record and include negative
  self-tests;
- `docs/operations/b20h-retirement-evidence.example.json`, a deliberately
  non-authorizing draft template; and
- `docs/operations/b20h-legacy-retirement-runbook.md`, which defines
  collection, validation, deletion, and rollback procedure.

The CI workflow runs the validator self-test. It does not accept the example
record and it does not infer approval from a silent metric.

## Authorization requirements

The validator requires all of the following:

- prior release-owner approval;
- at least 168 continuous hours;
- every expected replica listed once with full-window coverage;
- observer readiness continuously equal to one;
- zero legacy-invocation counter delta and zero unaccounted counter resets;
- no telemetry scrape gap above 300 seconds;
- coverage of authentication/loading, open world, inventory/economy,
  progression, pets/mounts, transfers, and scheduled world events; and
- passed, checksummed evidence for B19 reconciliation, backup/restore, clean
  install, upgrade install, prior-binary rollback, and archive parity.

The seven-day minimum is intentional: it spans weekday/weekend behavior and
multiple twice-daily world-boss cycles. A restarted or replaced process must
remain represented; replacing a replica cannot erase its observed counter.

## Current boundary

`B20LegacyPersistenceBaseline.RetirementComplete` remains `false`. The broad
compatibility calls and retained rollback schema are not deleted in this
change because the repository does not contain a production metrics backend,
a deployed replica inventory, a pre-approved observation, or a seven-day
workload campaign. Claiming those facts from local tests would be false.

Once a real record passes, the final deletion is a separate reviewed commit:
remove broad compatibility call sites and facades, end the prior-binary schema
compatibility window, add forward-only drop migrations where approved, set
the ratchet to complete/empty, and repeat the complete PostgreSQL and recovery
gate set.

## Active local rehearsal

The current local campaign runs the observed server with the `Redis`
coordination provider and 23 required routes. Its pinned source commit is
`db6565382693f84b25b94c8790afbb91c9c39697`, T0 is
`2026-08-01T07:08:36.9520131Z`, and its target end is
`2026-08-08T07:08:36.9520131Z`. The detailed status and invalidated predecessor
runs are recorded in
`docs/data-architecture-b20h-redis-observation-20260801.md`.

This campaign is intentionally classified as `local-alpha-rehearsal` and is
not eligible to authorize final retirement by itself.

## Local verification

```powershell
./tools/TestB20RetirementEvidence.ps1 -SelfTest
```

The self-test accepts one valid synthetic record and rejects short or future
windows, loose timestamps, non-Boolean approval, fractional counters, missing
replicas/readiness/workloads, nonzero invocation deltas, counter resets,
excessive scrape gaps, failed gates, duplicate JSON properties, and malformed
checksums. It also rejects artifact path escape, reparse points, and tampering.
The checked-in draft example is separately confirmed to fail.

No live database, player data, running server, container, metric backend, or
deployment was modified while establishing this gate.
