# B20H legacy-retirement runbook

Status: repository gate implemented on 2026-08-01. Final deletion remains
blocked until a real deployment produces approved evidence. A local test run,
an empty metric graph, or this example document is not that evidence.

## Safety rule

Do not set `B20LegacyPersistenceBaseline.RetirementComplete` to `true`, delete
the broad compatibility store, or drop compatibility schema objects until
`tools/TestB20RetirementEvidence.ps1` accepts a newly collected record. The
example record is deliberately `draft` and deliberately contains invalid
checksum placeholders so it cannot authorize retirement by accident.

The observation must cover every process serving the realm for seven
continuous days. Seven days covers weekday/weekend progression rules and
multiple twice-daily world-boss cycles. If battlefields or scheduled dungeons
are enabled during the release, exercise them inside the same window. A
release owner must approve the window before it starts.

## Before the window

1. Deploy the B20E-G release without deleting compatibility code or views.
2. Record the exact expected replica set. Include replacement process
   incarnations; do not silently discard a restarted process's counter.
3. Confirm every replica exports
   `godswar_legacy_persistence_observer_ready == 1` and the cumulative
   `godswar_legacy_persistence_invocations_total` counter.
4. Configure scrape-gap alerting at 300 seconds or less. Counter resets and
   missing readiness fail the window; start a new window after fixing them.
5. Copy
   `docs/operations/b20h-retirement-evidence.example.json` to an external,
   access-controlled evidence location. Do not commit the completed record.
6. Record the approved change identifier, approving role, start time, minimum
   168-hour duration, and expected replica count.

Metric labels must remain finite. Do not add account IDs, character IDs,
session IDs, IP addresses, usernames, or raw client values to evidence or
metric labels.

## Required workload coverage

The window is invalid unless ordinary users or an approved bounded test
campaign exercise:

- authentication, character selection, and character loading;
- open-world movement and combat;
- inventory and economy mutations, including equipment and forging paths;
- progression, rewards, talents, and Zodiac paths;
- pets and mounts;
- map and zone transfer; and
- scheduled world events, including at least two world-boss cycles.

Record only pass/fail coverage and a protected external reference. Do not put
player data or packet captures in the retirement record.

## Required gates

After the zero-use window, produce immutable evidence and SHA-256 receipts for
exactly these gates:

1. `b19_reconciliation`: a complete, non-truncated report with no findings;
2. `backup_restore`: the isolated B19 logical backup/restore drill;
3. `clean_install`: all migrations and publications on an empty database;
4. `upgrade_install`: the supported historical schema upgraded in place;
5. `prior_binary_rollback`: the declared prior release starts against the
   still-compatible schema and can be rolled forward again; and
6. `archive_parity`: archived JSON/test fixtures and publication baselines
   reproduce the reviewed authoritative content without becoming runtime
   fallbacks.

Use the B19 runbook for reconciliation and isolated restore. Never aim its
disposable harness at a shared or live database. Execute install and rollback
tests on disposable clones or an approved staging environment.

## Validate the record

Run the validator's negative tests in CI and before evaluating evidence:

```powershell
./tools/TestB20RetirementEvidence.ps1 -SelfTest
```

Then validate the completed external record:

```powershell
./tools/TestB20RetirementEvidence.ps1 `
  -EvidencePath 'C:\approved-evidence\b20h-retirement.json'
```

Keep each gate's referenced evidence file under the same protected evidence
directory and use a relative path that cannot escape it. The validator
recomputes every artifact's SHA-256, rejects duplicate references, and bounds
each artifact at 128 MiB.

Success returns JSON with `RetirementAuthorized: true`. The validator rejects
short/future windows, missing or duplicate replicas, observer gaps, legacy
invocations, counter resets, missing workloads, failed gates, and invalid
or mismatched checksums. Protecting the evidence directory and its approval
record remains an operator responsibility; a checksum is not an identity or
signature.

## Final deletion change

Only after validation succeeds:

1. Attach the validator output and immutable evidence references to the
   approved change.
2. Remove the remaining broad `IGameStore` compatibility call sites and their
   telemetry operations.
3. Delete the broad `PostgresGameStore` facade only after every focused
   adapter owns its connection and transaction boundary.
4. Audit every inbound foreign key before proposing a drop. In particular,
   `faction_area_experience_control.map_id` must no longer reference
   `world_boss_areas(map_id)`; rehome and validate that durable control state
   in a prior forward migration without cascade-loss risk.
5. Drop compatibility views/tables in a new forward-only migration only after
   the prior-binary rollback obligation has ended.
6. Set `RetirementComplete=true` and make the architecture baseline empty in
   the same change.
7. Re-run Release build, the full managed suite, B03 PostgreSQL gates, B19
   reconciliation/restore, clean/upgrade migration tests, and archive parity.

Rollback means redeploying the preserved compatibility release while the
compatible schema still exists. After a destructive schema migration, use a
forward repair or restore from the approved recovery point; do not improvise
reverse DDL on player data.

## Current limitation

This repository cannot truthfully complete the live observation itself. No
production metrics backend, deployed replica inventory, approved change
record, or seven-day workload window is available in source control. Until
those external facts exist, B20H is ready to operate but final legacy deletion
is intentionally not authorized.
